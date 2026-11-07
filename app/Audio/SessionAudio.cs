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
        var riff = new string(reader.ReadChars(4));
        if (riff != "RIFF" && riff != "RF64") throw new InvalidDataException("Not a WAV file.");
        reader.ReadUInt32();
        if (new string(reader.ReadChars(4)) != "WAVE") throw new InvalidDataException("Not a WAV file.");
        long dataLength = -1, data64 = -1;
        while (stream.Position + 8 <= stream.Length)
        {
            var chunkId = new string(reader.ReadChars(4)); var chunkLength = reader.ReadUInt32(); var chunkStart = stream.Position;
            if (chunkId == "ds64" && chunkLength >= 16) { reader.ReadUInt64(); data64 = (long)reader.ReadUInt64(); }
            else if (chunkId == "fmt ")
            {
                var formatTag = reader.ReadUInt16(); Channels = reader.ReadUInt16(); SampleRate = (int)reader.ReadUInt32(); reader.ReadUInt32(); reader.ReadUInt16(); BitsPerSample = reader.ReadUInt16();
                IsFloat = formatTag == 3;
                if (formatTag == 0xFFFE && chunkLength >= 40) { reader.ReadUInt16(); reader.ReadUInt16(); reader.ReadUInt32(); IsFloat = reader.ReadUInt16() == 3; } // extensible: subformat GUID starts with the format tag
            }
            else if (chunkId == "data") { dataOffset = chunkStart; dataLength = chunkLength == uint.MaxValue && data64 >= 0 ? data64 : chunkLength; break; }
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

    // Rewrites the lengths from the file size (after a crash mid-recording)
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
                WriteSizes(file, chunkStart, actual, blockAlign);
                return;
            }
            file.Position = chunkStart + chunkLength + (chunkLength & 1);
        }
    }

    // The header's lengths for dataLength bytes of audio starting at dataStart. Up to 4 GB a plain RIFF WAV; past it RF64 (EBU Tech 3306):
    // the 28-byte JUNK chunk the recorder reserves after "WAVE" becomes ds64 with 64-bit lengths, so one file holds a whole night
    public static void WriteSizes(Stream file, long dataStart, long dataLength, int blockAlign)
    {
        var end = file.Position; var bytes = new byte[8];
        void At(long position, ReadOnlySpan<byte> data) { file.Position = position; file.Write(data); }
        void U32(long position, uint value) { BinaryPrimitives.WriteUInt32LittleEndian(bytes, value); At(position, bytes.AsSpan(0, 4)); }
        void U64(long position, long value) { BinaryPrimitives.WriteUInt64LittleEndian(bytes, (ulong)value); At(position, bytes.AsSpan(0, 8)); }
        var head = new byte[4]; file.Position = 12; file.ReadExactly(head);
        var hasSlot = Encoding.ASCII.GetString(head) is "JUNK" or "ds64";
        var riffLength = dataStart + dataLength - 8;
        if (riffLength <= uint.MaxValue || !hasSlot)
        {
            if (!hasSlot) { dataLength = Math.Min(dataLength, uint.MaxValue - dataStart - 64); riffLength = dataStart + dataLength - 8; }
            At(0, "RIFF"u8); U32(4, (uint)riffLength); U32(dataStart - 4, (uint)dataLength);
            if (hasSlot) At(12, "JUNK"u8);
        }
        else
        {
            At(0, "RF64"u8); U32(4, uint.MaxValue); At(12, "ds64"u8); U32(16, 28);
            U64(20, riffLength); U64(28, dataLength); U64(36, blockAlign > 0 ? dataLength / blockAlign : 0); U32(44, 0);
            U32(dataStart - 4, uint.MaxValue);
        }
        file.Position = end;
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
