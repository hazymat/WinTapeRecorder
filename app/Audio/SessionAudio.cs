using System.Buffers.Binary;
using System.IO;
using System.Text;
namespace WinTapeRecorder.Audio;

// Random-access reader for a session WAV (PCM 16/24/32 or float 32), as interleaved floats. Thread-safe.
public sealed class SessionAudio : IDisposable
{
    public int SampleRate { get; private set; }
    public int Channels { get; private set; }
    public int BitsPerSample { get; private set; }
    public bool IsFloat { get; private set; }
    public long TotalFrames { get; private set; }
    int BytesPerSample => BitsPerSample / 8;
    int BlockAlign => BytesPerSample * Channels;
    long dataOffset;
    readonly FileStream stream;
    readonly object readLock = new();
    byte[] readBuffer = Array.Empty<byte>();

    SessionAudio(string path) { stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16); }

    public static SessionAudio Open(string path)
    {
        var audio = new SessionAudio(path);
        try { audio.ReadHeader(); return audio; } catch { audio.Dispose(); throw; }
    }

    void ReadHeader()
    {
        var reader = new BinaryReader(stream, Encoding.ASCII, true);
        stream.Position = 0;
        if (new string(reader.ReadChars(4)) != "RIFF") throw new InvalidDataException("Not a WAV file.");
        reader.ReadUInt32();
        if (new string(reader.ReadChars(4)) != "WAVE") throw new InvalidDataException("Not a WAV file.");
        long dataLength = -1;
        while (stream.Position + 8 <= stream.Length)
        {
            var chunkId = new string(reader.ReadChars(4)); var chunkLength = reader.ReadUInt32(); var chunkStart = stream.Position;
            if (chunkId == "fmt ")
            {
                var formatTag = reader.ReadUInt16(); Channels = reader.ReadUInt16(); SampleRate = (int)reader.ReadUInt32(); reader.ReadUInt32(); reader.ReadUInt16(); BitsPerSample = reader.ReadUInt16();
                IsFloat = formatTag == 3;
                if (formatTag == 0xFFFE && chunkLength >= 40) { reader.ReadUInt16(); reader.ReadUInt16(); reader.ReadUInt32(); IsFloat = reader.ReadUInt16() == 3; } // extensible: subformat GUID starts with the format tag
            }
            else if (chunkId == "data") { dataOffset = chunkStart; dataLength = chunkLength; break; }
            stream.Position = chunkStart + chunkLength + (chunkLength & 1);
        }
        if (dataOffset == 0 || Channels == 0) throw new InvalidDataException("The WAV file has no audio.");
        // A recording cut short (crash, power cut) leaves a stale length: trust the file
        var available = stream.Length - dataOffset;
        if (dataLength < 0 || dataLength > available || dataLength == 0) dataLength = available;
        TotalFrames = dataLength / BlockAlign;
    }

    // Reads up to frameCount frames from firstFrame into buffer (interleaved), returns frames read
    public int Read(long firstFrame, int frameCount, float[] buffer)
    {
        if (firstFrame < 0) firstFrame = 0;
        frameCount = (int)Math.Max(0, Math.Min(frameCount, TotalFrames - firstFrame));
        if (frameCount == 0) return 0;
        var byteCount = frameCount * BlockAlign;
        lock (readLock)
        {
            if (readBuffer.Length < byteCount) readBuffer = new byte[byteCount];
            stream.Position = dataOffset + firstFrame * BlockAlign;
            var got = 0;
            while (got < byteCount) { var read = stream.Read(readBuffer, got, byteCount - got); if (read <= 0) break; got += read; }
            frameCount = got / BlockAlign;
            SampleConvert.ToFloat(readBuffer.AsSpan(0, frameCount * BlockAlign), buffer.AsSpan(0, frameCount * Channels), BitsPerSample, IsFloat);
        }
        return frameCount;
    }

    // Rewrites the RIFF and data lengths from the file size (after a crash mid-recording)
    public static void RepairHeader(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        var reader = new BinaryReader(file);
        if (file.Length < 44) return;
        file.Position = 12;
        int blockAlign = 0;
        while (file.Position + 8 <= file.Length)
        {
            var chunkId = Encoding.ASCII.GetString(reader.ReadBytes(4)); var chunkLength = reader.ReadUInt32(); var chunkStart = file.Position;
            if (chunkId == "fmt ") { file.Position = chunkStart + 12; blockAlign = reader.ReadUInt16(); }
            if (chunkId == "data")
            {
                var actual = file.Length - chunkStart;
                if (blockAlign > 0) actual -= actual % blockAlign;
                if (actual > uint.MaxValue - 64) actual = uint.MaxValue - 64;
                if (chunkLength == actual) return;
                var lengthBytes = new byte[4];
                BinaryPrimitives.WriteUInt32LittleEndian(lengthBytes, (uint)actual); file.Position = chunkStart - 4; file.Write(lengthBytes);
                BinaryPrimitives.WriteUInt32LittleEndian(lengthBytes, (uint)(chunkStart + actual - 8)); file.Position = 4; file.Write(lengthBytes);
                return;
            }
            file.Position = chunkStart + chunkLength + (chunkLength & 1);
        }
    }

    public void Dispose() => stream.Dispose();
}

