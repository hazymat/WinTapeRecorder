using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Windows.Media.Control;
using WinTapeRecorder.Settings;
namespace WinTapeRecorder.Detection;

// One place a track name can come from: a media session Windows knows about, or a browser window's title
// Everything Windows' media info gives is kept (SPEC.md › Recording › Now playing), so the Record view can show exactly what arrives
public record NameSource(string Key, string Kind, string App, string Label, string Artist, string Title, string Album, bool Playing, string Rule, string AlbumArtist = "", int TrackNumber = 0, int TrackCount = 0, string Subtitle = "", string Genres = "", bool Picture = false)
{
    public bool HasName => Title != "";
    public string NameKey => Artist + "\u0001" + Title;
}

// Generic now-playing detection (SPEC.md › Track detection): Windows' media info and browser window titles through the user's rules.
// Media sessions are followed through Windows' own change events (title, playback, current session), so a new track shows within a moment;
// a slower poll is only a safety net, and every Windows call has a time limit so one stuck app can't freeze the rest.
public sealed class NowPlayingWatcher
{
    GlobalSystemMediaTransportControlsSessionManager? mediaManager;
    readonly Dictionary<string, GlobalSystemMediaTransportControlsSession> mediaSessions = new(); // kept so their events stay alive
    readonly Dictionary<string, NameSource> mediaSources = new();
    readonly Dictionary<string, DateTime> startedPlayingAt = new();
    readonly Dictionary<string, DateTime> lastPlayingAt = new(); // players flicker through "changing" or "paused" between tracks
    string currentMediaKey = "";
    List<NameSource> windowSources = new();
    readonly Dictionary<int, string> processNames = new();
    readonly object sourcesLock = new();
    public List<NameSource> Sources { get { lock (sourcesLock) return mediaSources.Values.Concat(windowSources).ToList(); } }
    public event Action? Updated;
    readonly System.Threading.Timer windowTimer, mediaTimer;
    bool windowsBusy, mediaBusy;
    readonly Func<AppSettings> settings;

    public NowPlayingWatcher(Func<AppSettings> settingsProvider)
    {
        settings = settingsProvider;
        windowTimer = new System.Threading.Timer(_ => PollWindows(), null, 300, 500);
        mediaTimer = new System.Threading.Timer(_ => _ = RefreshAllMediaAsync(), null, 200, 2000);
    }

    static async Task<T?> Limited<T>(Windows.Foundation.IAsyncOperation<T> operation) where T : class
    {
        var task = operation.AsTask();
        return await Task.WhenAny(task, Task.Delay(2000)) == task ? await task : null;
    }

    void PollWindows()
    {
        if (windowsBusy) return;
        windowsBusy = true;
        try { var found = WindowSources(settings()); lock (sourcesLock) windowSources = found; Updated?.Invoke(); }
        catch { }
        finally { windowsBusy = false; }
    }

    async Task RefreshAllMediaAsync()
    {
        if (mediaBusy) return;
        mediaBusy = true;
        try
        {
            if (mediaManager == null)
            {
                mediaManager = await Limited(GlobalSystemMediaTransportControlsSessionManager.RequestAsync());
                if (mediaManager == null) return;
                mediaManager.SessionsChanged += (sender, args) => _ = RefreshAllMediaAsync();
                mediaManager.CurrentSessionChanged += (sender, args) => { NoteCurrentSession(); Updated?.Invoke(); };
            }
            var sessions = mediaManager.GetSessions().ToList();
            var keys = sessions.Select(session => "media:" + session.SourceAppUserModelId).ToHashSet();
            lock (sourcesLock) foreach (var gone in mediaSources.Keys.Where(key => !keys.Contains(key)).ToList()) { mediaSources.Remove(gone); mediaSessions.Remove(gone); startedPlayingAt.Remove(gone); }
            foreach (var session in sessions) { Hook(session); await RefreshMediaAsync(session, false); }
            NoteCurrentSession();
            Updated?.Invoke();
        }
        catch { }
        finally { mediaBusy = false; }
    }

