using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Windows.Media.Control;
using WinTapeRecorder.Settings;
namespace WinTapeRecorder.Detection;

// One place a track name can come from: a media session Windows knows about, or a browser window's title
public record NameSource(string Key, string Kind, string App, string Label, string Artist, string Title, string Album, bool Playing, string Rule)
{
    public bool HasName => Title != "";
    public string NameKey => Artist + "\u0001" + Title;
}

// Generic now-playing detection (SPEC.md › Track detection): Windows' media info and browser window titles through the user's rules
public sealed class NowPlayingWatcher
{
    GlobalSystemMediaTransportControlsSessionManager? mediaManager;
    readonly Dictionary<int, string> processNames = new();
    public List<NameSource> Sources { get; private set; } = new();
    public event Action? Updated;
    readonly System.Threading.Timer pollTimer;
    bool polling;
    Func<AppSettings> settings;

    public NowPlayingWatcher(Func<AppSettings> settingsProvider)
    {
        settings = settingsProvider;
        pollTimer = new System.Threading.Timer(_ => _ = PollAsync(), null, 300, 700);
    }

    async Task PollAsync()
    {
        if (polling) return;
        polling = true;
        try
        {
            var found = new List<NameSource>();
            try { found.AddRange(await MediaSourcesAsync()); } catch { }
            try { found.AddRange(WindowSources(settings())); } catch { }
            Sources = found;
            Updated?.Invoke();
        }
        finally { polling = false; }
    }

    async Task<List<NameSource>> MediaSourcesAsync()
    {
        mediaManager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        var sources = new List<NameSource>();
        foreach (var mediaSession in mediaManager.GetSessions())
        {
            try
            {
                var properties = await mediaSession.TryGetMediaPropertiesAsync();
                var playing = mediaSession.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                var app = FriendlyApp(mediaSession.SourceAppUserModelId);
                var title = properties?.Title?.Trim() ?? ""; var artist = properties?.Artist?.Trim() ?? ""; var album = properties?.AlbumTitle?.Trim() ?? "";
                sources.Add(new NameSource("media:" + mediaSession.SourceAppUserModelId, "media", app, $"Media info: {app}", artist, title, album, playing, ""));
            }
            catch { }
        }
        return sources;
    }

    static string FriendlyApp(string appId)
    {
        var name = appId.Split('!').Last(); name = Path.GetFileNameWithoutExtension(name.Split('\\').Last());
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

    // Which name a recording follows: a chosen source, or "auto" (a playing media session, else a window a rule recognises)
    public NameSource? Current(string sourceKey, RecordingProfileSnapshot profile)
    {
        var sources = Sources;
        if (sourceKey == "none") return null;
        if (sourceKey != "auto") { var chosen = sources.FirstOrDefault(source => source.Key == sourceKey); return chosen != null && Allowed(chosen, profile) ? chosen : null; }
        return sources.FirstOrDefault(source => source.Kind == "media" && source.Playing && source.HasName && profile.UseMediaInfo)
            ?? sources.FirstOrDefault(source => source.Kind == "window" && source.Rule != "" && profile.UseWindowTitles);
    }

    static bool Allowed(NameSource source, RecordingProfileSnapshot profile) => source.Kind == "media" ? profile.UseMediaInfo : profile.UseWindowTitles;
}

public record RecordingProfileSnapshot(bool UseMediaInfo, bool UseWindowTitles);
