using System.Diagnostics;
using NAudio.CoreAudioApi;
using WinTapeRecorder.Sessions;
namespace WinTapeRecorder.Audio;

// Other apps' sounds while recording an output device (SPEC.md › Recording › Other sounds). Windows meters every app's audio
// session on a device separately, so each one is watched ten times a second. The app that has been audible longest is the one
// being recorded; any other app (or Windows' own system sounds) going above -40 dBFS is noted with where and for how long
public sealed class OtherSounds : IDisposable
{
    const float Audible = 0.01f; // -40 dBFS
    readonly MMDevice device;
    readonly Func<long> framesNow;
    readonly int sampleRate;
    readonly Thread thread;
    volatile bool stopping;
    readonly Dictionary<string, double> audibleSeconds = new(); // per app, this recording
    readonly Dictionary<string, (long frame, DateTime last, float peak)> open = new(); // a sound still going, per app
    public List<OtherSound> Found { get; } = new();

    public OtherSounds(MMDevice device, int sampleRate, Func<long> framesNow)
    {
        this.device = device; this.sampleRate = sampleRate; this.framesNow = framesNow;
        thread = new Thread(Run) { IsBackground = true, Name = "Other sounds" };
        thread.Start();
    }

    void Run()
    {
        var names = new Dictionary<uint, string>();
        var refreshed = DateTime.MinValue;
        AudioSessionManager? manager = null;
        try { manager = device.AudioSessionManager; } catch { return; }
        while (!stopping)
        {
            try
            {
                if (DateTime.UtcNow - refreshed > TimeSpan.FromSeconds(2)) { manager.RefreshSessions(); refreshed = DateTime.UtcNow; }
                var sessions = manager.Sessions; var heard = new List<(string app, float peak)>();
                var own = (uint)Environment.ProcessId;
                for (var index = 0; index < sessions.Count; index++)
                {
                    var session = sessions[index];
                    uint process; try { process = session.GetProcessID; } catch { continue; }
                    if (process == own) continue;
                    float peak; try { peak = session.AudioMeterInformation.MasterPeakValue; } catch { continue; }
                    if (peak < Audible) continue;
                    var app = session.IsSystemSoundsSession ? "Windows system sounds" : names.TryGetValue(process, out var known) ? known : names[process] = AppName(process, session);
                    heard.Add((app, peak));
                }
                lock (Found) Note(heard);
            }
            catch { }
            Thread.Sleep(100);
        }
        lock (Found) Close(DateTime.MaxValue);
    }

    static string AppName(uint process, AudioSessionControl session)
    {
        try { if (session.DisplayName is { Length: > 0 } display && !display.StartsWith('@')) return display; } catch { }
        try { using var running = Process.GetProcessById((int)process); return running.MainModule?.FileVersionInfo.FileDescription is { Length: > 0 } description ? description : running.ProcessName; } catch { }
        try { using var running = Process.GetProcessById((int)process); return running.ProcessName; } catch { return $"process {process}"; }
    }

    void Note(List<(string app, float peak)> heard)
    {
        foreach (var (app, _) in heard) audibleSeconds[app] = audibleSeconds.GetValueOrDefault(app) + 0.1;
        var main = audibleSeconds.Count == 0 ? "" : audibleSeconds.MaxBy(pair => pair.Value).Key;
        var now = DateTime.UtcNow; var frame = framesNow();
        foreach (var (app, peak) in heard)
        {
            if (app == main && audibleSeconds[app] > 3) continue; // the player being recorded
            open[app] = open.TryGetValue(app, out var going) ? (going.frame, now, Math.Max(going.peak, peak)) : (frame, now, peak);
        }
        Close(now - TimeSpan.FromSeconds(1));
    }

    // A sound is over once its app has been quiet for a second
    void Close(DateTime quietSince)
    {
        foreach (var (app, sound) in open.ToList())
        {
            if (sound.last > quietSince) continue;
            open.Remove(app);
            var length = framesNow() - sound.frame - (quietSince == DateTime.MaxValue ? 0 : sampleRate); // minus the quiet second
            Found.Add(new OtherSound { Frame = sound.frame, Ms = (int)Math.Max(100, length * 1000 / sampleRate), App = app, PeakDb = (int)Math.Round(20 * Math.Log10(Math.Max(sound.peak, 1e-6f))) });
        }
    }

    public List<OtherSound> Snapshot() { lock (Found) return Found.ToList(); }

    public void Dispose() { stopping = true; thread.Join(1000); }
}