    void Hook(GlobalSystemMediaTransportControlsSession session)
    {
        var key = "media:" + session.SourceAppUserModelId;
        lock (sourcesLock) { if (mediaSessions.TryGetValue(key, out var known) && ReferenceEquals(known, session)) return; mediaSessions[key] = session; }
        session.MediaPropertiesChanged += (sender, args) => _ = RefreshMediaAsync(sender, true);
        session.PlaybackInfoChanged += (sender, args) => _ = RefreshMediaAsync(sender, true);
    }

    void NoteCurrentSession() { try { currentMediaKey = mediaManager?.GetCurrentSession() is { } current ? "media:" + current.SourceAppUserModelId : ""; } catch { } }

    async Task RefreshMediaAsync(GlobalSystemMediaTransportControlsSession session, bool announce)
    {
        try
        {
            var properties = await Limited(session.TryGetMediaPropertiesAsync());
            var playing = session.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            var key = "media:" + session.SourceAppUserModelId; var app = FriendlyApp(session.SourceAppUserModelId);
            var source = new NameSource(key, "media", app, $"Media info: {app}", properties?.Artist?.Trim() ?? "", properties?.Title?.Trim() ?? "", properties?.AlbumTitle?.Trim() ?? "", playing, "",
                properties?.AlbumArtist?.Trim() ?? "", properties?.TrackNumber ?? 0, properties?.AlbumTrackCount ?? 0, properties?.Subtitle?.Trim() ?? "", properties?.Genres is { Count: > 0 } genres ? string.Join(", ", genres) : "", properties?.Thumbnail != null);
            lock (sourcesLock)
            {
                var wasPlaying = mediaSources.TryGetValue(key, out var previous) && previous.Playing;
                if (playing && !wasPlaying) startedPlayingAt[key] = DateTime.UtcNow;
                if (playing) lastPlayingAt[key] = DateTime.UtcNow;
                if (!playing) startedPlayingAt.Remove(key);
                // Players blank the title for a moment between tracks: keep the last name until the next one arrives, so nothing else is picked meanwhile
                var blank = properties == null || source.Title == "";
                mediaSources[key] = blank && previous != null && playing ? previous with { Playing = playing } : source;
            }
            if (announce) { NoteCurrentSession(); Updated?.Invoke(); }
        }
        catch { }
    }

    static string FriendlyApp(string appId)
    {
        // "chrome", "MSEdge", a full exe path, "Publisher.App_hash!App" or "velopack.AppName": the app's own name
        var name = appId.Split('!').Last().Split('\\').Last();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        else if (name.Contains('.')) name = name.Split('.').Last();
        return name switch { "msedge" or "MSEdge" => "Edge", "chrome" or "Chrome" => "Chrome", "firefox" or "308046B0AF4A39CB" => "Firefox", "" => "App", _ => name };
    }

