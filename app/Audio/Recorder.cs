using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using WinTapeRecorder.Sessions;
using WinTapeRecorder.Settings;
namespace WinTapeRecorder.Audio;

// Loopback with a deep (half-second) engine buffer, so a busy moment on the PC doesn't lose samples
sealed class LoopbackCapture : WasapiCapture
{
    public LoopbackCapture(MMDevice device) : base(device, false, 500) { }
    protected override AudioClientStreamFlags GetAudioClientStreamFlags() => AudioClientStreamFlags.Loopback | base.GetAudioClientStreamFlags();
}

// Records one session to a single WAV (SPEC.md › Recording): an input device, or an output device's loopback.
// The audio thread only converts, meters and queues; a writer thread puts the bytes on disk, so a slow disk never holds the audio up.
public sealed class Recorder : IDisposable
{
    IWaveIn? capture;
    WasapiOut? silenceKeeper; // loopback only delivers audio while something plays: playing silence keeps the clock running through gaps
    FileStream? output;
    BinaryWriter? headerWriter;
    long dataStart;
    BlockingCollection<(byte[] bytes, int count)>? writeQueue;
    Thread? writerThread;
    public Session? Session { get; private set; }
    public Peaks? Peaks { get; private set; }
    public bool Recording => Session != null;
    public long FramesWritten => Interlocked.Read(ref framesWritten);
    long framesWritten; // frames queued for the file (what the recording clock shows)
    int channels, bitDepth, captureBits, sampleRate; bool captureFloat;
    float[] floatBuffer = Array.Empty<float>();
    readonly object meterLock = new();
    // Input level check: highest peak per channel since the UI last asked, and clipped samples this session
    float[] heldPeaks = new float[2];
    public long ClippedSamples => Interlocked.Read(ref clippedSamples);
    long clippedSamples;
    public string Problem { get; private set; } = "";
    public string DeviceName { get; private set; } = "";
    public double DeviceVolume { get; private set; } = -1; // the Windows volume of a loopback device (0..1), -1 when unknown: loopback may record at that level
    public event Action? Stopped; // by itself: device gone, disk full, size limit
    public event Action? DropoutFound;
    public event Action? ClipFound;
    public List<ClipEvent> Clips { get; } = new();
    long clipBurstFrame = -1; int clipBurstSamples;
    DateTime lastHeaderUpdate;
    const long SizeLimit = uint.MaxValue - (64L << 20); // stop with room to spare under the WAV format's 4 GB

    // Dropout detection (SPEC.md › Recording): the audio clock is compared with the wall clock callback by callback; a sudden shortfall is samples the engine threw away
    readonly Stopwatch wallClock = new();
    double lastWallSeconds; double shortfallFrames; bool detecting;
    public List<Dropout> Dropouts { get; } = new();
    // Source quality: every two seconds the latest window of audio goes to a worker for its spectral cutoff
    readonly float[] qualityWindow = new float[SpectralCutoff.WindowFrames * 8]; int qualityFill; long nextQualityFrame; bool qualityBusy;
    public List<QualitySample> Quality { get; } = new();
    public int? CurrentCutoffHz { get; private set; }
    public double FlacRatio { get; private set; } // running estimate of FLAC size / WAV size for this recording
    int flacSamples;
    public event Action? QualityMeasured;

    public bool Capturing => capture != null;

