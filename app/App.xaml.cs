using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using WinTapeRecorder.Audio;
using WinTapeRecorder.Detection;
using WinTapeRecorder.Sessions;
using WinTapeRecorder.Settings;
using WinTapeRecorder.Shell;
namespace WinTapeRecorder;

public partial class App : Application
{
    public static App Instance => (App)Current;
    public AppSettings Settings { get; private set; } = null!;
    public AppUpdater Updater { get; } = new();
    public Recorder Recorder { get; } = new();
    public AuditionPlayer Player { get; } = new();
    public NowPlayingWatcher Watcher { get; private set; } = null!;
    public bool IsQuitting { get; private set; }
    public bool Exporting { get; set; }
    public string ExportingSessionId { get; set; } = "";
    public int ExportDone { get; set; }
    public int ExportTotal { get; set; }
    public List<string> ExportQueue { get; } = new(); // sessions waiting to export, in turn
    public event Action? StatusChanged;
    public event Action? SessionsChanged;
    public event Action? HintsChanged;
    public event Action? SettingsChanged;
    public event Action<string, string>? Tip;
    MainWindow mainWindow = null!;
    SettingsWindow? settingsWindow;
    Mutex? singleInstanceMutex;
    EventWaitHandle? showWindowSignal;

    // Which name source the current recording follows, and the last name noted
    public string RecordingSource { get; private set; } = "auto";
    public string MonitorSource { get; set; } = "auto"; // the Record view's choice before recording starts
    string lastNameKey = "";
    public NameSource? CurrentName { get; private set; }

    // The playing album's year (SPEC.md › Recording › Now playing): Windows media info has none, so it's looked up once per album
    readonly Dictionary<string, string> albumYears = new();
    readonly HashSet<string> yearsAsked = new();
    public string YearFor(NameSource? name) { if (name == null || name.Album == "") return ""; lock (albumYears) return albumYears.TryGetValue(name.Album, out var year) ? year : ""; } // by album: a featured artist on one track mustn't lose the year
    void LookUpYear(NameSource? name)
    {
        if (name == null || name.Album == "") return;
        var key = name.Album;
        lock (albumYears) if (!yearsAsked.Add(key)) return;
        _ = Task.Run(async () =>
        {
            string year = "";
            try { year = await TagLookup.AlbumYearAsync(name.AlbumArtist != "" ? name.AlbumArtist : name.Artist, name.Album); } catch { lock (albumYears) yearsAsked.Remove(key); return; }
            lock (albumYears) albumYears[key] = year;
            await Dispatcher.InvokeAsync(() =>
            {
                if (year != "" && Recorder.Session is { } session)
                {
                    var changed = false;
                    foreach (var change in session.Changes) if (change.Year == "" && change.Album == name.Album) { change.Year = year; changed = true; }
                    if (changed) session.Save();
                }
                StatusChanged?.Invoke();
            });
        });
    }

