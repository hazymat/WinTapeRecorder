namespace WinTapeRecorder.Audio;

// Source quality (SPEC.md › Recording): where the spectrum stops. Lossy encoders low-pass the audio (about 16 kHz at 128 kbps,
// 19–20 kHz at 256–320), so the highest frequency with any energy says roughly what the recording came from
public static class SpectralCutoff
{
    public const int WindowFrames = 8192;
    static readonly float[] hann = Enumerable.Range(0, WindowFrames).Select(index => (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * index / (WindowFrames - 1)))).ToArray();

    // Interleaved audio, at least WindowFrames frames: the cutoff in Hz, or null when the window is too quiet to tell
    public static int? Measure(ReadOnlySpan<float> interleaved, int channels, int sampleRate)
    {
        if (interleaved.Length < WindowFrames * channels) return null;
        var real = new double[WindowFrames]; var imaginary = new double[WindowFrames];
        for (var frame = 0; frame < WindowFrames; frame++)
        {
            float mono = 0; for (var channel = 0; channel < channels; channel++) mono += interleaved[frame * channels + channel];
            real[frame] = mono / channels * hann[frame];
        }
        Fft(real, imaginary);
        var bins = WindowFrames / 2; var binHz = (double)sampleRate / WindowFrames;
        var decibels = new double[bins];
        for (var bin = 0; bin < bins; bin++) decibels[bin] = 20 * Math.Log10(Math.Sqrt(real[bin] * real[bin] + imaginary[bin] * imaginary[bin]) / WindowFrames * 4 + 1e-12);
        // Reference: the loudest midrange bin. Silence or near-silence tells us nothing
        int fromBin = (int)(300 / binHz), toBin = (int)(3000 / binHz);
        var reference = double.MinValue; for (var bin = fromBin; bin < toBin; bin++) if (decibels[bin] > reference) reference = decibels[bin];
        if (reference < -50) return null;
        // Envelope: the loudest bin in each 150 Hz band, so single tones and dither don't mislead. The cutoff is the highest band still
        // within 70 dB of the midrange peak; a brick-wall low-pass drops far below that at once
        var bandBins = Math.Max(1, (int)(150 / binHz)); var threshold = reference - 70;
        for (var bandEnd = bins; bandEnd > bandBins; bandEnd -= bandBins)
        {
            var loudest = double.MinValue; for (var bin = bandEnd - bandBins; bin < bandEnd; bin++) if (decibels[bin] > loudest) loudest = decibels[bin];
            if (loudest > threshold) return (int)Math.Round(bandEnd * binHz);
        }
        return null;
    }

    // What a cutoff usually means. Lossless at 44.1 kHz reaches 22 kHz; a 320 kbps MP3 stops near 20 kHz, so the top band stays a range
    public static string Describe(int? cutoffHz) => cutoffHz switch
    {
        null => "",
        >= 20500 => "full bandwidth: lossless, or a very high bitrate",
        >= 19000 => "about 256–320 kbps, or lossless with a gentle top end",
        >= 16500 => "about 192 kbps",
        >= 14000 => "about 128 kbps",
        >= 11000 => "about 96 kbps",
        _ => "64 kbps or lower",
    };

    // Iterative radix-2 FFT, in place
    static void Fft(double[] real, double[] imaginary)
    {
        var count = real.Length;
        for (int index = 1, reversed = 0; index < count; index++)
        {
            var bit = count >> 1;
            for (; (reversed & bit) != 0; bit >>= 1) reversed ^= bit;
            reversed ^= bit;
            if (index < reversed) { (real[index], real[reversed]) = (real[reversed], real[index]); (imaginary[index], imaginary[reversed]) = (imaginary[reversed], imaginary[index]); }
        }
        for (var length = 2; length <= count; length <<= 1)
        {
            double angle = -2 * Math.PI / length, rootReal = Math.Cos(angle), rootImaginary = Math.Sin(angle);
            for (var start = 0; start < count; start += length)
            {
                double twiddleReal = 1, twiddleImaginary = 0;
                for (var offset = 0; offset < length / 2; offset++)
                {
                    int even = start + offset, odd = start + offset + length / 2;
                    double oddReal = real[odd] * twiddleReal - imaginary[odd] * twiddleImaginary, oddImaginary = real[odd] * twiddleImaginary + imaginary[odd] * twiddleReal;
                    real[odd] = real[even] - oddReal; imaginary[odd] = imaginary[even] - oddImaginary;
                    real[even] += oddReal; imaginary[even] += oddImaginary;
                    var nextReal = twiddleReal * rootReal - twiddleImaginary * rootImaginary; twiddleImaginary = twiddleReal * rootImaginary + twiddleImaginary * rootReal; twiddleReal = nextReal;
                }
            }
        }
    }

    // Spectrogram columns (SPEC.md › Editing): for each column, the dB spectrum of a short window at that frame, folded into rows from 0 Hz to Nyquist, as bytes (0 = -100 dB or quieter, 255 = full scale)
    public const int SpectrogramWindow = 2048;
    static readonly float[] hannShort = Enumerable.Range(0, SpectrogramWindow).Select(index => (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * index / (SpectrogramWindow - 1)))).ToArray();
    public static void Column(ReadOnlySpan<float> interleaved, int channels, Span<byte> column)
    {
        var real = new double[SpectrogramWindow]; var imaginary = new double[SpectrogramWindow];
        var frames = Math.Min(SpectrogramWindow, interleaved.Length / channels);
        for (var frame = 0; frame < frames; frame++) { float mono = 0; for (var channel = 0; channel < channels; channel++) mono += interleaved[frame * channels + channel]; real[frame] = mono / channels * hannShort[frame]; }
        Fft(real, imaginary);
        var bins = SpectrogramWindow / 2; var rows = column.Length; var binsPerRow = (double)bins / rows;
        for (var row = 0; row < rows; row++)
        {
            int from = (int)(row * binsPerRow), to = Math.Max(from + 1, (int)((row + 1) * binsPerRow));
            var loudest = 0.0; for (var bin = from; bin < to && bin < bins; bin++) { var power = real[bin] * real[bin] + imaginary[bin] * imaginary[bin]; if (power > loudest) loudest = power; }
            var decibels = 10 * Math.Log10(loudest / (SpectrogramWindow * SpectrogramWindow) * 16 + 1e-12);
            column[rows - 1 - row] = (byte)Math.Clamp((decibels + 100) * 2.55, 0, 255); // row 0 at the top = Nyquist
        }
    }
}