    // With a session: records into it. Without: only watches the input level (the Record view's level check before recording)
    public void Start(RecordingProfile profile, Session? session)
    {
        var device = AudioDevices.Find(profile.DeviceId, profile.DeviceName, profile.Loopback) ?? throw new InvalidOperationException(profile.DeviceId == "" ? "No default audio device." : $"{profile.DeviceName} isn't connected.");
        capture = profile.Loopback ? new LoopbackCapture(device) : new WasapiCapture(device, true, 200);
        var captureFormat = capture.WaveFormat;
        channels = captureFormat.Channels; captureBits = captureFormat.BitsPerSample; sampleRate = captureFormat.SampleRate;
        captureFloat = captureFormat.Encoding == WaveFormatEncoding.IeeeFloat || (captureFormat is WaveFormatExtensible extensible && extensible.SubFormat == NAudio.Dmo.AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT);
        bitDepth = profile.BitDepth is 16 or 24 or 32 ? profile.BitDepth : 24;
        heldPeaks = new float[channels]; clippedSamples = 0; framesWritten = 0; lastSoundFrame = 0; KeepThroughSilence = false; Problem = ""; Dropouts.Clear(); shortfallFrames = 0; detecting = false;
        Quality.Clear(); qualityFill = 0; nextQualityFrame = sampleRate * 2; CurrentCutoffHz = null; Clips.Clear(); clipBurstFrame = -1; FlacRatio = 0; flacSamples = 0;
        DeviceName = device.FriendlyName;
        try { DeviceVolume = profile.Loopback ? device.AudioEndpointVolume.MasterVolumeLevelScalar : -1; } catch { DeviceVolume = -1; }
        if (session != null)
        {
            session.Device = device.FriendlyName; session.Loopback = profile.Loopback; session.SampleRate = sampleRate; session.Channels = channels; session.BitDepth = bitDepth;
            session.Save();
            Peaks = new Peaks(channels);
            OpenWav(session.AudioPath, sampleRate);
            writeQueue = new BlockingCollection<(byte[], int)>();
            writerThread = new Thread(WriteLoop) { IsBackground = true, Name = "WAV writer", Priority = ThreadPriority.AboveNormal };
            writerThread.Start();
            Session = session;
        }
        if (profile.Loopback)
        {
            try { silenceKeeper = new WasapiOut(device, AudioClientShareMode.Shared, true, 100); silenceKeeper.Init(new SilenceProvider(WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels))); silenceKeeper.Play(); }
            catch { silenceKeeper?.Dispose(); silenceKeeper = null; }
        }
        capture.DataAvailable += OnData;
        capture.RecordingStopped += (sender, args) => { if (args.Exception != null && capture != null) { Problem = args.Exception.Message; Stop(); Stopped?.Invoke(); } };
        wallClock.Restart(); lastWallSeconds = 0; framesSeen = 0;
        capture.StartRecording();
    }

    void OpenWav(string path, int rate)
    {
        output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 18);
        headerWriter = new BinaryWriter(output, System.Text.Encoding.ASCII, true);
        var bytesPerSample = bitDepth / 8; var formatTag = (ushort)(bitDepth == 32 ? 3 : 1);
        headerWriter.Write("RIFF"u8); headerWriter.Write(0u); headerWriter.Write("WAVE"u8);
        headerWriter.Write("fmt "u8); headerWriter.Write(16u); headerWriter.Write(formatTag); headerWriter.Write((ushort)channels); headerWriter.Write((uint)rate);
        headerWriter.Write((uint)(rate * bytesPerSample * channels)); headerWriter.Write((ushort)(bytesPerSample * channels)); headerWriter.Write((ushort)bitDepth);
        headerWriter.Write("data"u8); headerWriter.Write(0u);
        headerWriter.Flush();
        dataStart = output.Position;
    }

    // Audio thread: convert, meter, peaks, queue. Nothing here touches the disk
    void OnData(object? sender, WaveInEventArgs args)
    {
        if (args.BytesRecorded == 0) return;
        var sampleCount = args.BytesRecorded / (captureBits / 8); var frames = sampleCount / channels;
        if (floatBuffer.Length < sampleCount) floatBuffer = new float[sampleCount];
        var samples = floatBuffer.AsSpan(0, sampleCount);
        SampleConvert.ToFloat(args.Buffer.AsSpan(0, args.BytesRecorded), samples, captureBits, captureFloat);
        var clippedNow = 0; var loudest = 0f;
        lock (meterLock)
        {
            for (var index = 0; index < sampleCount; index++)
            {
                var level = Math.Abs(samples[index]); var channel = index % channels;
                if (level > heldPeaks[channel]) heldPeaks[channel] = level;
                if (level > loudest) loudest = level;
                if (level >= 0.9999f) { clippedSamples++; clippedNow++; }
            }
        }
        if (loudest > SilenceLevel) lastSoundFrame = FramesWritten + frames;
        if (clippedNow > 0 && Session != null) NoteClipping(clippedNow);
        NoteTiming(frames);
        GatherQuality(samples, frames);
        var queue = writeQueue;
        if (queue == null || queue.IsAddingCompleted) return; // level check only, or stopping
        var byteCount = sampleCount * (bitDepth / 8);
        var bytes = ArrayPool<byte>.Shared.Rent(byteCount);
        SampleConvert.FromFloat(samples, bytes.AsSpan(0, byteCount), bitDepth);
        Peaks!.Add(samples);
        Interlocked.Add(ref framesWritten, frames);
        try { queue.Add((bytes, byteCount)); } catch (InvalidOperationException) { ArrayPool<byte>.Shared.Return(bytes); }
    }

    // Clipped samples within two seconds of each other are one burst, logged once (the user can then replay that track)
    void NoteClipping(int count)
    {
        var at = FramesWritten;
        lock (Clips)
        {
            if (clipBurstFrame >= 0 && at - clipBurstFrame < sampleRate * 2) { clipBurstSamples += count; Clips[^1].Samples = clipBurstSamples; return; }
            clipBurstFrame = at; clipBurstSamples = count; Clips.Add(new ClipEvent { Frame = at, Samples = count });
        }
        ClipFound?.Invoke();
    }

    // Keeps the most recent window; when two seconds have passed since the last measurement, hands a copy to a worker
    long framesSeen;
    void GatherQuality(ReadOnlySpan<float> samples, int frames)
    {
        var needed = SpectralCutoff.WindowFrames * channels;
        if (samples.Length >= needed) { samples[^needed..].CopyTo(qualityWindow); qualityFill = needed; }
        else
        {
            var keep = Math.Min(qualityFill, needed - samples.Length);
            if (keep > 0) Array.Copy(qualityWindow, qualityFill - keep, qualityWindow, 0, keep);
            samples.CopyTo(qualityWindow.AsSpan(keep)); qualityFill = keep + samples.Length;
        }
        framesSeen += frames;
        if (framesSeen < nextQualityFrame || qualityFill < needed || qualityBusy) return;
        nextQualityFrame = framesSeen + sampleRate * 2; qualityBusy = true;
        var copy = qualityWindow.AsSpan(0, needed).ToArray(); var at = FramesWritten; var rate = sampleRate; var channelCount = channels;
        Task.Run(() =>
        {
            try
            {
                var cutoff = SpectralCutoff.Measure(copy, channelCount, rate);
                CurrentCutoffHz = cutoff;
                var ratio = FlacWriter.EstimateRatio(copy, channelCount, bitDepth == 32 ? 24 : bitDepth); // a float session exports as 24-bit
                FlacRatio = flacSamples == 0 ? ratio : (FlacRatio * flacSamples + ratio) / (flacSamples + 1); flacSamples = Math.Min(flacSamples + 1, 200);
                if (Session != null) { lock (Quality) Quality.Add(new QualitySample { Frame = at, Hz = cutoff ?? 0 }); }
                QualityMeasured?.Invoke();
            }
            finally { qualityBusy = false; }
        });
    }

    // Each callback: wall-clock time passed against audio delivered. Normal jitter cancels out; a loss shows as a shortfall that never comes back.
    // A tiny allowance covers the two clocks drifting apart over hours (hundreds of ppm), which is far slower than any real dropout
    void NoteTiming(int frames)
    {
        var now = wallClock.Elapsed.TotalSeconds; var wallFrames = (now - lastWallSeconds) * sampleRate; lastWallSeconds = now;
        if (!detecting) { if (now > 2 && (silenceKeeper != null || capture is not LoopbackCapture)) { detecting = true; shortfallFrames = 0; } return; }
        shortfallFrames += wallFrames - frames;
        shortfallFrames -= frames * 0.0003; // clock drift allowance
        if (shortfallFrames < -0.5 * sampleRate) shortfallFrames = -0.5 * sampleRate; // delivered early (buffered start): no credit beyond half a second
        if (shortfallFrames > 0.06 * sampleRate)
        {
            var lost = (long)shortfallFrames; shortfallFrames = 0;
            if (Session != null) { lock (Dropouts) Dropouts.Add(new Dropout { Frame = FramesWritten, Ms = (int)(lost * 1000 / sampleRate) }); DropoutFound?.Invoke(); }
        }
    }

    // Writer thread: the queued bytes to disk, the WAV lengths every couple of seconds (a crash still leaves a playable file)
    void WriteLoop()
    {
        var queue = writeQueue!;
        try
        {
            foreach (var (bytes, count) in queue.GetConsumingEnumerable())
            {
                try { output!.Write(bytes, 0, count); } finally { ArrayPool<byte>.Shared.Return(bytes); }
                if (DateTime.UtcNow - lastHeaderUpdate > TimeSpan.FromSeconds(2)) { WriteLengths(); lastHeaderUpdate = DateTime.UtcNow; }
                if (output!.Position > SizeLimit) { Problem = "The recording reached the WAV size limit (4 GB) and was stopped."; queue.CompleteAdding(); _ = Task.Run(() => { Stop(); Stopped?.Invoke(); }); return; }
            }
        }
        catch (IOException writeError) { Problem = writeError.Message; queue.CompleteAdding(); _ = Task.Run(() => { Stop(); Stopped?.Invoke(); }); }
        catch (ObjectDisposedException) { }
    }

    void WriteLengths()
    {
        if (output == null || headerWriter == null) return;
        var end = output.Position; var dataLength = end - dataStart;
        output.Position = 4; headerWriter.Write((uint)(end - 8));
        output.Position = dataStart - 4; headerWriter.Write((uint)dataLength);
        headerWriter.Flush(); output.Position = end; output.Flush(true);
    }

    // Silence (SPEC.md › Recording › Auto-stop): below -70 dBFS counts as nothing playing
    const float SilenceLevel = 0.000316f;
    long lastSoundFrame;
    public long LastSoundFrame => Interlocked.Read(ref lastSoundFrame);
    public double SilentSeconds => sampleRate == 0 || !Recording ? 0 : Math.Max(0, FramesWritten - LastSoundFrame) / (double)sampleRate;
    public bool KeepThroughSilence { get; set; } // the user chose to keep recording through this silence

    // Peak per channel since the last call (0..1+), for the level check
    public float[] TakePeaks() { lock (meterLock) { var taken = heldPeaks.ToArray(); Array.Clear(heldPeaks); return taken; } }

    public double SecondsRecorded => sampleRate == 0 ? 0 : (double)FramesWritten / sampleRate;
    public long BytesWritten => FramesWritten * channels * (bitDepth / 8);
    public double MinutesLeftUnderLimit => sampleRate == 0 ? 0 : (SizeLimit - BytesWritten) / (double)(sampleRate * channels * (bitDepth / 8)) / 60;

    // Finishes the file; returns the session (peaks saved) or null when nothing was recording
    public Session? Stop()
    {
        var stoppedCapture = capture; capture = null;
        try { stoppedCapture?.StopRecording(); } catch { }
        try { stoppedCapture?.Dispose(); } catch { }
        try { silenceKeeper?.Stop(); silenceKeeper?.Dispose(); } catch { }
        silenceKeeper = null;
        var session = Session; var peaks = Peaks; Session = null;
        var queue = writeQueue; writeQueue = null;
        if (queue != null)
        {
            if (!queue.IsAddingCompleted) queue.CompleteAdding();
            writerThread?.Join(TimeSpan.FromSeconds(30)); writerThread = null; // everything queued reaches the disk
            queue.Dispose();
        }
        if (output != null) { try { WriteLengths(); } catch { } headerWriter?.Dispose(); output.Dispose(); output = null; headerWriter = null; }
        if (session == null) return null;
        peaks?.Finish();
        session.TotalFrames = FramesWritten; session.Recording = false; session.ClippedSamples = (int)Math.Min(int.MaxValue, ClippedSamples);
        lock (Dropouts) session.Dropouts = Dropouts.ToList();
        lock (Quality) session.Quality = Quality.ToList();
        lock (Clips) session.Clips = Clips.ToList();
        try { peaks?.Save(session.PeaksPath); } catch { }
        session.Save();
        return session;
    }

    // Crash safety (SPEC.md › Recording › Crash recovery): the audio reaches the disk every 2 s; this keeps the session's notes as current
    public void Checkpoint()
    {
        if (Session is not { } session) return;
        session.TotalFrames = FramesWritten; session.ClippedSamples = (int)Math.Min(int.MaxValue, ClippedSamples);
        lock (Dropouts) session.Dropouts = Dropouts.ToList();
        lock (Quality) session.Quality = Quality.ToList();
        lock (Clips) session.Clips = Clips.ToList();
        session.Save();
    }

    public void Dispose() => Stop();
}