    // ---- Browser windows ----
    delegate bool EnumWindowsCallback(IntPtr windowHandle, IntPtr parameter);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr windowHandle);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr windowHandle, StringBuilder text, int maxLength);
    [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr windowHandle);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out int processId);

    List<NameSource> WindowSources(AppSettings appSettings)
    {
        var browsers = appSettings.BrowserProcesses.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(name => name.Trim().ToLowerInvariant().Replace(".exe", "")).ToHashSet();
        var windows = new List<(IntPtr handle, string title, string process)>();
        EnumWindows((windowHandle, parameter) =>
        {
            if (!IsWindowVisible(windowHandle)) return true;
            var length = GetWindowTextLength(windowHandle); if (length == 0) return true;
            var text = new StringBuilder(length + 1); GetWindowText(windowHandle, text, text.Capacity);
            GetWindowThreadProcessId(windowHandle, out var processId);
            var process = ProcessName(processId);
            if (browsers.Contains(process)) windows.Add((windowHandle, text.ToString(), process));
            return true;
        }, IntPtr.Zero);
        return windows.Select(window =>
        {
            var (artist, title, album, rule) = ApplyRules(window.title, appSettings.DetectionRules);
            return new NameSource("window:" + window.handle, "window", window.process, $"Window: {CleanTitle(window.title)}", artist, title, album, false, rule);
        }).ToList();
    }

    string ProcessName(int processId)
    {
        if (processNames.TryGetValue(processId, out var cached)) return cached;
        var name = ""; try { using var process = Process.GetProcessById(processId); name = process.ProcessName.ToLowerInvariant(); } catch { }
        if (processNames.Count > 500) processNames.Clear();
        return processNames[processId] = name;
    }

    // The page's own title: no browser name, profile, "and 3 more pages", unread counts or playing marks
    static readonly Regex browserSuffix = new(@"\s+[-–—]\s+(?:Google Chrome|Mozilla Firefox|Microsoft.?Edge|Brave|Opera|Vivaldi|Chromium)\s*$", RegexOptions.Compiled);
    static readonly Regex morePages = new(@"\s+and \d+ more pages?(?:\s+[-–—]\s+.*)?$", RegexOptions.Compiled);
    static readonly Regex leadingNoise = new(@"^(?:\(\d+\)\s*|[🔊🔉🔈♪▶►]\s*)+", RegexOptions.Compiled);
    public static string CleanTitle(string windowTitle) => leadingNoise.Replace(morePages.Replace(browserSuffix.Replace(windowTitle.Replace("​", ""), ""), ""), "").Trim();

    // First enabled rule that matches gives artist, title and album; none: the whole title, no artist
    public static (string artist, string title, string album, string rule) ApplyRules(string windowTitle, List<DetectionRule> rules)
    {
        var cleaned = CleanTitle(windowTitle);
        foreach (var rule in rules.Where(rule => rule.Enabled && rule.Pattern.Trim() != ""))
        {
            Match match;
            try { match = Regex.Match(cleaned, rule.Pattern, RegexOptions.None, TimeSpan.FromMilliseconds(50)); } catch { continue; }
            if (!match.Success || match.Groups["title"].Value.Trim() == "") continue;
            return (match.Groups["artist"].Value.Trim(), match.Groups["title"].Value.Trim(), match.Groups["album"].Value.Trim(), rule.Name);
        }
        return ("", cleaned, "", "");
    }

    // Which name a recording follows: a chosen source, or "auto": the playing media session Windows calls current, else the one that started
    // playing most recently (so switching players follows at once, and a paused one drops out), else a window a rule recognises
    public NameSource? Current(string sourceKey, RecordingProfileSnapshot profile)
    {
        var sources = Sources;
        if (sourceKey == "none") return null;
        if (sourceKey != "auto") { var chosen = sources.FirstOrDefault(source => source.Key == sourceKey); return chosen != null && Allowed(chosen, profile) ? chosen : null; }
        if (profile.UseMediaInfo)
        {
            var playing = sources.Where(source => source.Kind == "media" && source.Playing && source.HasName).ToList();
            DateTime StartedAt(NameSource source) { lock (sourcesLock) return startedPlayingAt.TryGetValue(source.Key, out var at) ? at : DateTime.MinValue; }
            var best = playing.FirstOrDefault(source => source.Key == currentMediaKey) ?? playing.OrderByDescending(StartedAt).FirstOrDefault();
            if (best != null) return best;
            // Between tracks a player can briefly stop saying "playing": stay with it rather than jumping to a window title
            DateTime LastPlayed(NameSource source) { lock (sourcesLock) return lastPlayingAt.TryGetValue(source.Key, out var at) ? at : DateTime.MinValue; }
            var recent = sources.Where(source => source.Kind == "media" && source.HasName && DateTime.UtcNow - LastPlayed(source) < TimeSpan.FromSeconds(15)).OrderByDescending(LastPlayed).FirstOrDefault();
            if (recent != null) return recent;
        }
        return profile.UseWindowTitles ? sources.FirstOrDefault(source => source.Kind == "window" && source.Rule != "") : null;
    }

    static bool Allowed(NameSource source, RecordingProfileSnapshot profile) => source.Kind == "media" ? profile.UseMediaInfo : profile.UseWindowTitles;
}

public record RecordingProfileSnapshot(bool UseMediaInfo, bool UseWindowTitles);
