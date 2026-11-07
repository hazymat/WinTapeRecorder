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
        Recorder.Stopped += () => Dispatcher.BeginInvoke(() => { var problem = Recorder.Problem; FinishRecording(); if (problem != "") ShowTip("Recording stopped", problem); });
        Player.Finished += () => Dispatcher.BeginInvoke(() => StatusChanged?.Invoke());
        mainWindow = new MainWindow();
        mainWindow.RestoreBoundsForDisplays();
        var windowArgument = startupArgs.Args.FirstOrDefault(argument => argument.StartsWith("--window="));
        if (windowArgument != null) mainWindow.RestoreFromArgument(windowArgument["--window=".Length..]); else mainWindow.Show();
        Updater.Changed += () => Dispatcher.BeginInvoke(() => StatusChanged?.Invoke());
        Updater.Start();
        versionBeforeThisRun = Settings.General.LastRunVersion;
        if (Settings.General.LastRunVersion != Updater.CurrentVersion) { Settings.General.LastRunVersion = Updater.CurrentVersion; Settings.Save(); }
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

    public void StartRecording(string profileId, string source, string name)
    {
        if (Recorder.Recording) return;
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
    public void CheckSilence()
    {
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

    Session? FinishRecording()
    {
        var peaks = Recorder.Peaks;
        var session = Recorder.Stop();
        if (session != null && peaks != null)
        {
            try { session.Tracks = TrackBuilder.Build(session, peaks, Settings.RecordingProfileOrActive(session.RecordingProfileId)); session.Save(); }
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
    public void SetProblem(int index, string problem)
    {
        if (Recorder.Session is not { } session || index < 0 || index >= session.Changes.Count) return;
        session.Changes[index].Problem = problem; session.Save(); StatusChanged?.Invoke();
    }

    public void EditChange(int index, long? frame, string? artist, string? title, string? album)
    {
        if (Recorder.Session is not { } session || index < 0 || index >= session.Changes.Count) return;
        var change = session.Changes[index];
        if (frame.HasValue) change.Frame = Math.Clamp(frame.Value, 0, Math.Max(0, Recorder.FramesWritten));
        if (artist != null) change.Artist = artist; if (title != null) change.Title = title; if (album != null) change.Album = album;
        change.Manual = true; // edited by hand: the track builder keeps it even if it's short
        session.Changes.Sort((first, second) => first.Frame.CompareTo(second.Frame));
        session.Save(); StatusChanged?.Invoke();
    }
    public void RemoveChange(int index)
    {
        if (Recorder.Session is not { } session || index < 0 || index >= session.Changes.Count) return;
        session.Changes.RemoveAt(index); session.Save(); StatusChanged?.Invoke();
    }

    public void AddMarker()
    {
        if (Recorder.Session is not { } session) return;
        session.Changes.Add(new NameChange { Frame = Recorder.FramesWritten, Artist = CurrentName?.Artist ?? "", Title = CurrentName?.Title ?? "", Album = CurrentName?.Album ?? "", AlbumArtist = CurrentName?.AlbumArtist ?? "", TrackNumber = CurrentName?.TrackNumber ?? 0, Source = "marker", Manual = true });
        session.Save(); StatusChanged?.Invoke();
    }

    public void RaiseNamesUpdated() => OnNamesUpdated();
    void OnNamesUpdated()
    {
        if (Recorder.Recording) NoteName();
        else
        {
            var previous = CurrentName; CurrentName = Watcher.Current(MonitorSource, Snapshot(Settings.ActiveRecordingProfile));
            if (previous?.Key != CurrentName?.Key || previous?.NameKey != CurrentName?.NameKey) StatusChanged?.Invoke(); // the Record view shows it straight away
        }
        mainWindow.PushSources();
    }

    static RecordingProfileSnapshot Snapshot(RecordingProfile profile) => new(profile.UseMediaInfo, profile.UseWindowTitles);

    void NoteName()
    {
        if (Recorder.Session is not { } session) return;
        var profile = Settings.RecordingProfileOrActive(session.RecordingProfileId);
        CurrentName = Watcher.Current(RecordingSource, Snapshot(profile));
        if (CurrentName is not { HasName: true } name || name.NameKey == lastNameKey) return;
        lastNameKey = name.NameKey;
        var frame = Math.Max(0, Recorder.FramesWritten - (long)profile.LatencyMs * session.SampleRate / 1000);
        session.Changes.Add(new NameChange { Frame = frame, Artist = name.Artist, Title = name.Title, Album = name.Album, AlbumArtist = name.AlbumArtist, TrackNumber = name.TrackNumber, Source = name.Label });
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
            if (session.Recording) { SessionAudio.RepairHeader(session.AudioPath); session.Recording = false; } // left by a crash mid-recording
            var audio = SessionAudio.Open(session.AudioPath);
            if (session.TotalFrames != audio.TotalFrames || session.SampleRate == 0) { session.TotalFrames = audio.TotalFrames; session.SampleRate = audio.SampleRate; session.Channels = audio.Channels; session.BitDepth = audio.BitsPerSample; }
            var peaks = Peaks.Load(session.PeaksPath, audio.TotalFrames);
            if (peaks == null) { peaks = Peaks.Build(audio, progress); try { peaks.Save(session.PeaksPath); } catch { } }
            if (session.Tracks.Count == 0 && session.TotalFrames > 0) session.Tracks = TrackBuilder.Build(session, peaks, Settings.RecordingProfileOrActive(session.RecordingProfileId));
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
                            if (entry.number > 0) track.Suggestion = new TagSuggestion { Source = match.Source, Title = entry.title, TrackNumber = entry.number, TrackCount = entry.count, ReleaseId = match.ReleaseId };
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
        changes = Recorder.Session?.Changes.Select(change => new { change.Frame, change.Artist, change.Title, change.Album, change.Manual, change.Problem }) ?? Enumerable.Empty<object>(),
        dropoutList = Recorder.Dropouts.Select(dropout => new { dropout.Frame, dropout.Ms }),
        clips = Recorder.Clips.Select(clip => new { clip.Frame, clip.Samples }),
        current = CurrentName == null ? null : new { CurrentName.Key, CurrentName.Label, CurrentName.Artist, CurrentName.Title, CurrentName.Album },
        source = RecordingSource,
        playing = Player.Playing,
        playingSession = Player.SessionId,
        exporting = Exporting,
        exportingSession = ExportingSessionId, exportDone = ExportDone, exportTotal = ExportTotal,
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
