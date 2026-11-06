using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using WinTapeRecorder.Sessions;
using WinTapeRecorder.Settings;
namespace WinTapeRecorder.Audio;

// Records one session to a single WAV (SPEC.md › Recording): an input device, or an output device's loopback
public sealed class Recorder : IDisposable
{
    IWaveIn? capture;
    WasapiOut? silenceKeeper; // loopback only delivers audio while something plays: playing silence keeps the clock running through gaps
    FileStream? output;
    BinaryWriter? headerWriter;
    long dataStart;
    public Session? Session { get; private set; }
    public Peaks? Peaks { get; private set; }
    public bool Recording => Session != null;
    public long FramesWritten => Interlocked.Read(ref framesWritten);
    long framesWritten;
    int channels, bitDepth, captureBits; bool captureFloat;
    float[] floatBuffer = Array.Empty<float>(); byte[] byteBuffer = Array.Empty<byte>();
    readonly object writeLock = new();
    // Input level check: highest peak per channel since the UI last asked, and clipped samples this session
    float[] heldPeaks = new float[2];
    public long ClippedSamples => Interlocked.Read(ref clippedSamples);
    long clippedSamples;
    public string Problem { get; private set; } = "";
    public string DeviceName { get; private set; } = "";
    public event Action? Stopped; // by itself: device gone, disk full, size limit
    DateTime lastHeaderUpdate;
    const long SizeLimit = uint.MaxValue - (64L << 20); // stop with room to spare under the WAV format's 4 GB

    public bool Capturing => capture != null;

    // With a session: records into it. Without: only watches the input level (the Record view's level check before recording)
    public void Start(RecordingProfile profile, Session? session)
    {
        var device = AudioDevices.Find(profile.DeviceId, profile.DeviceName, profile.Loopback) ?? throw new InvalidOperationException(profile.DeviceId == "" ? "No default audio device." : $"{profile.DeviceName} isn't connected.");
        capture = profile.Loopback ? new WasapiLoopbackCapture(device) : new WasapiCapture(device, true, 50);
        var captureFormat = capture.WaveFormat;
        channels = captureFormat.Channels; captureBits = captureFormat.BitsPerSample;
        captureFloat = captureFormat.Encoding == WaveFormatEncoding.IeeeFloat || (captureFormat is WaveFormatExtensible extensible && extensible.SubFormat == NAudio.Dmo.AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT);
        bitDepth = profile.BitDepth is 16 or 24 or 32 ? profile.BitDepth : 24;
        heldPeaks = new float[channels]; clippedSamples = 0; framesWritten = 0; Problem = "";
        DeviceName = device.FriendlyName;
        if (session != null)
        {
            session.Device = device.FriendlyName; session.Loopback = profile.Loopback; session.SampleRate = captureFormat.SampleRate; session.Channels = channels; session.BitDepth = bitDepth;
            session.Save();
            Peaks = new Peaks(channels);
            OpenWav(session.AudioPath, captureFormat.SampleRate);
            Session = session;
        }
        if (profile.Loopback)
        {
            try { silenceKeeper = new WasapiOut(device, AudioClientShareMode.Shared, true, 100); silenceKeeper.Init(new SilenceProvider(WaveFormat.CreateIeeeFloatWaveFormat(captureFormat.SampleRate, channels))); silenceKeeper.Play(); }
            catch { silenceKeeper?.Dispose(); silenceKeeper = null; }
        }
        capture.DataAvailable += OnData;
        capture.RecordingStopped += (sender, args) => { if (args.Exception != null && capture != null) { Problem = args.Exception.Message; Stop(); Stopped?.Invoke(); } };
        capture.StartRecording();
    }

