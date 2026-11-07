using System.IO;
using System.Security.Cryptography;
namespace WinTapeRecorder.Audio;

// A small FLAC encoder (fixed predictors, Rice coding, stereo decorrelation): no codec needed from Windows, and lossless by construction
public sealed class FlacWriter : IDisposable
{
    const int BlockSize = 4096;
    readonly FileStream file;
    readonly int sampleRate, channels, bitsPerSample;
    readonly int[][] block; int blockFrames;
    long totalFrames, frameNumber;
    int minFrameBytes = int.MaxValue, maxFrameBytes;
    readonly IncrementalHash md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
    readonly byte[] md5Buffer;
    readonly BitWriter bits = new();

    public FlacWriter(string path, int sampleRate, int channels, int bitsPerSample)
    {
        if (bitsPerSample is not (16 or 24)) throw new NotSupportedException("FLAC output is 16 or 24-bit.");
        if (channels is < 1 or > 8) throw new NotSupportedException("FLAC holds 1 to 8 channels.");
        this.sampleRate = sampleRate; this.channels = channels; this.bitsPerSample = bitsPerSample;
        block = Enumerable.Range(0, channels).Select(_ => new int[BlockSize]).ToArray();
        md5Buffer = new byte[BlockSize * channels * (bitsPerSample / 8)];
        file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16);
        file.Write("fLaC"u8);
        WriteStreamInfo();
    }

    void WriteStreamInfo()
    {
        var info = new BitWriter();
        info.Write(0x80, 8); info.Write(34, 24); // last metadata block, STREAMINFO, 34 bytes
        info.Write(BlockSize, 16); info.Write(BlockSize, 16);
        info.Write(maxFrameBytes == 0 ? 0u : (uint)minFrameBytes, 24); info.Write((uint)maxFrameBytes, 24);
        info.Write((uint)sampleRate, 20); info.Write((uint)channels - 1, 3); info.Write((uint)bitsPerSample - 1, 5);
        info.Write((uint)(totalFrames >> 32) & 0xF, 4); info.Write((uint)totalFrames, 32);
        var hash = maxFrameBytes == 0 ? new byte[16] : md5.GetHashAndReset();
        foreach (var hashByte in hash) info.Write(hashByte, 8);
        file.Position = 4; file.Write(info.Bytes);
    }

    public void Write(ReadOnlySpan<float> interleaved)
    {
        var frames = interleaved.Length / channels;
        for (var frame = 0; frame < frames; frame++)
        {
            for (var channel = 0; channel < channels; channel++) block[channel][blockFrames] = SampleConvert.ToInteger(interleaved[frame * channels + channel], bitsPerSample);
            if (++blockFrames == BlockSize) EncodeBlock();
        }
    }

    void EncodeBlock()
    {
        if (blockFrames == 0) return;
        HashBlock();
        bits.Clear();
        // Stereo: whichever of left/right, left/side, side/right or mid/side costs least
        var assignment = channels - 1; int[][] signals = block; int[] signalBits = Enumerable.Repeat(bitsPerSample, channels).ToArray();
        if (channels == 2)
        {
            var left = block[0]; var right = block[1];
            var mid = new int[blockFrames]; var side = new int[blockFrames];
            for (var index = 0; index < blockFrames; index++) { mid[index] = (left[index] + right[index]) >> 1; side[index] = left[index] - right[index]; }
            long leftCost = EstimateCost(left, blockFrames), rightCost = EstimateCost(right, blockFrames), midCost = EstimateCost(mid, blockFrames), sideCost = EstimateCost(side, blockFrames);
            var options = new (long cost, int code, int[] first, int[] second, bool sideFirst, bool sideSecond)[] { (leftCost + rightCost, 1, left, right, false, false), (leftCost + sideCost, 8, left, side, false, true), (sideCost + rightCost, 9, side, right, true, false), (midCost + sideCost, 10, mid, side, false, true) };
            var best = options.MinBy(option => option.cost);
            assignment = best.code; signals = new[] { best.first, best.second };
            signalBits = new[] { bitsPerSample + (best.sideFirst ? 1 : 0), bitsPerSample + (best.sideSecond ? 1 : 0) };
        }
        WriteFrameHeader(assignment);
        for (var channel = 0; channel < channels; channel++) WriteSubframe(signals[channel], signalBits[channel]);
        bits.AlignToByte();
        bits.Write(Crc16(bits.Bytes), 16);
        var frameBytes = bits.Bytes;
        file.Write(frameBytes);
        minFrameBytes = Math.Min(minFrameBytes, frameBytes.Length); maxFrameBytes = Math.Max(maxFrameBytes, frameBytes.Length);
        totalFrames += blockFrames; frameNumber++; blockFrames = 0;
    }

    void HashBlock()
    {
        var bytesPerSample = bitsPerSample / 8; var position = 0;
        for (var frame = 0; frame < blockFrames; frame++)
            for (var channel = 0; channel < channels; channel++)
            {
                var value = block[channel][frame];
                md5Buffer[position++] = (byte)value; md5Buffer[position++] = (byte)(value >> 8);
                if (bytesPerSample == 3) md5Buffer[position++] = (byte)(value >> 16);
            }
        md5.AppendData(md5Buffer, 0, position);
    }

    void WriteFrameHeader(int assignment)
    {
        var headerStart = bits.Length;
        bits.Write(0xFFF8, 16); // sync, reserved, fixed block size
        var blockCode = blockFrames == BlockSize ? 12 : blockFrames <= 256 ? 6 : 7;
        bits.Write((uint)blockCode, 4);
        bits.Write((uint)(sampleRate switch { 88200 => 1, 176400 => 2, 192000 => 3, 8000 => 4, 16000 => 5, 22050 => 6, 24000 => 7, 32000 => 8, 44100 => 9, 48000 => 10, 96000 => 11, _ => 0 }), 4);
        bits.Write((uint)assignment, 4);
        bits.Write(bitsPerSample == 16 ? 4u : 6u, 3);
        bits.Write(0, 1);
        WriteUtf8(frameNumber);
        if (blockCode == 6) bits.Write((uint)blockFrames - 1, 8); else if (blockCode == 7) bits.Write((uint)blockFrames - 1, 16);
        bits.Write(Crc8(bits.Bytes.AsSpan(headerStart)), 8);
    }

    void WriteUtf8(long value)
    {
        if (value < 0x80) { bits.Write((uint)value, 8); return; }
        var extraBytes = value < 0x800 ? 1 : value < 0x10000 ? 2 : value < 0x200000 ? 3 : value < 0x4000000 ? 4 : 5;
        var leadBits = 6 - extraBytes;
        bits.Write((uint)((0xFF << (7 - extraBytes)) & 0xFF) | (uint)(value >> (6 * extraBytes)) & (uint)((1 << leadBits) - 1), 8);
        for (var index = extraBytes - 1; index >= 0; index--) bits.Write(0x80u | (uint)(value >> (6 * index)) & 0x3F, 8);
    }

    // Constant (silence), the best fixed predictor, or verbatim if prediction doesn't help
    void WriteSubframe(int[] signal, int signalBits)
    {
        var count = blockFrames;
        var constant = true; for (var index = 1; index < count && constant; index++) constant = signal[index] == signal[0];
        if (constant) { bits.Write(0, 8); bits.WriteSigned(signal[0], signalBits); return; }
        var order = BestOrder(signal, count, out var residual);
        var plan = PlanRice(residual, count, order);
        var verbatimBits = (long)count * signalBits;
        if (plan.bits + order * signalBits >= verbatimBits)
        {
            bits.Write(0x02, 8); // verbatim
            for (var index = 0; index < count; index++) bits.WriteSigned(signal[index], signalBits);
            return;
        }
        bits.Write((uint)(0x08 | order) << 1, 8); // fixed predictor of this order, no wasted bits
        for (var index = 0; index < order; index++) bits.WriteSigned(signal[index], signalBits);
        var wideParameters = plan.parameters.Any(parameter => parameter > 14);
        bits.Write(wideParameters ? 1u : 0u, 2); bits.Write((uint)plan.partitionOrder, 4);
        var partitionLength = count >> plan.partitionOrder; var sample = order;
        for (var partition = 0; partition < plan.parameters.Length; partition++)
        {
            var parameter = plan.parameters[partition];
            bits.Write((uint)parameter, wideParameters ? 5 : 4);
            var end = (partition + 1) * partitionLength;
            for (; sample < end; sample++) bits.WriteRice(residual[sample], parameter);
        }
    }

    static int BestOrder(int[] signal, int count, out int[] residual)
    {
        var totals = new long[5];
        for (var index = 4; index < count; index++)
        {
            long x0 = signal[index], x1 = signal[index - 1], x2 = signal[index - 2], x3 = signal[index - 3], x4 = signal[index - 4];
            totals[0] += Math.Abs(x0); totals[1] += Math.Abs(x0 - x1); totals[2] += Math.Abs(x0 - 2 * x1 + x2); totals[3] += Math.Abs(x0 - 3 * x1 + 3 * x2 - x3); totals[4] += Math.Abs(x0 - 4 * x1 + 6 * x2 - 4 * x3 + x4);
        }
        var order = count <= 4 ? 0 : Array.IndexOf(totals, totals.Min());
        residual = new int[count];
        for (var index = order; index < count; index++)
        {
            long x0 = signal[index];
            residual[index] = (int)(order switch
            {
                0 => x0,
                1 => x0 - signal[index - 1],
                2 => x0 - 2L * signal[index - 1] + signal[index - 2],
                3 => x0 - 3L * signal[index - 1] + 3L * signal[index - 2] - signal[index - 3],
                _ => x0 - 4L * signal[index - 1] + 6L * signal[index - 2] - 4L * signal[index - 3] + signal[index - 4],
            });
        }
        return order;
    }

    // Partition order and a Rice parameter per partition, by estimated size
    static (int partitionOrder, int[] parameters, long bits) PlanRice(int[] residual, int count, int order)
    {
        (int, int[], long) best = (0, Array.Empty<int>(), long.MaxValue);
        for (var partitionOrder = 0; partitionOrder <= 8; partitionOrder++)
        {
            if (count % (1 << partitionOrder) != 0 || (count >> partitionOrder) <= order) break;
            var partitions = 1 << partitionOrder; var partitionLength = count >> partitionOrder;
            var parameters = new int[partitions]; long total = 6;
            for (var partition = 0; partition < partitions; partition++)
            {
                var start = partition == 0 ? order : partition * partitionLength; var end = (partition + 1) * partitionLength;
                long sum = 0; for (var index = start; index < end; index++) sum += ZigZag(residual[index]);
                var samples = end - start;
                var parameter = 0; var mean = samples > 0 ? sum / samples : 0; while (parameter < 30 && (1L << (parameter + 1)) <= mean) parameter++;
                parameters[partition] = parameter;
                total += 5 + (long)samples * (parameter + 1) + (sum >> parameter);
            }
            if (total < best.Item3) best = (partitionOrder, parameters, total);
        }
        return best;
    }

    static long EstimateCost(int[] signal, int count) { var order = BestOrder(signal, count, out var residual); return PlanRice(residual, count, order).bits; }
    static uint ZigZag(int value) => (uint)((value << 1) ^ (value >> 31));

    static byte Crc8(ReadOnlySpan<byte> data) { byte crc = 0; foreach (var dataByte in data) { crc ^= dataByte; for (var bit = 0; bit < 8; bit++) crc = (byte)((crc & 0x80) != 0 ? (crc << 1) ^ 0x07 : crc << 1); } return crc; }
    static uint Crc16(ReadOnlySpan<byte> data) { uint crc = 0; foreach (var dataByte in data) { crc ^= (uint)dataByte << 8; for (var bit = 0; bit < 8; bit++) crc = (crc & 0x8000) != 0 ? ((crc << 1) ^ 0x8005) & 0xFFFF : (crc << 1) & 0xFFFF; } return crc; }

    // How small FLAC would make this audio (bytes per byte of PCM), from the encoder's cost model: no file written
    public static double EstimateRatio(ReadOnlySpan<float> interleaved, int channels, int bitsPerSample)
    {
        var frames = interleaved.Length / channels; if (frames < 64 || channels < 1) return 0.6;
        var block = Enumerable.Range(0, channels).Select(_ => new int[frames]).ToArray();
        for (var frame = 0; frame < frames; frame++) for (var channel = 0; channel < channels; channel++) block[channel][frame] = SampleConvert.ToInteger(interleaved[frame * channels + channel], bitsPerSample);
        double bits;
        if (channels == 2)
        {
            var mid = new int[frames]; var side = new int[frames];
            for (var index = 0; index < frames; index++) { mid[index] = (block[0][index] + block[1][index]) >> 1; side[index] = block[0][index] - block[1][index]; }
            double left = EstimateCost(block[0], frames), right = EstimateCost(block[1], frames), midCost = EstimateCost(mid, frames), sideCost = EstimateCost(side, frames);
            bits = Math.Min(Math.Min(left + right, left + sideCost), Math.Min(sideCost + right, midCost + sideCost));
        }
        else bits = block.Sum(signal => (double)EstimateCost(signal, frames));
        bits += 100 * Math.Ceiling(frames / 4096.0); // frame headers and such
        return Math.Clamp(bits / ((double)frames * channels * bitsPerSample), 0.1, 1.05);
    }

    public void Dispose()
    {
        EncodeBlock(); // the partial last block, at its own size
        WriteStreamInfo();
        file.Dispose(); md5.Dispose();
    }

    sealed class BitWriter
    {
        byte[] buffer = new byte[1 << 16]; int length; ulong accumulator; int pending;
        public int Length => length;
        public byte[] Bytes => buffer.AsSpan(0, length).ToArray();
        public void Clear() { length = 0; accumulator = 0; pending = 0; }
        void Put(byte value) { if (length == buffer.Length) Array.Resize(ref buffer, buffer.Length * 2); buffer[length++] = value; }
        public void Write(uint value, int count)
        {
            if (count == 0) return;
            accumulator = (accumulator << count) | (value & (count == 32 ? 0xFFFFFFFFu : (1u << count) - 1)); pending += count;
            while (pending >= 8) { pending -= 8; Put((byte)(accumulator >> pending)); }
            accumulator &= (1UL << pending) - 1;
        }
        public void WriteSigned(int value, int count) => Write((uint)value, count);
        public void WriteRice(int value, int parameter)
        {
            var folded = ZigZag(value); var quotient = folded >> parameter;
            while (quotient >= 32) { Write(0, 32); quotient -= 32; }
            Write(1, (int)quotient + 1);
            Write(folded, parameter);
        }
        public void AlignToByte() { if (pending > 0) Write(0, 8 - pending); }
    }
}
