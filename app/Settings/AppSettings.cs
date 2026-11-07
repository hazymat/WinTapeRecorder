using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
namespace WinTapeRecorder.Settings;

// Gap detection (SPEC.md › Track detection): silence below the threshold for MinGapMs is a gap
public class GapSettings
{
    public double ThresholdDb { get; set; } = -50; // peak level counted as silence
    public int MinGapMs { get; set; } = 1200;
    public int AttackMs { get; set; } = 30; // sound must last this long to count as a track starting
    public int ReleaseMs { get; set; } = 400; // silence must last this long to count as a track ending
    public int PreRollMs { get; set; } = 150; // kept before the first sound
    public int PostRollMs { get; set; } = 400; // kept after the last sound
    public int SnapWindowMs { get; set; } = 8000; // a name change looks this far either side for the gap
    public bool TrimSilence { get; set; } = true; // trim each track's leading and trailing silence
    public bool EndsMeetNextStart { get; set; } = true; // a track ends where the next one starts (no gap cut out between them); only the session's own ends are trimmed
    public int MinTrackSeconds { get; set; } = 20; // shorter pieces are dropped (adverts, stray clicks)
}

public class RecordingProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Default";
    public string DeviceId { get; set; } = ""; // empty = Windows default output, recorded as loopback
    public string DeviceName { get; set; } = "";
    public bool Loopback { get; set; } = true; // true: record what an output device plays; false: an input
    public int BitDepth { get; set; } = 24; // 16, 24 or 32 (float)
    public string SessionsFolder { get; set; } = ""; // empty = Music\WinTapeRecorder sessions
    public bool UseMediaInfo { get; set; } = true; // Windows' now-playing info
    public bool UseWindowTitles { get; set; } = true; // browser window titles through the detection rules
    public bool SplitOnGaps { get; set; } = true; // with no names, split on gaps
    public int LatencyMs { get; set; } = 0; // a name change is marked this much earlier
    public int StopAfterSilenceMinutes { get; set; } = 5; // recording stops by itself after this long below -70 dBFS; 0 = never
    public GapSettings Gaps { get; set; } = new();
    public override string ToString() => Name;
}

public class OutputProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "FLAC";
    public string Format { get; set; } = "flac"; // flac | wav | mp3 | m4a
    public int BitrateKbps { get; set; } = 320; // mp3 and m4a
    public string Folder { get; set; } = ""; // empty = Music\WinTapeRecorder
    public string Template { get; set; } = "{albumartist} - {album}/{artist} - {album} - {track:00} {title}";
    public string Album { get; set; } = "";
    public string AlbumArtist { get; set; } = "";
    public string Genre { get; set; } = "";
    public string Comment { get; set; } = "";
    public bool EmbedCover { get; set; } = true; // the cover inside each file's tags
    public bool SaveCoverFile { get; set; } = true; // and as a picture beside the tracks
    public string CoverFileName { get; set; } = "folder"; // without extension: .jpg or .png follows the picture
    public override string ToString() => Name;
}

// A browser window title turned into artist and title (SPEC.md › Track detection); the pattern's named groups are artist, title, album
public class DetectionRule
{
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string Pattern { get; set; } = "";
    public override string ToString() => (Enabled ? "" : "(off) ") + Name;
    public static List<DetectionRule> Examples() => new()
    {
        new() { Name = "YouTube", Pattern = @"^(?:\(\d+\)\s*)?(?:(?<artist>.+?)\s+[-–—]\s+)?(?<title>.+?) - YouTube\b" },
        new() { Name = "Bandcamp", Pattern = @"^(?<title>[^|]+?) \| (?<artist>[^|]+?)(?: [-–—] [^|]*)?$" },
        new() { Name = "Lyrion web player", Pattern = @"^(?<title>.+?) [•·] (?<artist>.+?)(?: [•·] .*)?$" },
    };
}

