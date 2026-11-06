using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
namespace WinTapeRecorder.Audio;

// Plays parts of a session (SPEC.md › Auditioning): from a point, or a run of track starts and ends with a short pause between
public sealed class AuditionPlayer : IDisposable
{
    WasapiOut? output;
    SegmentReader? reader;
    double outputToSourceRatio = 1;
    int outputBlockAlign = 1;
    public string SessionId { get; private set; } = "";
    public bool Playing => output?.PlaybackState == PlaybackState.Playing;
    public event Action? Finished;

    public void Play(SessionAudio audio, string sessionId, List<(long start, long end)> segments, Func<long, float>? gainAt, string deviceId)
    {
        Stop();
        var device = AudioDevices.Find(deviceId, "", true) ?? AudioDevices.Find("", "", true) ?? throw new InvalidOperationException("No output device for auditioning.");
        var mixFormat = device.AudioClient.MixFormat;
        reader = new SegmentReader(audio, segments, gainAt);
        ISampleProvider source = reader;
        if (audio.Channels == 1 && mixFormat.Channels >= 2) source = new MonoToStereoSampleProvider(source);
        else if (audio.Channels > 2 && mixFormat.Channels == 2) source = new MultiplexingSampleProvider(new[] { source }, 2);
        if (source.WaveFormat.SampleRate != mixFormat.SampleRate) source = new WdlResamplingSampleProvider(source, mixFormat.SampleRate);
        outputToSourceRatio = (double)audio.SampleRate / source.WaveFormat.SampleRate;
        outputBlockAlign = source.WaveFormat.BlockAlign;
        output = new WasapiOut(device, AudioClientShareMode.Shared, true, 80);
        output.Init(source);
        var playingOutput = output;
        output.PlaybackStopped += (sender, args) => { if (playingOutput == output) Finished?.Invoke(); };
        SessionId = sessionId;
        output.Play();
    }

    // The frame being heard now (what the device has played, mapped back through the segments), or -1
    public long Position
    {
        get
        {
            if (output == null || reader == null) return -1;
            long playedBytes; try { playedBytes = output.GetPosition(); } catch { return -1; }
            return reader.SourceFrameAt((long)(playedBytes / outputBlockAlign * outputToSourceRatio));
        }
    }

    public void Stop()
    {
        var stopping = output; output = null; reader = null;
        try { stopping?.Stop(); } catch { }
        stopping?.Dispose();
        SessionId = "";
    }

    public void Dispose() => Stop();
}

// Reads the segments in order as one stream, with 400 ms of silence between them, remembering where each output frame came from
sealed class SegmentReader : ISampleProvider
{
    readonly SessionAudio audio;
    readonly List<(long start, long end)> segments;
    readonly Func<long, float>? gainAt;
    readonly int pauseFrames;
    int segmentIndex; long sourceFrame; long pauseLeft; long outputFrames;
    readonly List<(long outputFrame, long sourceFrame, bool pause)> checkpoints = new();
    readonly object checkpointLock = new();
    float[] readBuffer = Array.Empty<float>();
    public WaveFormat WaveFormat { get; }

    public SegmentReader(SessionAudio audio, List<(long start, long end)> segments, Func<long, float>? gainAt)
    {
        this.audio = audio; this.segments = segments.Where(segment => segment.end > segment.start).ToList(); this.gainAt = gainAt;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(audio.SampleRate, audio.Channels);
        pauseFrames = audio.SampleRate * 2 / 5;
        if (this.segments.Count > 0) sourceFrame = this.segments[0].start;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        var channels = audio.Channels; var framesWanted = count / channels; var framesDone = 0;
        while (framesDone < framesWanted && segmentIndex < segments.Count)
        {
            if (pauseLeft > 0)
            {
                var silent = (int)Math.Min(pauseLeft, framesWanted - framesDone);
                Note(true);
                Array.Clear(buffer, offset + framesDone * channels, silent * channels);
                pauseLeft -= silent; framesDone += silent; outputFrames += silent;
                continue;
            }
            var segment = segments[segmentIndex];
            var take = (int)Math.Min(segment.end - sourceFrame, framesWanted - framesDone);
            if (take <= 0)
            {
                segmentIndex++;
                if (segmentIndex < segments.Count) { sourceFrame = segments[segmentIndex].start; pauseLeft = pauseFrames; }
                continue;
            }
            if (readBuffer.Length < take * channels) readBuffer = new float[take * channels];
            Note(false);
            var read = audio.Read(sourceFrame, take, readBuffer);
            if (read <= 0) { segmentIndex = segments.Count; break; }
            for (var frame = 0; frame < read; frame++)
            {
                var gain = gainAt?.Invoke(sourceFrame + frame) ?? 1f;
                for (var channel = 0; channel < channels; channel++) buffer[offset + (framesDone + frame) * channels + channel] = readBuffer[frame * channels + channel] * gain;
            }
            sourceFrame += read; framesDone += read; outputFrames += read;
        }
        return framesDone * channels;
    }

    void Note(bool pause) { lock (checkpointLock) { checkpoints.Add((outputFrames, sourceFrame, pause)); if (checkpoints.Count > 4000) checkpoints.RemoveRange(0, 2000); } }

    public long SourceFrameAt(long playedFrames)
    {
        lock (checkpointLock)
        {
            for (var index = checkpoints.Count - 1; index >= 0; index--)
            {
                var checkpoint = checkpoints[index];
                if (checkpoint.outputFrame <= playedFrames) return checkpoint.pause ? checkpoint.sourceFrame : checkpoint.sourceFrame + (playedFrames - checkpoint.outputFrame);
            }
            return segments.Count > 0 ? segments[0].start : 0;
        }
    }
}
