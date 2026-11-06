using System.IO;
namespace WinTapeRecorder.Audio;

// Min and max of each channel per 256-frame block (SPEC.md › Architecture): the waveform at any zoom above sample level, and gap detection
public sealed class Peaks
{
    public const int BlockFrames = 256;
    public int Channels { get; }
    public long BlockCount { get; private set; }
    float[][] values; // per channel: min, max, min, max...
    // The block being filled while recording
    readonly float[] pendingMin, pendingMax; int pendingFrames;
    readonly object valuesLock = new();

    public Peaks(int channels)
    {
        Channels = channels; values = Enumerable.Range(0, channels).Select(_ => new float[2 * 4096]).ToArray();
        pendingMin = new float[channels]; pendingMax = new float[channels]; ResetPending();
    }

    void ResetPending() { Array.Fill(pendingMin, float.MaxValue); Array.Fill(pendingMax, float.MinValue); pendingFrames = 0; }

    // Interleaved samples, any length; completes blocks as they fill
    public void Add(ReadOnlySpan<float> samples)
    {
        var frames = samples.Length / Channels;
        for (var frame = 0; frame < frames; frame++)
        {
            for (var channel = 0; channel < Channels; channel++)
            {
                var sample = samples[frame * Channels + channel];
                if (sample < pendingMin[channel]) pendingMin[channel] = sample;
                if (sample > pendingMax[channel]) pendingMax[channel] = sample;
            }
            if (++pendingFrames == BlockFrames) CompleteBlock();
        }
    }

    void CompleteBlock()
    {
        lock (valuesLock)
        {
            var index = (int)BlockCount * 2;
            if (index + 2 > values[0].Length) for (var channel = 0; channel < Channels; channel++) Array.Resize(ref values[channel], values[channel].Length * 2);
            for (var channel = 0; channel < Channels; channel++) { values[channel][index] = pendingMin[channel]; values[channel][index + 1] = pendingMax[channel]; }
            BlockCount++;
        }
        ResetPending();
    }

    public void Finish() { if (pendingFrames > 0) CompleteBlock(); }

    public (float min, float max) Get(int channel, long block) { lock (valuesLock) { var index = (int)block * 2; return (values[channel][index], values[channel][index + 1]); } }

    // Loudest absolute peak across channels for a block (gap detection)
    public float Level(long block)
    {
        lock (valuesLock)
        {
            var index = (int)block * 2; var level = 0f;
            for (var channel = 0; channel < Channels; channel++) level = Math.Max(level, Math.Max(-values[channel][index], values[channel][index + 1]));
            return level;
        }
    }

    // Min/max per column for each channel over [firstBlock, lastBlock)
    public float[][] Columns(double firstBlock, double blocksPerColumn, int columns)
    {
        var result = Enumerable.Range(0, Channels).Select(_ => new float[columns * 2]).ToArray();
        lock (valuesLock)
        {
            for (var column = 0; column < columns; column++)
            {
                var from = (long)Math.Floor(firstBlock + column * blocksPerColumn); var to = (long)Math.Floor(firstBlock + (column + 1) * blocksPerColumn);
                if (to <= from) to = from + 1;
                from = Math.Max(0, from); to = Math.Min(BlockCount, to);
                for (var channel = 0; channel < Channels; channel++)
                {
                    float min = 0, max = 0; var any = false;
                    for (var block = from; block < to; block++)
                    {
                        var blockMin = values[channel][block * 2]; var blockMax = values[channel][block * 2 + 1];
                        if (!any) { min = blockMin; max = blockMax; any = true; } else { if (blockMin < min) min = blockMin; if (blockMax > max) max = blockMax; }
                    }
                    result[channel][column * 2] = min; result[channel][column * 2 + 1] = max;
                }
            }
        }
        return result;
    }

    public void Save(string path)
    {
        lock (valuesLock)
        {
            using var writer = new BinaryWriter(File.Create(path));
            writer.Write(0x4B504157); writer.Write(Channels); writer.Write(BlockCount);
            for (long block = 0; block < BlockCount; block++) for (var channel = 0; channel < Channels; channel++) { writer.Write(values[channel][block * 2]); writer.Write(values[channel][block * 2 + 1]); }
        }
    }

    public static Peaks? Load(string path, long expectedFrames)
    {
        try
        {
            using var reader = new BinaryReader(File.OpenRead(path));
            if (reader.ReadInt32() != 0x4B504157) return null;
            var peaks = new Peaks(reader.ReadInt32()); var blockCount = reader.ReadInt64();
            if (Math.Abs(blockCount - (expectedFrames + BlockFrames - 1) / BlockFrames) > 1) return null; // the audio changed since
            peaks.values = Enumerable.Range(0, peaks.Channels).Select(_ => new float[Math.Max(2, blockCount * 2)]).ToArray();
            for (long block = 0; block < blockCount; block++) for (var channel = 0; channel < peaks.Channels; channel++) { peaks.values[channel][block * 2] = reader.ReadSingle(); peaks.values[channel][block * 2 + 1] = reader.ReadSingle(); }
            peaks.BlockCount = blockCount;
            return peaks;
        }
        catch { return null; }
    }

    // Built by reading the whole WAV (sessions recorded before, or after a crash)
    public static Peaks Build(SessionAudio audio, Action<double>? progress = null)
    {
        var peaks = new Peaks(audio.Channels);
        var buffer = new float[65536 * audio.Channels];
        for (long frame = 0; frame < audio.TotalFrames;)
        {
            var read = audio.Read(frame, 65536, buffer); if (read == 0) break;
            peaks.Add(buffer.AsSpan(0, read * audio.Channels)); frame += read;
            progress?.Invoke((double)frame / audio.TotalFrames);
        }
        peaks.Finish();
        return peaks;
    }
}