public static class SampleConvert
{
    public static void ToFloat(ReadOnlySpan<byte> source, Span<float> target, int bitsPerSample, bool isFloat)
    {
        switch (bitsPerSample)
        {
            case 32 when isFloat: for (var index = 0; index < target.Length; index++) target[index] = BinaryPrimitives.ReadSingleLittleEndian(source[(index * 4)..]); break;
            case 32: for (var index = 0; index < target.Length; index++) target[index] = BinaryPrimitives.ReadInt32LittleEndian(source[(index * 4)..]) / 2147483648f; break;
            case 24: for (var index = 0; index < target.Length; index++) { var offset = index * 3; target[index] = ((source[offset] << 8 | source[offset + 1] << 16 | source[offset + 2] << 24) >> 8) / 8388608f; } break;
            case 16: for (var index = 0; index < target.Length; index++) target[index] = BinaryPrimitives.ReadInt16LittleEndian(source[(index * 2)..]) / 32768f; break;
            case 8: for (var index = 0; index < target.Length; index++) target[index] = (source[index] - 128) / 128f; break;
            default: throw new NotSupportedException($"{bitsPerSample}-bit audio isn't supported.");
        }
    }

    // Float to PCM (clamped) or float bytes, for writing. Scaling mirrors ToFloat, so integer sources round-trip exactly
    public static void FromFloat(ReadOnlySpan<float> source, Span<byte> target, int bitsPerSample)
    {
        switch (bitsPerSample)
        {
            case 32: for (var index = 0; index < source.Length; index++) BinaryPrimitives.WriteSingleLittleEndian(target[(index * 4)..], source[index]); break;
            case 24:
                for (var index = 0; index < source.Length; index++)
                {
                    var value = ToInteger(source[index], 24); var offset = index * 3;
                    target[offset] = (byte)value; target[offset + 1] = (byte)(value >> 8); target[offset + 2] = (byte)(value >> 16);
                }
                break;
            case 16: for (var index = 0; index < source.Length; index++) BinaryPrimitives.WriteInt16LittleEndian(target[(index * 2)..], (short)ToInteger(source[index], 16)); break;
            default: throw new NotSupportedException($"{bitsPerSample}-bit output isn't supported.");
        }
    }

    // Float to integer samples at the given depth, clamped (FLAC and PCM writing)
    public static int ToInteger(float sample, int bitsPerSample)
    {
        var full = 1 << (bitsPerSample - 1);
        return (int)Math.Clamp(Math.Round((double)sample * full), -full, full - 1);
    }
}
