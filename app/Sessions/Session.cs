using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using WinTapeRecorder.Settings;
namespace WinTapeRecorder.Sessions;

// A now-playing name seen while recording, at the frame it appeared (SPEC.md › Track detection)
public class NameChange
{
    public long Frame { get; set; }
    public string Artist { get; set; } = "";
    public string Title { get; set; } = "";
    public string Album { get; set; } = "";
    public string Source { get; set; } = "";
    public bool Manual { get; set; } // M pressed while recording
}

// Samples the audio engine threw away while recording (the PC was too busy): where, and how much
public class Dropout
{
    public long Frame { get; set; }
    public int Ms { get; set; }
}

public class Track
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public long Start { get; set; }
    public long End { get; set; }
    public string Artist { get; set; } = "";
    public string Title { get; set; } = "";
    public string Album { get; set; } = "";
    public string AlbumArtist { get; set; } = "";
    public string Genre { get; set; } = "";
    public string Year { get; set; } = "";
    public string Composer { get; set; } = "";
    public string Comment { get; set; } = "";
    public int TrackNumber { get; set; } // 0 = its place in the session
    public int TrackCount { get; set; } // 0 = the session's number of tracks
    public string ArtPath { get; set; } = ""; // a picture in the session folder, embedded on export
    public int FadeInMs { get; set; }
    public int FadeOutMs { get; set; }
    public bool Exported { get; set; }
    public string ExportedPath { get; set; } = "";
}

// One recording: audio.wav, peaks.bin and session.json in its own folder (SPEC.md › Sessions)
public class Session
{
    public string Name { get; set; } = "";
    public DateTime Created { get; set; } = DateTime.Now;
    public string Device { get; set; } = "";
    public bool Loopback { get; set; }
    public int SampleRate { get; set; }
    public int Channels { get; set; }
    public int BitDepth { get; set; }
    public long TotalFrames { get; set; }
    public string RecordingProfileId { get; set; } = "";
    public string OutputProfileId { get; set; } = "";
    public bool Recording { get; set; } // still true after a crash mid-recording: the WAV header is repaired on open
    public int ClippedSamples { get; set; }
    public List<NameChange> Changes { get; set; } = new();
    public List<Dropout> Dropouts { get; set; } = new();
    public List<Track> Tracks { get; set; } = new();

    [JsonIgnore] public string Folder { get; set; } = "";
    [JsonIgnore] public string Id => Folder;
    [JsonIgnore] public string AudioPath => Path.Combine(Folder, "audio.wav");
    [JsonIgnore] public string PeaksPath => Path.Combine(Folder, "peaks.bin");
    [JsonIgnore] string JsonPath => Path.Combine(Folder, "session.json");

    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public void Save()
    {
        var tempFile = JsonPath + ".tmp";
        File.WriteAllText(tempFile, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(tempFile, JsonPath, true);
    }

    public static Session? Load(string folder)
    {
        try
        {
            var session = JsonSerializer.Deserialize<Session>(File.ReadAllText(Path.Combine(folder, "session.json")), JsonOptions);
            if (session != null) session.Folder = folder;
            return session;
        }
        catch { return null; }
    }
}

public static class SessionStore
{
    public static string FolderFor(RecordingProfile profile) => profile.SessionsFolder.Trim() == "" ? AppPaths.DefaultSessionsFolder : profile.SessionsFolder.Trim();

    // Every session in every recording profile's folder, newest first
    public static List<Session> List(AppSettings settings)
    {
        var sessions = new List<Session>();
        foreach (var folder in settings.RecordingProfiles.Select(FolderFor).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(folder)) continue;
            foreach (var sessionFolder in Directory.GetDirectories(folder))
                if (File.Exists(Path.Combine(sessionFolder, "session.json")) && Session.Load(sessionFolder) is { } session) sessions.Add(session);
        }
        return sessions.OrderByDescending(session => session.Created).ToList();
    }

    public static Session Create(RecordingProfile profile, string name)
    {
        var created = DateTime.Now;
        var folderName = created.ToString("yyyy-MM-dd HH.mm.ss");
        var folder = Path.Combine(FolderFor(profile), folderName);
        Directory.CreateDirectory(folder);
        var session = new Session { Name = name == "" ? created.ToString(@"d MMM yyyy, HH\:mm") : name, Created = created, Folder = folder, RecordingProfileId = profile.Id, Recording = true };
        session.Save();
        return session;
    }

    // Close session (SPEC.md › Sessions): the whole folder goes, WAV included. Exported files live elsewhere and are never touched
    public static void Delete(Session session)
    {
        if (!File.Exists(Path.Combine(session.Folder, "session.json"))) throw new InvalidOperationException("That folder isn't a session.");
        Directory.Delete(session.Folder, true);
    }
}
