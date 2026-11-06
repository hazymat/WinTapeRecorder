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
    string lastNameKey = "";
    public NameSource? CurrentName { get; private set; }

    protected override void OnStartup(StartupEventArgs startupArgs)
    {
        singleInstanceMutex = new Mutex(true, "WinTapeRecorder.SingleInstance", out var isFirstInstance);
        showWindowSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "WinTapeRecorder.ShowWindow");
        if (!isFirstInstance) { showWindowSignal.Set(); Shutdown(); return; } // a second launch brings the running one forward
        ThreadPool.RegisterWaitForSingleObject(showWindowSignal, (state, timedOut) => Dispatcher.BeginInvoke(ShowMainWindow), null, -1, false);
        if (AppPaths.IsPortable) AppUpdater.CleanUpAfterPortableUpdate();
        CrashReporter.Install(this);
        Settings = AppSettings.Load();
        Watcher = new NowPlayingWatcher(() => Settings);
        Watcher.Updated += () => Dispatcher.BeginInvoke(OnNamesUpdated);
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
    public void AddMarker()
    {
        if (Recorder.Session is not { } session) return;
        session.Changes.Add(new NameChange { Frame = Recorder.FramesWritten, Artist = CurrentName?.Artist ?? "", Title = CurrentName?.Title ?? "", Album = CurrentName?.Album ?? "", Source = "marker", Manual = true });
        session.Save(); StatusChanged?.Invoke();
    }

    void OnNamesUpdated()
    {
        if (Recorder.Recording) NoteName();
        else CurrentName = Watcher.Current("auto", Snapshot(Settings.ActiveRecordingProfile));
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
        session.Changes.Add(new NameChange { Frame = frame, Artist = name.Artist, Title = name.Title, Album = name.Album, Source = name.Label });
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
            session.Save();
            return (session, audio, peaks);
        });
        foreach (var stale in openSessions.Keys.Where(key => key != sessionId).ToList()) CloseOpen(stale); // one at a time keeps memory down
        openSessions[sessionId] = opened;
        return opened;
    }

    public bool TryGetOpen(string sessionId, out (Session session, SessionAudio audio, Peaks peaks) open) => openSessions.TryGetValue(sessionId, out open);

    void CloseOpen(string sessionId)
    {
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
        sampleRate = Recorder.Session?.SampleRate ?? 0,
        changes = Recorder.Session?.Changes.Select(change => new { change.Frame, change.Artist, change.Title, change.Manual }) ?? Enumerable.Empty<object>(),
        current = CurrentName == null ? null : new { CurrentName.Label, CurrentName.Artist, CurrentName.Title },
        source = RecordingSource,
        playing = Player.Playing,
        playingSession = Player.SessionId,
        exporting = Exporting,
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
    public void ShowMainWindow() { mainWindow.Show(); if (mainWindow.WindowState == WindowState.Minimized) mainWindow.WindowState = WindowState.Normal; mainWindow.Activate(); }
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

    public void Quit()
    {
        if (IsQuitting) return;
        IsQuitting = true;
        mainWindow.SaveBounds(); Settings.Save();
        if (Recorder.Recording) FinishRecording(); else Recorder.Stop();
        Player.Dispose();
        foreach (var sessionId in openSessions.Keys.ToList()) CloseOpen(sessionId);
        settingsWindow?.Close();
        mainWindow.Close();
        Shutdown();
    }
}