public class GeneralSettings
{
    public bool AutoUpdate { get; set; } = true;
    public string UpdateChannel { get; set; } = "stable"; // stable | dev
    public bool Glass { get; set; } = true; // Windows 11 acrylic backdrop behind a see-through UI
    public bool ShowTitleBar { get; set; } // frameless by default
    [JsonIgnore] public bool Frameless { get => !ShowTitleBar; set => ShowTitleBar = !value; }
    public bool HideHints { get; set; }
    public List<string> DismissedHints { get; set; } = new();
    public Dictionary<string, double[]> WindowBounds { get; set; } = new(); // per monitor layout: left, top, width, height, maximised
    public double UiScale { get; set; } = 1;
    public string Skin { get; set; } = "auto"; // auto | dark | light
    public string AccentColour { get; set; } = "";
    public int GlassTransparency { get; set; } = -1; // 0..100, -1 = default
    public bool GlassWhenInactive { get; set; } = true;
    public int GlassTintHue { get; set; } = 0; // 0 = neutral
    public string LastRunVersion { get; set; } = "";
    public string DiscogsToken { get; set; } = ""; // the user's own, for Discogs lookups
}

public class AudioSettings
{
    public string AuditionDeviceId { get; set; } = ""; // empty = Windows default output
    public int AuditionSeconds { get; set; } = 5;
}

public class AppSettings
{
    public GeneralSettings General { get; set; } = new();
    public AudioSettings Audio { get; set; } = new();
    public List<RecordingProfile> RecordingProfiles { get; set; } = new();
    public string ActiveRecordingProfileId { get; set; } = "";
    public List<OutputProfile> OutputProfiles { get; set; } = new();
    public string ActiveOutputProfileId { get; set; } = "";
    public List<DetectionRule> DetectionRules { get; set; } = DetectionRule.Examples();
    public string BrowserProcesses { get; set; } = "chrome, msedge, firefox, brave, opera, vivaldi, chromium";

    [JsonIgnore] public RecordingProfile ActiveRecordingProfile => RecordingProfiles.FirstOrDefault(profile => profile.Id == ActiveRecordingProfileId) ?? RecordingProfiles[0];
    [JsonIgnore] public OutputProfile ActiveOutputProfile => OutputProfiles.FirstOrDefault(profile => profile.Id == ActiveOutputProfileId) ?? OutputProfiles[0];
    public RecordingProfile RecordingProfileOrActive(string id) => RecordingProfiles.FirstOrDefault(profile => profile.Id == id) ?? ActiveRecordingProfile;
    public OutputProfile OutputProfileOrActive(string id) => OutputProfiles.FirstOrDefault(profile => profile.Id == id) ?? ActiveOutputProfile;

    static readonly JsonSerializerOptions jsonOptions = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public static AppSettings Load()
    {
        AppSettings settings;
        try { settings = File.Exists(AppPaths.SettingsFile) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile), jsonOptions) ?? new() : new(); }
        catch { settings = new(); }
        settings.EnsureDefaults();
        return settings;
    }

    // There is always at least one profile of each kind
    void EnsureDefaults()
    {
        if (RecordingProfiles.Count == 0) RecordingProfiles.Add(new RecordingProfile());
        // Profiles made by the first builds used the session name; the album is the folder now
        foreach (var profile in OutputProfiles) { if (profile.Template == "{session}/{track:00} {artist} - {title}") profile.Template = new OutputProfile().Template; if (profile.Album == "{session}") profile.Album = ""; }
        if (OutputProfiles.Count == 0) { OutputProfiles.Add(new OutputProfile()); OutputProfiles.Add(new OutputProfile { Name = "MP3 320", Format = "mp3", BitrateKbps = 320 }); }
    }

    public void Save()
    {
        Directory.CreateDirectory(AppPaths.DataFolder);
        var tempFile = AppPaths.SettingsFile + ".tmp";
        File.WriteAllText(tempFile, JsonSerializer.Serialize(this, jsonOptions));
        File.Move(tempFile, AppPaths.SettingsFile, true);
    }

    public string ExportJson() => JsonSerializer.Serialize(this, jsonOptions);

    public static AppSettings Import(string importJson)
    {
        var imported = JsonSerializer.Deserialize<AppSettings>(importJson, jsonOptions) ?? throw new InvalidDataException("Not a WinTapeRecorder settings file.");
        imported.EnsureDefaults();
        return imported;
    }
}