    protected override void OnStartup(StartupEventArgs startupArgs)
    {
        // One copy per data folder: a portable copy runs beside the installed one
        var instanceName = "WinTapeRecorder" + (AppPaths.IsPortable ? "." + Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(AppPaths.DataFolder.ToLowerInvariant())))[..12] : "");
        singleInstanceMutex = new Mutex(true, instanceName + ".SingleInstance", out var isFirstInstance);
        showWindowSignal = new EventWaitHandle(false, EventResetMode.AutoReset, instanceName + ".ShowWindow");
        if (!isFirstInstance) { showWindowSignal.Set(); Shutdown(); return; } // a second launch brings the running one forward
        ThreadPool.RegisterWaitForSingleObject(showWindowSignal, (state, timedOut) => Dispatcher.BeginInvoke(ShowMainWindow), null, -1, false);
        if (AppPaths.IsPortable) AppUpdater.CleanUpAfterPortableUpdate();
        CrashReporter.Install(this);
        Settings = AppSettings.Load();
        Watcher = new NowPlayingWatcher(() => Settings);
        Watcher.Updated += () => Dispatcher.BeginInvoke(OnNamesUpdated);
        Recorder.QualityMeasured += () => Dispatcher.BeginInvoke(() => StatusChanged?.Invoke());
        Recorder.DropoutFound += () => Dispatcher.BeginInvoke(() => { NoteProblem("dropout"); StatusChanged?.Invoke(); });
        Recorder.ClipFound += () => Dispatcher.BeginInvoke(() => { NoteProblem("clipping"); StatusChanged?.Invoke(); });
        Recorder.Stopped += () => Dispatcher.BeginInvoke(() =>
        {
            // A full WAV (4 GB, about 4 h at 24-bit) carries straight on into the next part, so long overnight sessions keep going (SPEC.md › Recording)
            if (Recorder.HitSizeLimit)
            {
                var keep = Recorder.KeepThroughSilence; var source = RecordingSource;
                var full = FinishRecording(); if (full == null) return;
                var (profileId, name) = (full.RecordingProfileId, full.Name);
                var part = System.Text.RegularExpressions.Regex.Match(name, @"^(.*) \(part (\d+)\)$");
                var next = part.Success ? $"{part.Groups[1].Value} (part {int.Parse(part.Groups[2].Value) + 1})" : $"{name} (part 2)";
                StartRecording(profileId, source, next);
                Recorder.KeepThroughSilence = keep;
                ShowTip("Recording continues", $"The recording reached the 256 GB guard, so it carries on in “{next}”.");
                return;
            }
            var problem = Recorder.Problem; FinishRecording(); if (problem != "") ShowTip("Recording stopped", problem);
        });
        Player.Finished += () => Dispatcher.BeginInvoke(() => StatusChanged?.Invoke());
        mainWindow = new MainWindow();
        mainWindow.RestoreBoundsForDisplays();
        var windowArgument = startupArgs.Args.FirstOrDefault(argument => argument.StartsWith("--window="));
        if (windowArgument != null) mainWindow.RestoreFromArgument(windowArgument["--window=".Length..]); else mainWindow.Show();
        Updater.Changed += () => Dispatcher.BeginInvoke(() => StatusChanged?.Invoke());
        Updater.Start();
        versionBeforeThisRun = Settings.General.LastRunVersion;
        if (Settings.General.LastRunVersion != Updater.CurrentVersion) { Settings.General.LastRunVersion = Updater.CurrentVersion; Settings.Save(); }
        // A recording cut off by a crash or power cut: say so; its audio up to the last two seconds is on disk and opens as usual
        _ = Task.Run(async () =>
        {
            try
            {
                var cut = SessionStore.List(Settings).Where(session => session.Recording).ToList();
                await Task.Delay(4000); // say so once the window is up
                if (cut.Count > 0) Dispatcher.BeginInvoke(() => ShowTip("A recording was cut off", $"“{cut[0].Name}” stopped with a crash or power cut. Everything up to the last couple of seconds is kept: open it from the list to check and export it."));
            }
            catch { }
        });
        if (CrashReporter.TakeUnseenReport()) ShowTip("WinTapeRecorder had a problem last time", "It crashed or stopped responding. Details are in Settings › About › Open logs (crash.log).");
    }

    // ---- Recording (SPEC.md › Recording) ----
    public void StartMonitoring(string profileId)
    {
        if (Recorder.Recording) return;
        Recorder.Stop();
        try { Recorder.Start(Settings.RecordingProfileOrActive(profileId), null); }
        catch (Exception monitorError) { ShowTip("Can't open the audio device", monitorError.Message); }
        StatusChanged?.Invoke();
    }

    public void StopMonitoring() { if (!Recorder.Recording) { Recorder.Stop(); StatusChanged?.Invoke(); } }

    public void StartRecording(string profileId, string source, string name, bool armed = false)
    {
        if (Recorder.Recording) return;
        Recorder.ArmNext = armed;
        Player.Stop();
        var profile = Settings.RecordingProfileOrActive(profileId);
        Recorder.Stop();
        Session? session = null;
        try
        {
            session = SessionStore.Create(profile, name);
            session.OutputProfileId = Settings.ActiveOutputProfile.Id;
            Recorder.Start(profile, session);
            RecordingSource = source; lastNameKey = "";
            NoteName(); // whatever is playing as recording starts names the first track
        }
        catch (Exception recordError)
        {
            Recorder.Stop();
            if (session != null) try { SessionStore.Delete(session); } catch { }
            ShowTip("Can't start recording", recordError.Message);
        }
        StatusChanged?.Invoke(); SessionsChanged?.Invoke();
    }

    // Auto-stop (SPEC.md › Recording): after the profile's minutes of silence the recording finishes by itself, the last track ending
    // a second after the sound did. Checked by the level timer
    public event Action<string, string>? RecordingStoppedItself;
    public double SilenceStopIn()
    {
        if (!Recorder.Recording || Recorder.KeepThroughSilence) return -1;
        var minutes = Settings.RecordingProfileOrActive(Recorder.Session?.RecordingProfileId ?? "").StopAfterSilenceMinutes;
        return minutes <= 0 ? -1 : Math.Max(0, minutes * 60 - Recorder.SilentSeconds);
    }
    DateTime lastCheckpoint = DateTime.UtcNow;
    int otherSoundsShown;
    bool wasWaiting;
    public void CheckSilence()
    {
        if (Recorder.Recording && Recorder.WaitingForSound != wasWaiting) { wasWaiting = Recorder.WaitingForSound; StatusChanged?.Invoke(); }
        if (Recorder.Recording) { var others = Recorder.OtherSoundsSoFar.Count; if (others != otherSoundsShown) { otherSoundsShown = others; StatusChanged?.Invoke(); } } else otherSoundsShown = 0;
        if (Recorder.Recording && DateTime.UtcNow - lastCheckpoint > TimeSpan.FromSeconds(30)) { lastCheckpoint = DateTime.UtcNow; try { Recorder.Checkpoint(); } catch { } }
        if (SilenceStopIn() != 0) return;
        var minutes = Settings.RecordingProfileOrActive(Recorder.Session?.RecordingProfileId ?? "").StopAfterSilenceMinutes;
        var soundEnd = Recorder.LastSoundFrame; var rate = Recorder.Session?.SampleRate ?? 48000;
        var session = FinishRecording();
        if (session == null) return;
        var limit = Math.Min(session.TotalFrames, soundEnd + rate);
        session.Tracks = session.Tracks.Where(track => track.Start < limit).ToList();
        foreach (var track in session.Tracks) if (track.End > limit) track.End = limit;
        session.Save();
        RecordingStoppedItself?.Invoke(session.Id, $"Recording stopped by itself after {minutes} minute{(minutes == 1 ? "" : "s")} of silence. The silence at the end isn't part of any track.");
    }

    public Session? StopRecording()
    {
        var session = FinishRecording();
        return session;
    }

    // Track numbers count up within each run of tracks sharing artist and album (SPEC.md › Recording): what the live list showed
    static void NumberTracks(Session session)
    {
        string key(Track track) => TagLookup.Loose(track.AlbumArtist != "" ? track.AlbumArtist : track.Artist) + "|" + TagLookup.Loose(track.Album);
        string previous = ""; int number = 0;
        foreach (var track in session.Tracks.Where(track => !track.Skip))
        {
            if (track.Album == "") { previous = ""; continue; }
            number = key(track) == previous ? number + 1 : 1; previous = key(track);
            track.TrackNumber = number;
        }
        // An album's year, typed or looked up on any of its tracks, goes on all of them
        foreach (var run in session.Tracks.Where(track => track.Album != "").GroupBy(key))
        {
            var year = run.Select(track => track.Year).FirstOrDefault(year => year != "") ?? "";
            if (year != "") foreach (var track in run) if (track.Year == "") track.Year = year;
        }
    }

    Session? FinishRecording()
    {
        var peaks = Recorder.Peaks;
        var stopped = Recorder.Stop(); var fromAudioSide = Recorder.TakeLastStopped();
        var session = stopped ?? fromAudioSide; // stopped from the audio side already: finish that one
        if (session != null && peaks != null)
        {
            try { session.Tracks = TrackBuilder.Build(session, peaks, Settings.RecordingProfileOrActive(session.RecordingProfileId)); NumberTracks(session); session.Save(); }
            catch (Exception buildError) { CrashReporter.Write("Track detection failed", buildError, false); }
        }
        StatusChanged?.Invoke(); SessionsChanged?.Invoke();
        return session;
    }

    // M while recording: a marker here, named by whatever is playing
    // Live editing while recording (SPEC.md › Recording): the markers and names noted so far can be moved, retyped or removed; capture carries on untouched
    // A dropout or clipping burst marks the track playing at the time as damaged (SPEC.md › Recording › Issues); a later take with the same name replaces it
    void NoteProblem(string kind)
    {
        if (Recorder.Session is not { } session) return;
        var at = Recorder.FramesWritten;
        var change = session.Changes.Where(candidate => candidate.Frame <= at).OrderByDescending(candidate => candidate.Frame).FirstOrDefault();
        if (change == null || change.Problem != "") return;
        change.Problem = kind; session.Save();
    }
    public void SetProblem(int index, string problem, string? note = null)
    {
        if (Recorder.Session is not { } session || index < 0 || index >= session.Changes.Count) return;
        session.Changes[index].Problem = problem; if (note != null) session.Changes[index].Note = note;
        session.Save(); StatusChanged?.Invoke();
    }

    // Several markers at once (SPEC.md › Recording › Live editing): an album's artist, name or year, or a track joining an album
    public void EditChanges(List<int> indexes, string? artist, string? album, string? year, string? genre = null, string? composer = null, bool? confirmed = null)
    {
        if (Recorder.Session is not { } session) return;
        foreach (var index in indexes.Where(index => index >= 0 && index < session.Changes.Count))
        {
            var change = session.Changes[index];
            if (artist != null) change.Artist = artist; if (album != null) change.Album = album; if (year != null) change.Year = year;
            if (genre != null) change.Genre = genre; if (composer != null) change.Composer = composer; if (confirmed != null) change.Confirmed = confirmed.Value;
        }
        session.Save(); StatusChanged?.Invoke();
    }

    public void EditChange(int index, long? frame, string? artist, string? title, string? album, string? year = null)
    {
        if (Recorder.Session is not { } session || index < 0 || index >= session.Changes.Count) return;
        var change = session.Changes[index];
        if (frame.HasValue) change.Frame = Math.Clamp(frame.Value, 0, Math.Max(0, Recorder.FramesWritten));
        if (artist != null) change.Artist = artist; if (title != null) change.Title = title; if (album != null) change.Album = album; if (year != null) change.Year = year;
        change.Manual = true; // edited by hand: the track builder keeps it even if it's short
        session.Changes.Sort((first, second) => first.Frame.CompareTo(second.Frame));
        session.Save(); StatusChanged?.Invoke();
    }
    public void RemoveChange(int index)
    {
        if (Recorder.Session is not { } session || index < 0 || index >= session.Changes.Count) return;
        session.Changes.RemoveAt(index); session.Save(); StatusChanged?.Invoke();
    }

    // The latest marker of the same album passes on its year, genre and composer (typed or looked up), even with other names in between
    static void CarryAlbumTags(Session session, NameChange change)
    {
        if (change.Album == "") return;
        var before = session.Changes.LastOrDefault(other => string.Equals(other.Album, change.Album, StringComparison.OrdinalIgnoreCase));
        if (before == null) return;
        if (change.Year == "") change.Year = before.Year;
        if (change.Genre == "") change.Genre = before.Genre;
        if (change.Composer == "") change.Composer = before.Composer;
    }

    public void AddMarker()
    {
        if (Recorder.Session is not { } session) return;
        session.Changes.Add(new NameChange { Frame = Recorder.FramesWritten, Artist = CurrentName?.Artist ?? "", Title = CurrentName?.Title ?? "", Album = CurrentName?.Album ?? "", AlbumArtist = CurrentName?.AlbumArtist ?? "", TrackNumber = CurrentName?.TrackNumber ?? 0, Year = YearFor(CurrentName), Source = "marker", Manual = true });
        CarryAlbumTags(session, session.Changes[^1]);
        session.Save(); StatusChanged?.Invoke();
    }

    public void RaiseNamesUpdated() => OnNamesUpdated();
    void OnNamesUpdated()
    {
        if (Recorder.Recording) NoteName();
        else
        {
            var previous = CurrentName; CurrentName = Watcher.Current(MonitorSource, Snapshot(Settings.ActiveRecordingProfile)); LookUpYear(CurrentName);
            if (previous?.Key != CurrentName?.Key || previous?.NameKey != CurrentName?.NameKey) StatusChanged?.Invoke(); // the Record view shows it straight away
        }
        mainWindow.PushSources();
    }

    static RecordingProfileSnapshot Snapshot(RecordingProfile profile) => new(profile.UseMediaInfo, profile.UseWindowTitles);

    // The player's picture for a name change, kept in the session folder (once per picture)
    async Task SavePlayerArtAsync(Session session, NameChange change, string sourceKey)
    {
        try
        {
            await Task.Delay(1500); // players send the picture a moment after the name
            var bytes = await Watcher.ThumbnailAsync(sourceKey);
            if (bytes == null || bytes.Length < 500) return;
            var path = TagLookup.SaveArtBytes(bytes, session.Folder);
            await Dispatcher.InvokeAsync(() => { change.ArtPath = path; if (Recorder.Session == session) session.Save(); });
        }
        catch { }
    }

    void NoteName()
    {
        if (Recorder.Session is not { } session) return;
        var profile = Settings.RecordingProfileOrActive(session.RecordingProfileId);
        CurrentName = Watcher.Current(RecordingSource, Snapshot(profile)); LookUpYear(CurrentName);
        if (CurrentName is not { HasName: true } name) return;
        if (name.NameKey == lastNameKey)
        {
            // Players send the album and such a moment after the title: fill what the marker still lacks, never what was typed
            if (session.Changes.LastOrDefault() is { } latest && string.Equals(latest.Title, name.Title, StringComparison.OrdinalIgnoreCase))
            {
                var filled = false;
                if (latest.Album == "" && name.Album != "") { latest.Album = name.Album; filled = true; }
                if (latest.AlbumArtist == "" && name.AlbumArtist != "") { latest.AlbumArtist = name.AlbumArtist; filled = true; }
                if (latest.TrackNumber == 0 && name.TrackNumber > 0) { latest.TrackNumber = name.TrackNumber; filled = true; }
                if (latest.Artist == "" && name.Artist != "") { latest.Artist = name.Artist; filled = true; }
                if (latest.ArtPath == "" && name.Picture) _ = SavePlayerArtAsync(session, latest, name.Key);
                if (filled) { session.Save(); StatusChanged?.Invoke(); }
            }
            return;
        }
        lastNameKey = name.NameKey;
        var frame = Math.Max(0, Recorder.FramesWritten - (long)profile.LatencyMs * session.SampleRate / 1000);
        // Players can flicker between tracks (the same title with a placeholder artist, then the real one): the same title again
        // within 30 s is the same track, so its marker is kept and only filled in, by the fuller of the two names
        if (session.Changes.LastOrDefault() is { Manual: false } last && frame - last.Frame < 30L * session.SampleRate && string.Equals(last.Title.Trim(), name.Title.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            int Filled(string artist, string album, string albumArtist, int number) => (artist != "" ? 1 : 0) + (album != "" ? 1 : 0) + (albumArtist != "" ? 1 : 0) + (number > 0 ? 1 : 0);
            if (Filled(name.Artist, name.Album, name.AlbumArtist, name.TrackNumber) > Filled(last.Artist, last.Album, last.AlbumArtist, last.TrackNumber)) { last.Artist = name.Artist; last.Album = name.Album; last.AlbumArtist = name.AlbumArtist; last.TrackNumber = name.TrackNumber; last.Source = name.Label; session.Save(); StatusChanged?.Invoke(); }
            else if ((last.Album == "" && name.Album != "") || (last.AlbumArtist == "" && name.AlbumArtist != "")) { if (last.Album == "") last.Album = name.Album; if (last.AlbumArtist == "") last.AlbumArtist = name.AlbumArtist; session.Save(); StatusChanged?.Invoke(); }
            if (last.ArtPath == "" && name.Picture) _ = SavePlayerArtAsync(session, last, name.Key);
            lastNameKey = name.NameKey;
            return;
        }
        var change = new NameChange { Frame = frame, Artist = name.Artist, Title = name.Title, Album = name.Album, AlbumArtist = name.AlbumArtist, TrackNumber = name.TrackNumber, Year = YearFor(name), Source = name.Label };
        // Players show an idle title for a moment between songs: a name that lasted under 5 s with no album was only that blip, so it goes
        if (session.Changes.Count > 1 && session.Changes[^1] is { Manual: false, Problem: "", Album: "" } blip && frame - blip.Frame < 5L * session.SampleRate) session.Changes.RemoveAt(session.Changes.Count - 1);
        CarryAlbumTags(session, change);
        session.Changes.Add(change);
        if (name.Picture) _ = SavePlayerArtAsync(session, change, name.Key);
        session.Save();
        StatusChanged?.Invoke();
    }

    // ---- Open sessions: the WAV reader and peaks stay open while the session is shown ----
    readonly Dictionary<string, (Session session, SessionAudio audio, Peaks peaks)> openSessions = new();
    public async Task<(Session session, SessionAudio audio, Peaks peaks)> OpenSessionAsync(string sessionId, Action<double>? progress = null)
    {
        if (Recorder.Session is { } recording && recording.Id == sessionId) throw new InvalidOperationException("That session is still recording.");
        if (openSessions.TryGetValue(sessionId, out var open)) return open;
        var session = Session.Load(sessionId) ?? throw new FileNotFoundException("The session's files are missing.");
        var opened = await Task.Run(() =>
        {
            if (session.Recording) { SessionAudio.RepairHeader(session.AudioPath); session.Recording = false; session.CutOff = true; } // left by a crash mid-recording
            var audio = SessionAudio.Open(session.AudioPath);
            if (session.TotalFrames != audio.TotalFrames || session.SampleRate == 0) { session.TotalFrames = audio.TotalFrames; session.SampleRate = audio.SampleRate; session.Channels = audio.Channels; session.BitDepth = audio.BitsPerSample; }
            var peaks = Peaks.Load(session.PeaksPath, audio.TotalFrames);
            if (peaks == null) { peaks = Peaks.Build(audio, progress); try { peaks.Save(session.PeaksPath); } catch { } }
            if (session.Tracks.Count == 0 && session.TotalFrames > 0) session.Tracks = TrackBuilder.Build(session, peaks, Settings.RecordingProfileOrActive(session.RecordingProfileId));
            foreach (var track in session.Tracks) { if (track.DetectedStart < 0) track.DetectedStart = track.Start; if (track.DetectedEnd < 0) track.DetectedEnd = track.End; } // older sessions: as they are now
            if (session.Quality.Count == 0 && session.TotalFrames > audio.SampleRate * 2) session.Quality = MeasureQuality(audio); // recorded before this was measured live
            session.Save();
            return (session, audio, peaks);
        });
        foreach (var stale in openSessions.Keys.Where(key => key != sessionId).ToList()) CloseOpen(stale); // one at a time keeps memory down
        openSessions[sessionId] = opened;
        return opened;
    }

    // A cutoff reading every two seconds through the file (a 2 s window at each)
    static List<QualitySample> MeasureQuality(SessionAudio audio)
    {
        var samples = new List<QualitySample>(); var buffer = new float[SpectralCutoff.WindowFrames * audio.Channels];
        for (long frame = audio.SampleRate * 2; frame + SpectralCutoff.WindowFrames <= audio.TotalFrames; frame += audio.SampleRate * 2)
        {
            if (audio.Read(frame - SpectralCutoff.WindowFrames, SpectralCutoff.WindowFrames, buffer) < SpectralCutoff.WindowFrames) break;
            samples.Add(new QualitySample { Frame = frame, Hz = SpectralCutoff.Measure(buffer, audio.Channels, audio.SampleRate) ?? 0 });
        }
        return samples;
    }

    // ---- Suggestions (SPEC.md › Tags): looked up in the background once a session is open, one track at a time, MusicBrainz's one-a-second limit respected ----
    readonly Dictionary<string, CancellationTokenSource> suggesting = new();
    public event Action<string, Track>? SuggestionFound;
    public event Action<string, AlbumCheck?>? AlbumChecked;
    public event Action<string, Session>? AlbumMatched;
    public event Action<string, int, int>? AlbumProgress;
    public void SuggestTags(string sessionId, bool again = false)
    {
        if (!openSessions.TryGetValue(sessionId, out var open)) return;
        if (suggesting.TryGetValue(sessionId, out var running)) { if (!again) return; running.Cancel(); }
        var session = open.session;
        if (session.AlbumLooked && !again) return;
        var cancel = new CancellationTokenSource(); suggesting[sessionId] = cancel;
        _ = Task.Run(async () =>
        {
            try
            {
                // The session's albums (SPEC.md › Tags › Album match); tracks get only their title and number from their album
                var named = session.Tracks.Where(track => track.Title != "" && !track.Skip).ToList();
                var albums = await TagLookup.MatchAlbumsAsync(named.Select(track => (track.Artist, track.Title)).ToList(), (done, total) => AlbumProgress?.Invoke(sessionId, done, total), cancel.Token);
                if (cancel.IsCancellationRequested) return;
                await Dispatcher.InvokeAsync(() =>
                {
                    session.AlbumCheck = null; session.AlbumLooked = true; session.Albums = new();
                    foreach (var track in session.Tracks) { track.SuggestionLooked = true; track.SuggestionDismissed = false; track.Suggestion = null; track.OffAlbum = false; }
                    foreach (var (match, found) in albums)
                    {
                        foreach (var (index, entry) in found)
                        {
                            var track = named[index]; match.TrackIds.Add(track.Id);
                            // The release's spelling, without asides like "(take 2)" or "(Remastered)" unless the track's own title has them
                            var offered = track.Title.IndexOfAny(new[] { '(', '[' }) >= 0 ? entry.title : System.Text.RegularExpressions.Regex.Replace(entry.title, @"\s*[\(\[][^\)\]]*[\)\]]", "").Trim();
                            if (offered == "") offered = entry.title;
                            if (entry.number > 0) track.Suggestion = new TagSuggestion { Source = match.Source, Title = offered, TrackNumber = entry.number, TrackCount = entry.count, ReleaseId = match.ReleaseId };
                            else track.OffAlbum = match.TrackTitles.Count > 0;
                        }
                        session.Albums.Add(match);
                    }
                    session.Save(); AlbumMatched?.Invoke(sessionId, session);
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { CrashReporter.Write("Tag suggestions", error, false); }
            finally { if (suggesting.TryGetValue(sessionId, out var mine) && mine == cancel) suggesting.Remove(sessionId); }
        });
    }

    // The album most tracks point at, against its track list: how many are here, which are missing
    async Task CheckAlbumAsync(Session session, string sessionId, CancellationToken cancel)
    {
        var releases = session.Tracks.Where(track => !track.Skip && track.Suggestion is { ReleaseId.Length: > 0 }).GroupBy(track => track.Suggestion!.ReleaseId).OrderByDescending(group => group.Count()).FirstOrDefault();
        AlbumCheck? check = null;
        if (releases != null)
        {
            var sample = releases.First().Suggestion!;
            var titles = await TagLookup.ReleaseTracksAsync(releases.Key);
            if (cancel.IsCancellationRequested) return;
            var have = session.Tracks.Where(track => !track.Skip).Select(track => TagLookup.Loose(track.Title)).ToHashSet();
            var missing = titles.Where(title => !have.Contains(TagLookup.Loose(title))).ToList();
            check = new AlbumCheck { Source = "MusicBrainz", Album = sample.Album, Artist = sample.AlbumArtist != "" ? sample.AlbumArtist : sample.Artist, Total = titles.Count, Have = titles.Count - missing.Count, Missing = missing };
        }
        await Dispatcher.InvokeAsync(() => { session.AlbumCheck = check; session.Save(); AlbumChecked?.Invoke(sessionId, check); });
    }

    public bool TryGetOpen(string sessionId, out (Session session, SessionAudio audio, Peaks peaks) open) => openSessions.TryGetValue(sessionId, out open);

    void CloseOpen(string sessionId)
    {
        if (suggesting.Remove(sessionId, out var running)) running.Cancel();
        if (!openSessions.Remove(sessionId, out var open)) return;
        if (Player.SessionId == sessionId) Player.Stop();
        open.audio.Dispose();
    }

    public void DeleteSession(string sessionId)
    {
        CloseOpen(sessionId);
        var session = Session.Load(sessionId) ?? throw new FileNotFoundException("The session is already gone.");
        SessionStore.Delete(session);
        SessionsChanged?.Invoke();
    }

    public void NotifySessionsChanged() => SessionsChanged?.Invoke();
    public void NotifyHintsChanged() => HintsChanged?.Invoke();
    public void NotifySettingsChanged() => SettingsChanged?.Invoke();
    public void RaiseStatusChanged() => StatusChanged?.Invoke();

    public string StatusJson() => JsonSerializer.Serialize(new
    {
        type = "status",
        recording = Recorder.Recording,
        capturing = Recorder.Capturing,
        sessionId = Recorder.Session?.Id ?? "",
        sessionName = Recorder.Session?.Name ?? "",
        device = Recorder.DeviceName,
        deviceVolume = Recorder.DeviceVolume,
        dropouts = Recorder.Dropouts.Count,
        cutoffHz = Recorder.CurrentCutoffHz,
        flacRatio = Recorder.FlacRatio,
        bitDepth = Recorder.Session?.BitDepth ?? 0,
        channels = Recorder.Session?.Channels ?? 0,
        cutoffText = SpectralCutoff.Describe(Recorder.CurrentCutoffHz),
        sampleRate = Recorder.Session?.SampleRate ?? 0,
        changes = Recorder.Session?.Changes.Select(change => new { change.Frame, change.Artist, change.Title, change.Album, change.Year, change.Genre, change.Composer, change.Confirmed, change.Manual, change.Problem, change.Note }) ?? Enumerable.Empty<object>(),
        dropoutList = Recorder.Dropouts.Select(dropout => new { dropout.Frame, dropout.Ms }),
        otherSounds = Recorder.OtherSoundsSoFar,
        clips = Recorder.Clips.Select(clip => new { clip.Frame, clip.Samples }),
        current = CurrentName == null ? null : new { CurrentName.Key, CurrentName.Label, CurrentName.Artist, CurrentName.Title, CurrentName.Album, year = YearFor(CurrentName) },
        source = RecordingSource,
        playing = Player.Playing,
        playingSession = Player.SessionId,
        exporting = Exporting,
        waitingForSound = Recorder.Recording && Recorder.WaitingForSound,
        paused = Recorder.Recording && Recorder.Paused,
        exportingSession = ExportingSessionId, exportDone = ExportDone, exportTotal = ExportTotal, exportQueue = ExportQueue,
        version = Updater.CurrentVersion,
        updateReady = Updater.ReadyVersion,
    }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

    // ---- Updates and versions ----
    string versionBeforeThisRun = "";
    bool whatsNewChecked;
    public async Task ShowWhatsNewAsync()
    {
        if (whatsNewChecked) return;
        whatsNewChecked = true;
        string previousVersion = versionBeforeThisRun, currentVersion = Updater.CurrentVersion;
        if (previousVersion == "" || !AppUpdater.IsNewer(currentVersion, previousVersion)) return;
        try
        {
            var releases = (await Updater.ListReleasesAsync(true)).Where(release => AppUpdater.IsNewer(release.Version, previousVersion) && !AppUpdater.IsNewer(release.Version, currentVersion)).OrderByDescending(release => Version.Parse(release.Version)).ToList();
            if (releases.Count > 0) mainWindow.PushWhatsNew(previousVersion, currentVersion, releases);
        }
        catch (Exception error) { CrashReporter.Write("What's new: release notes unavailable", error, false); }
    }

    // An update restart never cuts a recording or export short
    public bool CanRestart()
    {
        if (Recorder.Recording) { MessageBox.Show("Stop the recording first, then restart to update.", "WinTapeRecorder", MessageBoxButton.OK, MessageBoxImage.Information); return false; }
        if (Exporting) { MessageBox.Show("Wait for the export to finish, then restart to update.", "WinTapeRecorder", MessageBoxButton.OK, MessageBoxImage.Information); return false; }
        return true;
    }

    public Task<string[]> ResumeArgumentsAsync() => Task.FromResult(new[] { "--window=" + mainWindow.DescribeForRestart() });

    public void PrepareForRestart()
    {
        mainWindow.SaveBounds(); Settings.Save();
        IsQuitting = true;
        Player.Dispose(); Recorder.Dispose();
        singleInstanceMutex?.ReleaseMutex(); singleInstanceMutex?.Dispose(); singleInstanceMutex = null;
    }

    public async Task ReplaceSettingsAsync(AppSettings importedSettings)
    {
        Settings = importedSettings; Settings.Save();
        ApplyWindowStyle(); ApplySkin(); SettingsChanged?.Invoke(); SessionsChanged?.Invoke();
        await Task.CompletedTask;
    }

    public void ApplyWindowStyle() => mainWindow.ApplyWindowStyle();
    public void ApplySkin() => mainWindow.PushSkin();
    public void ApplyUiScale() => mainWindow.ApplyUiScale();
    public void RefreshInactiveGlass() => mainWindow.RefreshInactiveGlass();
    public void ShowMainWindow() { if (IsQuitting) return; mainWindow.Show(); if (mainWindow.WindowState == WindowState.Minimized) mainWindow.WindowState = WindowState.Normal; mainWindow.Activate(); }
    // No tray: tips show as a message in the window
    public void ShowTip(string title, string text) => Tip?.Invoke(title, text);

    public void OpenSettings(string tabName = "")
    {
        if (settingsWindow == null) { settingsWindow = new SettingsWindow(); settingsWindow.Closed += (sender, args) => settingsWindow = null; }
        if (mainWindow.IsVisible && settingsWindow.Owner == null) settingsWindow.Owner = mainWindow;
        settingsWindow.SelectTab(tabName);
        settingsWindow.Show(); settingsWindow.Activate();
    }

    // Close (X) quits, after a warning when a recording or export would be cut short (SPEC.md › Window and look)
    public bool ConfirmQuit()
    {
        if (Recorder.Recording && MessageBox.Show("A recording is in progress. Stop it and quit? What's recorded so far is kept.", "WinTapeRecorder", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return false;
        if (Exporting && MessageBox.Show("An export is in progress. Quit anyway? The track being written is left unfinished.", "WinTapeRecorder", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return false;
        return true;
    }

    public void Quit(bool windowClosing = false)
    {
        if (IsQuitting) return;
        IsQuitting = true;
        try
        {
            mainWindow.SaveBounds(); Settings.Save();
            if (Recorder.Recording) FinishRecording(); else Recorder.Stop();
            Player.Dispose();
            foreach (var sessionId in openSessions.Keys.ToList()) CloseOpen(sessionId);
            settingsWindow?.Close();
            if (!windowClosing) mainWindow.Close();
        }
        catch (Exception quitError) { CrashReporter.Write("While quitting", quitError, false); }
        finally { Shutdown(); } // whatever happened above, the process ends
    }
}