    void OpenWav(string path, int sampleRate)
    {
        output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 18);
        headerWriter = new BinaryWriter(output, System.Text.Encoding.ASCII, true);
        var bytesPerSample = bitDepth / 8; var formatTag = (ushort)(bitDepth == 32 ? 3 : 1);
        headerWriter.Write("RIFF"u8); headerWriter.Write(0u); headerWriter.Write("WAVE"u8);
        headerWriter.Write("fmt "u8); headerWriter.Write(16u); headerWriter.Write(formatTag); headerWriter.Write((ushort)channels); headerWriter.Write((uint)sampleRate);
        headerWriter.Write((uint)(sampleRate * bytesPerSample * channels)); headerWriter.Write((ushort)(bytesPerSample * channels)); headerWriter.Write((ushort)bitDepth);
        headerWriter.Write("data"u8); headerWriter.Write(0u);
        headerWriter.Flush();
        dataStart = output.Position;
    }

    void OnData(object? sender, WaveInEventArgs args)
    {
        lock (writeLock)
        {
            if (args.BytesRecorded == 0) return;
            var sampleCount = args.BytesRecorded / (captureBits / 8);
            if (floatBuffer.Length < sampleCount) floatBuffer = new float[sampleCount];
            var samples = floatBuffer.AsSpan(0, sampleCount);
            SampleConvert.ToFloat(args.Buffer.AsSpan(0, args.BytesRecorded), samples, captureBits, captureFloat);
            for (var index = 0; index < sampleCount; index++)
            {
                var level = Math.Abs(samples[index]); var channel = index % channels;
                if (level > heldPeaks[channel]) heldPeaks[channel] = level;
                if (level >= 0.9999f) clippedSamples++;
            }
            if (output == null) return; // level check only
            var byteCount = sampleCount * (bitDepth / 8);
            if (byteBuffer.Length < byteCount) byteBuffer = new byte[byteCount];
            SampleConvert.FromFloat(samples, byteBuffer.AsSpan(0, byteCount), bitDepth);
            try
            {
                output.Write(byteBuffer, 0, byteCount);
                Peaks!.Add(samples);
                Interlocked.Add(ref framesWritten, sampleCount / channels);
                if (DateTime.UtcNow - lastHeaderUpdate > TimeSpan.FromSeconds(2)) { WriteLengths(); lastHeaderUpdate = DateTime.UtcNow; } // a crash still leaves a playable file
            }
            catch (IOException writeError) { Problem = writeError.Message; _ = Task.Run(() => { Stop(); Stopped?.Invoke(); }); return; }
            if (output.Position > SizeLimit) { Problem = "The recording reached the WAV size limit (4 GB) and was stopped."; _ = Task.Run(() => { Stop(); Stopped?.Invoke(); }); }
        }
    }

    void WriteLengths()
    {
        if (output == null || headerWriter == null) return;
        var end = output.Position; var dataLength = end - dataStart;
        output.Position = 4; headerWriter.Write((uint)(end - 8));
        output.Position = dataStart - 4; headerWriter.Write((uint)dataLength);
        headerWriter.Flush(); output.Position = end; output.Flush(true);
    }

    // Peak per channel since the last call (0..1+), for the level check
    public float[] TakePeaks() { lock (writeLock) { var taken = heldPeaks.ToArray(); Array.Clear(heldPeaks); return taken; } }

    public double SecondsRecorded => Session == null || Session.SampleRate == 0 ? 0 : (double)FramesWritten / Session.SampleRate;
    public long BytesWritten => FramesWritten * channels * (bitDepth / 8);
    public double MinutesLeftUnderLimit => Session == null || Session.SampleRate == 0 ? 0 : (SizeLimit - BytesWritten) / (double)(Session.SampleRate * channels * (bitDepth / 8)) / 60;

    // Finishes the file; returns the session (peaks saved) or null when nothing was recording
    public Session? Stop()
    {
        var stoppedCapture = capture; capture = null;
        try { stoppedCapture?.StopRecording(); } catch { }
        try { stoppedCapture?.Dispose(); } catch { }
        try { silenceKeeper?.Stop(); silenceKeeper?.Dispose(); } catch { }
        silenceKeeper = null;
        Session? session; Peaks? peaks;
        lock (writeLock)
        {
            session = Session; peaks = Peaks; Session = null;
            if (output != null) { try { WriteLengths(); } catch { } headerWriter?.Dispose(); output.Dispose(); output = null; headerWriter = null; }
        }
        if (session == null) return null;
        peaks?.Finish();
        session.TotalFrames = FramesWritten; session.Recording = false; session.ClippedSamples = (int)Math.Min(int.MaxValue, ClippedSamples);
        try { peaks?.Save(session.PeaksPath); } catch { }
        session.Save();
        return session;
    }

    public void Dispose() => Stop();
}
