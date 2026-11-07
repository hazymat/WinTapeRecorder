using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using WinTapeRecorder.Audio;
using WinTapeRecorder.Detection;
using WinTapeRecorder.Sessions;
using WinTapeRecorder.Settings;
using WinTapeRecorder.Shell;
namespace WinTapeRecorder;

public partial class MainWindow : Window
{
    const string uiHost = "app.wintaperecorder"; // virtual host mapped to wwwroot
    bool webViewReady;
    App App => App.Instance;
    static readonly JsonSerializerOptions camel = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    readonly DispatcherTimer levelTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    readonly DispatcherTimer playheadTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    CancellationTokenSource? exportCancel;
    readonly List<JsonElement> exportQueue = new();

    // One export: its own copy of the session and its own reader of the WAV, so opening other sessions meanwhile can't stop it.
    // Each track's result is pushed as it's written; at the end the results go into the session as it is now (open or on disk)
    async Task RunExportAsync(JsonElement message)
    {
        var exportSessionId = Text(message, "id");
        var session = Session.Load(exportSessionId);
        if (session == null) { Reply(message, new { done = 0, verified = 0, error = "The session's files are missing." }); StartNextExport(); return; }
        var wanted = message.GetProperty("trackIds").EnumerateArray().Select(element => element.GetString() ?? "").ToHashSet();
        var exportTracks = session.Tracks.Where(track => wanted.Contains(track.Id)).ToList();
        var outputProfile = App.Settings.OutputProfileOrActive(Text(message, "profileId"));
        App.Exporting = true; App.ExportingSessionId = exportSessionId; App.ExportDone = 0; App.ExportTotal = exportTracks.Count; Push(App.StatusJson());
        exportCancel = new CancellationTokenSource();
        var errors = "";
        // Albums can have their own output profile: each profile's tracks are exported in turn
        var byProfile = exportTracks.GroupBy(track => track.OutputProfileId != "" && App.Settings.OutputProfiles.Any(profile => profile.Id == track.OutputProfileId) ? track.OutputProfileId : outputProfile.Id).ToList();
        int exportedCount = 0;
        try
        {
            exportedCount = await Task.Run(() =>
            {
                var thread = Thread.CurrentThread; var before = thread.Priority; thread.Priority = ThreadPriority.BelowNormal;
                using var audio = SessionAudio.Open(session.AudioPath);
                try
                {
                    int doneBefore = 0, total = exportTracks.Count, written = 0;
                    foreach (var batch in byProfile)
                    {
                        var profile = App.Settings.OutputProfiles.First(candidate => candidate.Id == batch.Key);
                        var offset = doneBefore;
                        written += Exporter.Export(session, audio, batch.ToList(), profile, progress =>
                        {
                            if (progress.Error != "") errors = progress.Error; App.ExportDone = offset + progress.Done;
                            Push(JsonSerializer.Serialize(new { type = "exportProgress", id = exportSessionId, done = offset + progress.Done, total, current = progress.Current }));
                            Push(JsonSerializer.Serialize(new { type = "exportTracks", id = exportSessionId, tracks = exportTracks.Where(track => track.Exported || track.VerifyError != "").Select(track => new { track.Id, track.Exported, track.ExportedPath, track.Verified, track.VerifyError }) }, camel));
                        }, exportCancel.Token);
                        doneBefore += batch.Count();
                        if (exportCancel.IsCancellationRequested) break;
                    }
                    return written;
                }
                finally { thread.Priority = before; }
            });
        }
        catch (Exception exportError) { errors = exportError.Message; }
        // The results go into the session as it is now: the open copy if it's open (the user may have edited it meanwhile), else the one on disk
        var target = App.TryGetOpen(exportSessionId, out var openNow) ? openNow.session : Session.Load(exportSessionId) ?? session;
        foreach (var track in exportTracks)
            if (target.Tracks.FirstOrDefault(candidate => candidate.Id == track.Id) is { } into) { into.Exported = track.Exported; into.ExportedPath = track.ExportedPath; into.Verified = track.Verified; into.VerifyError = track.VerifyError; }
        target.OutputProfileId = outputProfile.Id; target.Save();
        App.Exporting = false; App.ExportingSessionId = ""; Push(App.StatusJson());
        var verifiedCount = exportTracks.Count(track => track.Verified);
        Reply(message, new { done = exportedCount, verified = verifiedCount, error = errors, tracks = target.Tracks, folder = outputProfile.Folder.Trim() == "" ? AppPaths.DefaultOutputFolder : outputProfile.Folder.Trim() });
        Push(JsonSerializer.Serialize(new { type = "exportDone", id = exportSessionId, done = exportedCount, verified = verifiedCount, total = exportTracks.Count, error = errors }));
        App.NotifySessionsChanged();
        StartNextExport();
    }

    void StartNextExport()
    {
        if (exportQueue.Count == 0) return;
        var next = exportQueue[0]; exportQueue.RemoveAt(0); App.ExportQueue.Remove(Text(next, "id"));
        _ = Dispatcher.InvokeAsync(() => RunExportAsync(next));
    }

    public MainWindow()
    {
        InitializeComponent();
        // Smallest size where the layout still works (SPEC.md › Window and look), but never bigger than the screen allows
        MinWidth = Math.Min(1100, SystemParameters.WorkArea.Width); MinHeight = Math.Min(680, SystemParameters.WorkArea.Height);
        Loaded += async (sender, args) => await InitWebViewAsync();
        SourceInitialized += (sender, args) => ApplyWindowStyle();
        Activated += (sender, args) => OnFirstActivation();
        StateChanged += (sender, args) => { UpdateFrameMargin(); PushWindowState(); };
        App.StatusChanged += () => { Push(App.StatusJson()); levelTimer.IsEnabled = App.Recorder.Capturing; playheadTimer.IsEnabled = App.Player.Playing; if (!App.Player.Playing) Push("{\"type\":\"playhead\",\"frame\":-1}"); };
        App.SessionsChanged += () => Push("{\"type\":\"sessionsChanged\"}");
        App.HintsChanged += PushSettings;
        App.SettingsChanged += PushSettings;
        App.Tip += (title, text) => Push(JsonSerializer.Serialize(new { type = "tip", title, text }));
        App.SuggestionFound += (sessionId, track) => Push(JsonSerializer.Serialize(new { type = "suggestion", id = sessionId, trackId = track.Id, suggestion = track.Suggestion, looked = track.SuggestionLooked }, camel));
        App.AlbumChecked += (sessionId, check) => Push(JsonSerializer.Serialize(new { type = "albumCheck", id = sessionId, check }, camel));
        App.RecordingStoppedItself += (sessionId, reason) => Push(JsonSerializer.Serialize(new { type = "recordingStopped", sessionId, reason }));
        App.AlbumProgress += (sessionId, done, total) => Push(JsonSerializer.Serialize(new { type = "albumProgress", id = sessionId, done, total }, camel));
        App.AlbumMatched += (sessionId, matched) => Push(JsonSerializer.Serialize(new { type = "albumMatch", id = sessionId, albums = matched.Albums, tracks = matched.Tracks.Select(track => new { trackId = track.Id, suggestion = track.Suggestion, offAlbum = track.OffAlbum }) }, camel));
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (sender, args) => Dispatcher.BeginInvoke(RestoreBoundsForDisplays);
        levelTimer.Tick += (sender, args) => PushLevels();
        playheadTimer.Tick += (sender, args) => { if (!App.Player.Playing) { playheadTimer.Stop(); Push(App.StatusJson()); Push("{\"type\":\"playhead\",\"frame\":-1}"); return; } Push(JsonSerializer.Serialize(new { type = "playhead", frame = App.Player.Position, session = App.Player.SessionId })); };
        // WebView2 forwards accelerator keys to WPF as KeyDown
        PreviewKeyDown += (sender, keyArgs) =>
        {
            if (Keyboard.Modifiers != ModifierKeys.Control) return;
            switch (keyArgs.Key)
            {
                case Key.Q: keyArgs.Handled = true; Close(); break;
                case Key.OemComma: keyArgs.Handled = true; App.OpenSettings(); break;
            }
        };
    }

    async Task InitWebViewAsync()
    {
        var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(AppPaths.DataFolder, "WebView2"));
        await webView.EnsureCoreWebView2Async(environment);
        webView.DefaultBackgroundColor = glassActive || App.Settings.General.Frameless ? System.Drawing.Color.Transparent : System.Drawing.Color.FromArgb(16, 17, 20);
        webView.CoreWebView2.SetVirtualHostNameToFolderMapping(uiHost, Path.Combine(AppContext.BaseDirectory, "wwwroot"), CoreWebView2HostResourceAccessKind.DenyCors);
        webView.CoreWebView2.WebMessageReceived += async (sender, args) => await OnUiMessageAsync(args.WebMessageAsJson);
        // Right-click in a text field gives Cut, Copy, Paste and Select all; elsewhere the app's own menus (or none)
        webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
        webView.CoreWebView2.ContextMenuRequested += (sender, args) =>
        {
            if (!args.ContextMenuTarget.IsEditable) { args.Handled = true; return; }
            var keep = new HashSet<string> { "undo", "redo", "cut", "copy", "paste", "pasteAndMatchStyle", "selectAll" };
            for (var index = args.MenuItems.Count - 1; index >= 0; index--) if (!keep.Contains(args.MenuItems[index].Name) || args.MenuItems[index].Name == "pasteAndMatchStyle") args.MenuItems.RemoveAt(index);
            // no separators left at the ends or doubled
            while (args.MenuItems.Count > 0 && args.MenuItems[0].Kind == Microsoft.Web.WebView2.Core.CoreWebView2ContextMenuItemKind.Separator) args.MenuItems.RemoveAt(0);
        };
        webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
        webView.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;
        webView.CoreWebView2.Settings.IsZoomControlEnabled = false; // Ctrl+wheel zooms the waveform, not the page
        ApplyUiScale();
        webView.CoreWebView2.NavigationCompleted += (sender, args) => webViewReady = true;
        webView.CoreWebView2.ProcessFailed += (sender, args) => { CrashReporter.Write($"WebView2 process failed: {args.ProcessFailedKind} ({args.Reason})", null); if (args.ProcessFailedKind is CoreWebView2ProcessFailedKind.RenderProcessExited or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive) webView.CoreWebView2.Reload(); };
        webView.CoreWebView2.Navigate($"https://{uiHost}/index.html");
    }

    void Push(string messageJson) { if (webViewReady) Dispatcher.BeginInvoke(() => webView.CoreWebView2?.PostWebMessageAsJson(messageJson)); }
    void Reply(JsonElement message, object? data) { if (message.TryGetProperty("requestId", out var requestId)) Push($"{{\"type\":\"result\",\"id\":{requestId.GetInt64()},\"data\":{JsonSerializer.Serialize(data, camel)}}}"); }
    static string Text(JsonElement message, string name) => message.TryGetProperty(name, out var value) ? value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.GetRawText() : "";
    static double Number(JsonElement message, string name, double fallback = 0) => message.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : fallback;

    // Bridge: the UI posts {type, ...}; requests carrying an id get {type:"result", id, data}
    async Task OnUiMessageAsync(string messageJson)
    {
        using var messageDocument = JsonDocument.Parse(messageJson);
        var message = messageDocument.RootElement.Clone();
        webViewReady = true;
        CrashReporter.Note("UI message " + Text(message, "type"));
        try
        {
            switch (Text(message, "type"))
            {
                case "ready": LookLog("ready"); _ = ReapplyLookSoonAsync(); PushSkin(); PushWindowState(); Push(App.StatusJson()); PushSettings(); PushSources(); _ = App.ShowWhatsNewAsync(); break;
                case "dismissHint": if (!App.Settings.General.DismissedHints.Contains(Text(message, "id"))) { App.Settings.General.DismissedHints.Add(Text(message, "id")); App.Settings.Save(); } break;
                case "openSettings": App.OpenSettings(Text(message, "tab")); break;
                case "quit": Close(); break;
                case "applyUpdate": await App.Updater.RestartToUpdateAsync(); break;
                // ---- Recording ----
                case "monitor": if (message.GetProperty("on").GetBoolean()) App.StartMonitoring(Text(message, "profileId")); else App.StopMonitoring(); break;
                case "record": App.StartRecording(Text(message, "profileId"), Text(message, "source") is { Length: > 0 } source ? source : "auto", Text(message, "name")); break;
                case "keepThroughSilence": App.Recorder.KeepThroughSilence = true; break;
                case "stopRecording": var stopped = App.StopRecording(); Reply(message, new { sessionId = stopped?.Id ?? "" }); break;
                case "marker": App.AddMarker(); break;
                case "editChange": App.EditChange((int)Number(message, "index", -1), message.TryGetProperty("frame", out var frameValue) && frameValue.ValueKind == JsonValueKind.Number ? (long)frameValue.GetDouble() : null, message.TryGetProperty("artist", out _) ? Text(message, "artist") : null, message.TryGetProperty("title", out _) ? Text(message, "title") : null, message.TryGetProperty("album", out _) ? Text(message, "album") : null, message.TryGetProperty("year", out _) ? Text(message, "year") : null); break;
                case "editChanges": App.EditChanges(message.GetProperty("indexes").EnumerateArray().Select(index => index.GetInt32()).ToList(), message.TryGetProperty("artist", out _) ? Text(message, "artist") : null, message.TryGetProperty("album", out _) ? Text(message, "album") : null, message.TryGetProperty("year", out _) ? Text(message, "year") : null); break;
                case "removeChange": App.RemoveChange((int)Number(message, "index", -1)); break;
                case "setProblem": App.SetProblem((int)Number(message, "index", -1), Text(message, "problem")); break;
                case "setSource": App.MonitorSource = Text(message, "source") is { Length: > 0 } chosenSource ? chosenSource : "auto"; App.RaiseNamesUpdated(); break;
                case "setActiveProfile":
                    if (Text(message, "kind") == "recording") App.Settings.ActiveRecordingProfileId = Text(message, "profileId"); else App.Settings.ActiveOutputProfileId = Text(message, "profileId");
                    App.Settings.Save(); PushSettings();
                    if (App.Recorder.Capturing && !App.Recorder.Recording && Text(message, "kind") == "recording") App.StartMonitoring(Text(message, "profileId"));
                    break;
                case "setTrimSilence": { var gapProfile = App.Settings.RecordingProfileOrActive(Text(message, "profileId")); gapProfile.Gaps.TrimSilence = message.GetProperty("trim").GetBoolean(); if (message.TryGetProperty("meet", out var meet)) gapProfile.Gaps.EndsMeetNextStart = meet.GetBoolean(); App.Settings.Save(); break; }
                case "setAuditionSeconds": App.Settings.Audio.AuditionSeconds = (int)Number(message, "seconds", 5); App.Settings.Save(); break;
                // ---- Sessions ----
                case "sessions": Reply(message, await Task.Run(() => SessionStore.List(App.Settings).Select(Summary).ToList())); break;
                case "openSession":
                    var sessionId = Text(message, "id");
                    var opened = await App.OpenSessionAsync(sessionId, fraction => Push(JsonSerializer.Serialize(new { type = "openProgress", id = sessionId, fraction })));
                    Reply(message, SessionDetail(opened.session));
                    App.SuggestTags(sessionId);
                    break;
                case "suggestAgain": App.SuggestTags(Text(message, "id"), true); break;
                case "albumKeep":
                    if (App.TryGetOpen(Text(message, "id"), out var keeping) && keeping.session.Albums.ElementAtOrDefault((int)Number(message, "album")) is { } keptMatch)
                    {
                        var keptField = Text(message, "field");
                        if (message.TryGetProperty("keep", out var keepFlag) && keepFlag.GetBoolean()) { if (!keptMatch.Kept.Contains(keptField)) keptMatch.Kept.Add(keptField); }
                        else keptMatch.Kept.Remove(keptField);
                        keeping.session.Save();
                    }
                    break;
                case "peaks": Reply(message, await Task.Run(() => PeaksFor(Text(message, "id"), Number(message, "start"), Number(message, "end"), (int)Number(message, "columns", 800)))); break;
                case "saveSession":
                    if (App.TryGetOpen(Text(message, "id"), out var saving))
                    {
                        if (message.TryGetProperty("tracks", out var tracksElement)) saving.session.Tracks = tracksElement.Deserialize<List<Track>>(Session.JsonOptions) ?? saving.session.Tracks;
                        if (Text(message, "name") is { Length: > 0 } newName) saving.session.Name = newName;
                        if (Text(message, "outputProfileId") is { Length: > 0 } outputProfileId) saving.session.OutputProfileId = outputProfileId;
                        saving.session.Save();
                        if (message.TryGetProperty("name", out _)) App.NotifySessionsChanged();
                    }
                    break;
                case "redetect":
                    if (App.TryGetOpen(Text(message, "id"), out var redetecting))
                    {
                        var profile = App.Settings.RecordingProfileOrActive(Text(message, "profileId") is { Length: > 0 } chosenProfile ? chosenProfile : redetecting.session.RecordingProfileId);
                        var rebuilt = await Task.Run(() => TrackBuilder.Build(redetecting.session, redetecting.peaks, profile));
                        Reply(message, rebuilt);
                    }
                    break;
                case "spectrogram":
                    if (App.TryGetOpen(Text(message, "id"), out var spectro)) Reply(message, await Task.Run(() => Spectrogram(spectro.audio, Number(message, "start"), Number(message, "end"), (int)Number(message, "columns", 800), (int)Number(message, "rows", 256))));
                    else Reply(message, new { rows = 0 });
                    break;
                // ---- Undo history (SPEC.md › Editing): snapshots of the track list in the session folder ----
                case "snapshot":
                    if (App.TryGetOpen(Text(message, "id"), out var snapping))
                    {
                        var history = LoadHistory(snapping.session);
                        var tracksJson = message.GetProperty("tracks").GetRawText();
                        if (history.Count == 0 || history[^1].TracksJson != tracksJson) { history.Add(new Snapshot { At = DateTime.Now, Label = Text(message, "label"), TracksJson = tracksJson }); if (history.Count > 80) history.RemoveRange(0, history.Count - 80); SaveHistory(snapping.session, history); }
                    }
                    break;
                case "history": Reply(message, App.TryGetOpen(Text(message, "id"), out var listing) ? LoadHistory(listing.session).Select((snapshot, index) => new { index, at = snapshot.At, label = snapshot.Label, tracks = JsonDocument.Parse(snapshot.TracksJson).RootElement.GetArrayLength() }).Reverse().ToList() : new()); break;
                case "restoreSnapshot":
                    if (App.TryGetOpen(Text(message, "id"), out var restoring) && LoadHistory(restoring.session) is { } snapshots && (int)Number(message, "index", -1) is var snapshotIndex && snapshotIndex >= 0 && snapshotIndex < snapshots.Count) ReplyRawJson(message, snapshots[snapshotIndex].TracksJson); else Reply(message, new { error = "That snapshot is gone." });
                    break;
                case "nearestZero":
                    if (App.TryGetOpen(Text(message, "id"), out var zeroing)) Reply(message, await Task.Run(() => NearestZeroCrossing(zeroing.audio, (long)Number(message, "frame"), (int)Number(message, "radius", 2000))));
                    break;
                case "deleteSession": App.DeleteSession(Text(message, "id")); Reply(message, true); break;
                // ---- Tags (SPEC.md › Tags) ----
                case "lookup": Reply(message, await TagLookup.SearchAsync(Text(message, "source"), Text(message, "artist"), Text(message, "title"), Text(message, "album"), App.Settings.General.DiscogsToken)); break;
                case "fetchArt":
                    if (App.TryGetOpen(Text(message, "id"), out var artSession))
                    {
                        string savedArt;
                        try { savedArt = await TagLookup.SaveArtAsync(Text(message, "url"), artSession.session.Folder); }
                        catch (HttpRequestException) { Reply(message, new { error = "That release has no cover picture online." }); break; }
                        Reply(message, new { path = savedArt, thumb = TagLookup.Thumbnail(savedArt) });
                    }
                    break;
                case "chooseArt":
                    if (App.TryGetOpen(Text(message, "id"), out var chooseSession))
                    {
                        var pictureDialog = new Microsoft.Win32.OpenFileDialog { Filter = "Pictures (*.jpg, *.jpeg, *.png)|*.jpg;*.jpeg;*.png", Title = "Album art" };
                        if (pictureDialog.ShowDialog(this) != true) { Reply(message, new { path = "" }); break; }
                        var chosenArt = TagLookup.SaveArtBytes(await File.ReadAllBytesAsync(pictureDialog.FileName), chooseSession.session.Folder);
                        Reply(message, new { path = chosenArt, thumb = TagLookup.Thumbnail(chosenArt) });
                    }
                    break;
                // Files already there from an earlier export (SPEC.md › Export): which tracks have one, and taking them as exported after a check
                case "existingExports":
                    {
                        var existingSession = Session.Load(Text(message, "id"));
                        if (existingSession == null) { Reply(message, new List<object>()); break; }
                        var askIds = message.GetProperty("trackIds").EnumerateArray().Select(element => element.GetString() ?? "").ToHashSet();
                        var sessionProfile = App.Settings.OutputProfileOrActive(Text(message, "profileId"));
                        var numbersForExisting = Exporter.AlbumNumbers(existingSession);
                        var found = new List<object>();
                        foreach (var track in existingSession.Tracks.Where(track => askIds.Contains(track.Id) && !track.Skip))
                        {
                            var profile = track.OutputProfileId != "" && App.Settings.OutputProfiles.FirstOrDefault(candidate => candidate.Id == track.OutputProfileId) is { } own ? own : sessionProfile;
                            var rootFolder = profile.Folder.Trim() == "" ? AppPaths.DefaultOutputFolder : profile.Folder.Trim();
                            var full = Path.Combine(rootFolder, Exporter.RelativePath(existingSession, track, profile, numbersForExisting));
                            if (File.Exists(full)) found.Add(new { trackId = track.Id, path = full });
                        }
                        Reply(message, found);
                    }
                    break;
                case "adoptExports":
                    if (App.TryGetOpen(Text(message, "id"), out var adopting))
                    {
                        var adopted = 0; var failed = new List<string>();
                        foreach (var element in message.GetProperty("files").EnumerateArray())
                        {
                            var trackId = Text(element, "trackId"); var filePath = Text(element, "path");
                            if (adopting.session.Tracks.FirstOrDefault(track => track.Id == trackId) is not { } track) continue;
                            var problem = await Task.Run(() => Verifier.Check(filePath, track, adopting.session.SampleRate));
                            track.ExportedPath = filePath; track.Exported = problem == ""; track.Verified = problem == ""; track.VerifyError = problem;
                            if (problem == "") adopted++; else failed.Add(track.Title);
                        }
                        adopting.session.Save();
                        Reply(message, new { adopted, failed, tracks = adopting.session.Tracks });
                        App.NotifySessionsChanged();
                    }
                    break;
                case "openOutputFolder":
                    {
                        var profile = App.Settings.OutputProfileOrActive(Text(message, "profileId"));
                        var outputFolder = profile.Folder.Trim() == "" ? AppPaths.DefaultOutputFolder : profile.Folder.Trim();
                        Directory.CreateDirectory(outputFolder); Process.Start("explorer.exe", $"\"{outputFolder}\"");
                    }
                    break;
                case "artData":
                    if (App.TryGetOpen(Text(message, "id"), out var droppedOn))
                    {
                        try { var droppedArt = TagLookup.SaveArtBytes(Convert.FromBase64String(Text(message, "data")), droppedOn.session.Folder); Reply(message, new { path = droppedArt, thumb = TagLookup.Thumbnail(droppedArt), error = "" }); }
                        catch (Exception dropError) { Reply(message, new { path = "", error = dropError.Message }); }
                    }
                    break;
                case "pasteArt":
                    if (App.TryGetOpen(Text(message, "id"), out var pasteSession))
                    {
                        var (pastedBytes, pasteUrl) = ClipboardPicture();
                        if (pastedBytes == null && pasteUrl != "") { try { pastedBytes = await TagLookup.ArtBytesAsync(pasteUrl); } catch { } }
                        if (pastedBytes == null) { Reply(message, new { path = "", error = "The clipboard has no picture. Copy an image (or a picture file, or its web address) and paste again." }); break; }
                        var pastedArt = TagLookup.SaveArtBytes(pastedBytes, pasteSession.session.Folder);
                        Reply(message, new { path = pastedArt, thumb = TagLookup.Thumbnail(pastedArt), error = "" });
                    }
                    break;
                case "exportPaths":
                    if (App.TryGetOpen(Text(message, "id"), out var pathSession))
                    {
                        var numbersForPaths = Exporter.AlbumNumbers(pathSession.session);
                        var sessionProfile = App.Settings.OutputProfileOrActive(Text(message, "profileId"));
                        Reply(message, pathSession.session.Tracks.ToDictionary(track => track.Id, track =>
                        {
                            var profile = track.OutputProfileId != "" && App.Settings.OutputProfiles.FirstOrDefault(candidate => candidate.Id == track.OutputProfileId) is { } own ? own : sessionProfile;
                            var (number, count) = numbersForPaths.TryGetValue(track, out var found) ? found : (0, 0);
                            return new { path = Exporter.RelativePath(pathSession.session, track, profile, numbersForPaths), number = track.TrackNumber > 0 ? track.TrackNumber : number, count = track.TrackCount > 0 ? track.TrackCount : count, comment = track.Comment != "" ? track.Comment : profile.Comment };
                        }));
                    }
                    break;
                case "thumbs": Reply(message, message.GetProperty("paths").EnumerateArray().Select(path => path.GetString() ?? "").Distinct().ToDictionary(path => path, path => File.Exists(path) ? TagLookup.Thumbnail(path) : "")); break;
                case "openFolder": var folder = Text(message, "path"); if (File.Exists(folder)) Process.Start("explorer.exe", $"/select,\"{folder}\""); else if (Directory.Exists(folder)) Process.Start("explorer.exe", $"\"{folder}\""); break;
                // ---- Auditioning ----
                case "play":
                    // Recording what the PC plays would record an audition too (SPEC.md › Workflow): no auditioning on that device meanwhile
                    if (App.Recorder.Recording && App.Recorder.Session is { Loopback: true } recordingNow && (App.Settings.Audio.AuditionDeviceId == "" || App.Settings.Audio.AuditionDeviceId == App.Settings.RecordingProfileOrActive(recordingNow.RecordingProfileId).DeviceId))
                    { App.ShowTip("Not while recording", "Another session is recording what this PC plays, so anything you hear now would be recorded too. Tag and export meanwhile; listen once the recording stops, or pick another audition device in Settings › Audio."); break; }
                    if (App.TryGetOpen(Text(message, "id"), out var playing))
                    {
                        var segments = message.GetProperty("segments").EnumerateArray().Select(segment => (segment[0].GetInt64(), segment[1].GetInt64())).ToList();
                        var tracks = playing.session.Tracks.ToList(); var rate = playing.session.SampleRate;
                        Track? lastTrack = null; // looked up once per track, not per sample (this runs on the audio thread)
                        Func<long, float> gain = frame =>
                        {
                            if (lastTrack == null || frame < lastTrack.Start || frame >= lastTrack.End) lastTrack = tracks.FirstOrDefault(candidate => frame >= candidate.Start && frame < candidate.End);
                            var track = lastTrack;
                            return track == null || (track.FadeInMs == 0 && track.FadeOutMs == 0) ? 1f : Exporter.FadeGain(frame, track, (long)track.FadeInMs * rate / 1000, (long)track.FadeOutMs * rate / 1000);
                        };
                        App.Player.Play(playing.session.AudioPath, playing.session.Id, segments, gain, App.Settings.Audio.AuditionDeviceId);
                        playheadTimer.Start(); Push(App.StatusJson());
                    }
                    break;
                case "stopPlayback": App.Player.Stop(); Push(App.StatusJson()); Push("{\"type\":\"playhead\",\"frame\":-1}"); break;
                // ---- Export (SPEC.md › Export) ----
                case "export":
                    // Exports run one at a time in the background, whatever the user opens meanwhile; later ones wait their turn (SPEC.md › Export)
                    if (App.Exporting) { exportQueue.Add(message.Clone()); App.ExportQueue.Add(Text(message, "id")); Push(App.StatusJson()); break; }
                    await RunExportAsync(message.Clone());
                    break;
                case "unqueueExport":
                    {
                        var unqueueId = Text(message, "id");
                        var job = exportQueue.FirstOrDefault(candidate => Text(candidate, "id") == unqueueId);
                        if (job.ValueKind != JsonValueKind.Undefined) { exportQueue.Remove(job); App.ExportQueue.Remove(unqueueId); Reply(job, new { done = 0, verified = 0, error = "", unqueued = true }); Push(App.StatusJson()); }
                    }
                    break;
                case "cancelExport": exportCancel?.Cancel(); break;
                // ---- Window ----
                case "edgeColour": LookLog("edge " + Text(message, "colour")); SetEdgeColour(Text(message, "colour")); break;
                case "resizeWindow": if (resizeHitCodes.TryGetValue(Text(message, "edge"), out var hitCode)) { ReleaseCapture(); SendMessage(new WindowInteropHelper(this).Handle, 0xA1 /*WM_NCLBUTTONDOWN*/, (IntPtr)hitCode, IntPtr.Zero); } break;
                case "dragWindow": ReleaseCapture(); SendMessage(new WindowInteropHelper(this).Handle, 0xA1 /*WM_NCLBUTTONDOWN*/, (IntPtr)2 /*HTCAPTION*/, IntPtr.Zero); webView.Focus(); break;
                case "windowControl":
                    switch (Text(message, "action")) { case "minimise": WindowState = WindowState.Minimized; break; case "maximise": WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; break; case "close": Close(); break; }
                    break;
                case "theme": darkTheme = message.TryGetProperty("dark", out var darkValue) && darkValue.GetBoolean(); ApplyWindowStyle(); break;
            }
        }
        catch (Exception bridgeError)
        {
            CrashReporter.Write("UI request failed: " + Text(message, "type"), bridgeError, false);
            if (message.TryGetProperty("requestId", out _)) Reply(message, new { error = bridgeError.Message });
            else App.ShowTip("Something went wrong", bridgeError.Message);
        }
    }

    static object Summary(Session session) => new
    {
        id = session.Id, name = session.Name, created = session.Created, seconds = session.SampleRate == 0 ? 0 : (double)session.TotalFrames / session.SampleRate,
        tracks = session.Tracks.Count(track => !track.Skip), exported = session.Tracks.Count(track => track.Exported && !track.Skip), verified = session.Tracks.Count(track => track.Verified && !track.Skip), recording = session.Recording && App.Instance.Recorder.Session?.Id == session.Id, crashed = session.Recording && App.Instance.Recorder.Session?.Id != session.Id,
    };

    static object SessionDetail(Session session) => new
    {
        id = session.Id, name = session.Name, created = session.Created, device = session.Device, loopback = session.Loopback, sampleRate = session.SampleRate, channels = session.Channels, bitDepth = session.BitDepth,
        totalFrames = session.TotalFrames, recordingProfileId = session.RecordingProfileId, outputProfileId = session.OutputProfileId, clippedSamples = session.ClippedSamples, changes = session.Changes, dropouts = session.Dropouts, clips = session.Clips, quality = session.Quality, albumCheck = session.AlbumCheck, cutOff = session.CutOff, otherSounds = session.OtherSounds, albums = session.Albums, albumLooked = session.AlbumLooked, tracks = session.Tracks, folder = session.Folder,
    };

    // Waveform data for [start, end) in columns: min/max per column for each channel, or the samples themselves when zoomed in that far
    object PeaksFor(string sessionId, double start, double end, int columns)
    {
        columns = Math.Clamp(columns, 1, 8000);
        Peaks? peaks; SessionAudio? audio = null; long totalFrames;
        if (App.TryGetOpen(sessionId, out var open)) { peaks = open.peaks; audio = open.audio; totalFrames = open.audio.TotalFrames; }
        else if (App.Recorder.Session?.Id == sessionId && App.Recorder.Peaks != null) { peaks = App.Recorder.Peaks; totalFrames = peaks.BlockCount * Peaks.BlockFrames; }
        else return new { mode = "none" };
        start = Math.Max(0, start); end = Math.Max(start + 1, end);
        var framesPerColumn = (end - start) / columns;
        if (framesPerColumn >= Peaks.BlockFrames || audio == null)
        {
            var columnsData = peaks.Columns(start / Peaks.BlockFrames, framesPerColumn / Peaks.BlockFrames, columns);
            return new { mode = "peaks", start, end, channels = columnsData.Select(Round) };
        }
        var firstFrame = (long)Math.Floor(start); var frameCount = (int)Math.Min(totalFrames - firstFrame, Math.Ceiling(end) - firstFrame + 1);
        if (frameCount <= 0) return new { mode = "none" };
        var channels = audio.Channels; var buffer = new float[frameCount * channels];
        var read = audio.Read(firstFrame, frameCount, buffer);
        if (framesPerColumn < 1.5)
        {
            var samples = Enumerable.Range(0, channels).Select(channel => Round(Enumerable.Range(0, read).Select(frame => buffer[frame * channels + channel]).ToArray()));
            return new { mode = "samples", start = firstFrame, end, channels = samples };
        }
        var result = Enumerable.Range(0, channels).Select(_ => new float[columns * 2]).ToArray();
        for (var column = 0; column < columns; column++)
        {
            var from = (int)(start + column * framesPerColumn - firstFrame); var to = (int)Math.Min(read, start + (column + 1) * framesPerColumn - firstFrame);
            if (to <= from) to = Math.Min(read, from + 1);
            for (var channel = 0; channel < channels; channel++)
            {
                float min = 0, max = 0;
                for (var frame = Math.Max(0, from); frame < to; frame++) { var sample = buffer[frame * channels + channel]; if (frame == Math.Max(0, from)) { min = max = sample; } else { if (sample < min) min = sample; if (sample > max) max = sample; } }
                result[channel][column * 2] = min; result[channel][column * 2 + 1] = max;
            }
        }
        return new { mode = "peaks", start, end, channels = result.Select(Round) };
    }

    record Snapshot { public DateTime At { get; set; } public string Label { get; set; } = ""; public string TracksJson { get; set; } = "[]"; }
    static List<Snapshot> LoadHistory(Session session) { try { return File.Exists(session.HistoryPath) ? JsonSerializer.Deserialize<List<Snapshot>>(File.ReadAllText(session.HistoryPath)) ?? new() : new(); } catch { return new(); } }
    static void SaveHistory(Session session, List<Snapshot> history) { try { File.WriteAllText(session.HistoryPath, JsonSerializer.Serialize(history)); } catch { } }
    void ReplyRawJson(JsonElement message, string dataJson) { if (message.TryGetProperty("requestId", out var requestId)) Push($"{{\"type\":\"result\",\"id\":{requestId.GetInt64()},\"data\":{dataJson}}}"); }

    // The spectrogram of [start, end): one short-window spectrum per column, rows from Nyquist (top) to 0 Hz, as a base64 string of bytes
    static object Spectrogram(SessionAudio audio, double start, double end, int columns, int rows)
    {
        columns = Math.Clamp(columns, 1, 4000); rows = Math.Clamp(rows, 16, 512);
        var bytes = new byte[columns * rows]; var window = new float[SpectralCutoff.SpectrogramWindow * audio.Channels]; var framesPerColumn = (end - start) / columns;
        for (var column = 0; column < columns; column++)
        {
            var centre = (long)(start + (column + 0.5) * framesPerColumn); var first = Math.Max(0, centre - SpectralCutoff.SpectrogramWindow / 2);
            Array.Clear(window);
            var read = audio.Read(first, SpectralCutoff.SpectrogramWindow, window);
            if (read > 0) SpectralCutoff.Column(window, audio.Channels, bytes.AsSpan(column * rows, rows));
        }
        return new { columns, rows, start, end, nyquist = audio.SampleRate / 2, data = Convert.ToBase64String(bytes) };
    }

    static double[] Round(float[] values) => values.Select(value => Math.Round(value, 5)).ToArray();

    // The closest frame to this one where the signal (all channels summed) crosses zero, within the radius; the frame itself if none
    static long NearestZeroCrossing(SessionAudio audio, long frame, int radius)
    {
        var first = Math.Max(0, frame - radius); var count = (int)Math.Min(audio.TotalFrames - first, radius * 2L + 1);
        if (count < 2) return frame;
        var buffer = new float[count * audio.Channels]; var read = audio.Read(first, count, buffer);
        float Sum(int index) { var total = 0f; for (var channel = 0; channel < audio.Channels; channel++) total += buffer[index * audio.Channels + channel]; return total; }
        long best = frame; var bestDistance = long.MaxValue;
        for (var index = 1; index < read; index++)
        {
            float previous = Sum(index - 1), current = Sum(index);
            if (!(previous <= 0 && current >= 0 || previous >= 0 && current <= 0)) continue;
            var crossing = first + (Math.Abs(previous) <= Math.Abs(current) ? index - 1 : index);
            var distance = Math.Abs(crossing - frame);
            if (distance < bestDistance) { bestDistance = distance; best = crossing; }
        }
        return best;
    }

    void PushLevels()
    {
        if (!App.Recorder.Capturing) { levelTimer.Stop(); return; }
        var recorder = App.Recorder;
        Push(JsonSerializer.Serialize(new { type = "levels", peaks = recorder.TakePeaks(), clipped = recorder.ClippedSamples, seconds = recorder.SecondsRecorded, bytes = recorder.BytesWritten, minutesLeft = recorder.Recording ? recorder.MinutesLeftUnderLimit : -1, silentSeconds = recorder.SilentSeconds, silenceStopIn = App.SilenceStopIn() }));
        App.CheckSilence();
    }

    // The name sources the Record view offers, with what each says now
    public void PushSources() => Push(JsonSerializer.Serialize(new { type = "sources", sources = App.Watcher.Sources.Select(source => new { source.Key, source.Kind, source.App, source.Label, source.Artist, source.Title, source.Album, source.AlbumArtist, source.TrackNumber, source.TrackCount, source.Subtitle, source.Genres, source.Picture, source.Playing, source.Rule }), current = App.CurrentName == null ? null : new { App.CurrentName.Key, App.CurrentName.Artist, App.CurrentName.Title } }, camel));

    void PushSettings()
    {
        var settings = App.Settings;
        Push(JsonSerializer.Serialize(new
        {
            type = "settings",
            hints = new { hidden = settings.General.HideHints, dismissed = settings.General.DismissedHints },
            recordingProfiles = settings.RecordingProfiles.Select(profile => new { profile.Id, profile.Name, profile.Loopback, profile.DeviceName, profile.BitDepth, TrimSilence = profile.Gaps.TrimSilence, EndsMeet = profile.Gaps.EndsMeetNextStart }),
            outputProfiles = settings.OutputProfiles.Select(profile => new { profile.Id, profile.Name, profile.Format, profile.Template, profile.BitrateKbps }),
            activeRecording = settings.ActiveRecordingProfile.Id, activeOutput = settings.ActiveOutputProfile.Id, auditionSeconds = settings.Audio.AuditionSeconds,
        }, camel));
    }

    public void PushWhatsNew(string fromVersion, string toVersion, List<ReleaseEntry> releases) => Push(JsonSerializer.Serialize(new { type = "whatsNew", fromVersion, toVersion, releases = releases.Select(release => new { version = release.Version, date = release.Published.ToString("d MMM"), notes = release.Notes, url = release.Url }) }));

    // Window state and bounds handed to the relaunched version after an update
    public string DescribeForRestart()
    {
        var shownState = !IsVisible ? "hidden" : WindowState == WindowState.Maximized ? "maximised" : WindowState == WindowState.Minimized ? "minimised" : "normal";
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        return string.Join(",", new object[] { shownState, bounds.Left, bounds.Top, bounds.Width, bounds.Height }.Select(part => Convert.ToString(part, System.Globalization.CultureInfo.InvariantCulture)));
    }

    public void RestoreFromArgument(string argument)
    {
        var parts = argument.Split(',');
        var numbers = parts.Skip(1).Select(part => double.TryParse(part, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number) ? number : double.NaN).ToArray();
        if (numbers.Length == 4 && numbers.All(number => !double.IsNaN(number) && !double.IsInfinity(number)) && numbers[2] > 100 && numbers[3] > 100)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = numbers[0]; Top = numbers[1]; Width = numbers[2]; Height = numbers[3];
        }
        Show();
        if (parts[0] == "maximised") WindowState = WindowState.Maximized; else if (parts[0] == "minimised") WindowState = WindowState.Minimized;
    }

    // ---- Glass and frameless (from WinSqueeze) ----
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr windowHandle, int attribute, ref int value, int size);
    [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr windowHandle, ref FrameMargins margins);
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr windowHandle, int index);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr windowHandle, int index, int value);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr windowHandle, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr windowHandle, int messageId, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern IntPtr DefWindowProc(IntPtr windowHandle, int messageId, IntPtr wParam, IntPtr lParam);
    const int StyleIndex = -16, CaptionStyle = 0x00C00000, SystemMenuStyle = 0x00080000, CaptionColourAttribute = 35, CornerAttribute = 33, RoundCorners = 2;
    const int DarkModeAttribute = 20, BorderColourAttribute = 34, BackdropAttribute = 38, AcrylicBackdrop = 3, NoBackdrop = 1;
    struct FrameMargins { public int Left, Right, Top, Bottom; }
    bool glassActive, darkTheme = true;

    public void ApplyWindowStyle()
    {
        var windowHandle = new WindowInteropHelper(this).Handle;
        if (windowHandle == IntPtr.Zero) return;
        var general = App.Settings.General;
        WindowChrome.SetWindowChrome(this, general.Frameless ? new WindowChrome { CaptionHeight = 0, ResizeBorderThickness = new Thickness(6), GlassFrameThickness = new Thickness(-1), UseAeroCaptionButtons = false, CornerRadius = new CornerRadius(8) } : null);
        var darkValue = darkTheme ? 1 : 0;
        DwmSetWindowAttribute(windowHandle, DarkModeAttribute, ref darkValue, sizeof(int));
        var style = GetWindowLong(windowHandle, StyleIndex);
        var wantedStyle = general.Frameless ? style & ~(CaptionStyle | SystemMenuStyle) : style | CaptionStyle | SystemMenuStyle;
        if (wantedStyle != style) { SetWindowLong(windowHandle, StyleIndex, wantedStyle); SetWindowPos(windowHandle, IntPtr.Zero, 0, 0, 0, 0, 0x0027 /*NOMOVE|NOSIZE|NOZORDER|FRAMECHANGED*/); }
        var captionColour = general.Frameless ? unchecked((int)0xFFFFFFFE) : unchecked((int)0xFFFFFFFF);
        DwmSetWindowAttribute(windowHandle, CaptionColourAttribute, ref captionColour, sizeof(int));
        var corners = RoundCorners;
        DwmSetWindowAttribute(windowHandle, CornerAttribute, ref corners, sizeof(int));
        var borderColour = general.Frameless ? unchecked((int)0xFFFFFFFE) : unchecked((int)0xFFFFFFFF);
        DwmSetWindowAttribute(windowHandle, BorderColourAttribute, ref borderColour, sizeof(int));
        // Acrylic needs Windows 11 22H2+; elsewhere the call fails and the UI stays opaque
        var backdrop = general.Glass ? AcrylicBackdrop : NoBackdrop;
        glassActive = general.Glass && DwmSetWindowAttribute(windowHandle, BackdropAttribute, ref backdrop, sizeof(int)) == 0;
        var margins = glassActive ? new FrameMargins { Left = -1, Right = -1, Top = -1, Bottom = -1 } : new FrameMargins();
        DwmExtendFrameIntoClientArea(windowHandle, ref margins);
        var hwndSource = HwndSource.FromHwnd(windowHandle);
        if (hwndSource?.CompositionTarget != null) hwndSource.CompositionTarget.BackgroundColor = glassActive ? Colors.Transparent : Color.FromRgb(16, 17, 20);
        Background = general.Frameless && edgeColour != null ? new SolidColorBrush(edgeColour.Value) : glassActive ? Brushes.Transparent : new SolidColorBrush(Color.FromRgb(16, 17, 20));
        if (webView.CoreWebView2 != null) webView.DefaultBackgroundColor = glassActive || general.Frameless ? System.Drawing.Color.Transparent : System.Drawing.Color.FromArgb(16, 17, 20);
        LookLog($"style glass={glassActive} wanted={general.Glass} frameless={general.Frameless} dark={darkTheme} edge={edgeColour}");
        UpdateFrameMargin();
        if (hwndSource != null) { hwndSource.RemoveHook(ThemeMessageHook); hwndSource.AddHook(ThemeMessageHook); }
        PushWindowState();
    }

    bool restyleQueued, firstActivationSeen; DateTime lastRestyle;
    async Task ReapplyLookSoonAsync() { await Task.Delay(1500); LookLog("reapply"); ApplyWindowStyle(); PushSkin(); }
    void OnFirstActivation() { if (firstActivationSeen) return; firstActivationSeen = true; LookLog("activated"); ApplyWindowStyle(); PushSkin(); }
    static readonly DateTime lookLogStart = DateTime.Now;
    void LookLog(string line)
    {
        try
        {
            var path = Path.Combine(AppPaths.DataFolder, "look.log");
            if (File.Exists(path) && new FileInfo(path).Length > 64_000) File.Delete(path);
            File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} +{(DateTime.Now - lookLogStart).TotalSeconds:0.0}s v{App.Updater.CurrentVersion} {line}{Environment.NewLine}");
        }
        catch { }
    }
    public void RefreshInactiveGlass() { var windowHandle = new WindowInteropHelper(this).Handle; if (windowHandle != IntPtr.Zero && !IsActive) DefWindowProc(windowHandle, 0x0086, App.Settings.General.GlassWhenInactive && glassActive ? (IntPtr)1 : IntPtr.Zero, (IntPtr)(-1)); }
    IntPtr ThemeMessageHook(IntPtr windowHandle, int messageId, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Windows draws the backdrop solid while the frame looks inactive; telling it the frame is still active keeps the glass
        const int WM_NCACTIVATE = 0x0086;
        if (messageId == WM_NCACTIVATE && wParam == IntPtr.Zero && glassActive && App.Settings.General.GlassWhenInactive) { handled = true; return DefWindowProc(windowHandle, messageId, (IntPtr)1, (IntPtr)(-1)); }
        const int WM_SETTINGCHANGE = 0x001A, WM_THEMECHANGED = 0x031A, WM_DWMCOMPOSITIONCHANGED = 0x031E, WM_DWMCOLORIZATIONCOLORCHANGED = 0x0320;
        if (messageId is WM_SETTINGCHANGE or WM_THEMECHANGED or WM_DWMCOMPOSITIONCHANGED or WM_DWMCOLORIZATIONCOLORCHANGED && !restyleQueued && DateTime.UtcNow - lastRestyle > TimeSpan.FromSeconds(2))
        {
            restyleQueued = true;
            Dispatcher.BeginInvoke(async () => { await Task.Delay(400); restyleQueued = false; LookLog($"theme message 0x{messageId:X}"); lastRestyle = DateTime.UtcNow; ApplyWindowStyle(); PushSkin(); }, DispatcherPriority.ApplicationIdle);
        }
        return IntPtr.Zero;
    }

    static readonly Dictionary<string, int> resizeHitCodes = new() { ["left"] = 10, ["right"] = 11, ["top"] = 12, ["topleft"] = 13, ["topright"] = 14, ["bottom"] = 15, ["bottomleft"] = 16, ["bottomright"] = 17 };
    Color? edgeColour;
    void SetEdgeColour(string cssColour)
    {
        var parts = System.Text.RegularExpressions.Regex.Matches(cssColour, @"[\d.]+").Select(match => double.Parse(match.Value, System.Globalization.CultureInfo.InvariantCulture)).ToList();
        if (parts.Count < 3) return;
        edgeColour = Color.FromArgb((byte)Math.Round((parts.Count > 3 ? parts[3] : 1) * 255), (byte)parts[0], (byte)parts[1], (byte)parts[2]);
        if (App.Settings.General.Frameless) Background = new SolidColorBrush(edgeColour.Value);
    }
    void UpdateFrameMargin() => webView.Margin = App.Settings.General.Frameless && WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);

    // ---- Multi-monitor: size and position remembered per monitor layout; a window left off-screen comes back ----
    static string DisplayKey => string.Join("|", System.Windows.Forms.Screen.AllScreens.Select(screen => $"{screen.Bounds.X},{screen.Bounds.Y},{screen.Bounds.Width}x{screen.Bounds.Height}").OrderBy(text => text));
    public void SaveBounds()
    {
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (bounds.IsEmpty || double.IsNaN(bounds.Width) || double.IsInfinity(bounds.Width) || bounds.Width < 200) return;
        App.Settings.General.WindowBounds[DisplayKey] = new[] { bounds.Left, bounds.Top, bounds.Width, bounds.Height, WindowState == WindowState.Maximized ? 1 : 0 };
    }

    public void RestoreBoundsForDisplays()
    {
        if (App.Settings.General.WindowBounds.TryGetValue(DisplayKey, out var saved) && saved.Length == 5 && saved[2] >= 200 && saved[3] >= 150)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal;
            Left = saved[0]; Top = saved[1]; Width = saved[2]; Height = saved[3];
            if (saved[4] == 1) WindowState = WindowState.Maximized;
        }
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;
        KeepOnScreen();
    }

    void KeepOnScreen()
    {
        if (double.IsNaN(Left) || double.IsNaN(Top)) return;
        var scale = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1;
        var dragPoint = new Point(Left + Math.Min(Width, 400) / 2, Top + 12);
        var onScreen = System.Windows.Forms.Screen.AllScreens.Any(screen => new Rect(screen.WorkingArea.X / scale, screen.WorkingArea.Y / scale, screen.WorkingArea.Width / scale, screen.WorkingArea.Height / scale).Contains(dragPoint));
        if (onScreen) return;
        var workArea = SystemParameters.WorkArea;
        Width = Math.Min(Width, workArea.Width); Height = Math.Min(Height, workArea.Height);
        Left = workArea.Left + (workArea.Width - Width) / 2; Top = workArea.Top + (workArea.Height - Height) / 2;
    }

    public void PushSkin() => Push(JsonSerializer.Serialize(new { type = "skin", skin = App.Settings.General.Skin, accent = App.Settings.General.AccentColour, glassTransparency = App.Settings.General.GlassTransparency, glassTintHue = App.Settings.General.GlassTintHue }));
    public void ApplyUiScale() { if (webView.CoreWebView2 != null) webView.ZoomFactor = Math.Clamp(App.Settings.General.UiScale, 0.75, 1.5); }
    void PushWindowState() => Push(JsonSerializer.Serialize(new { type = "window", glass = glassActive, frameless = App.Settings.General.Frameless, maximised = WindowState == WindowState.Maximized }));

    // Close (X) quits: warnings first if a recording or export would be cut short
    // The window is closing already, so the app finishes up around it rather than closing it again (which WPF refuses, leaving the app running windowless)
    protected override void OnClosing(CancelEventArgs closingArgs)
    {
        if (App.IsQuitting) return;
        if (!App.ConfirmQuit()) { closingArgs.Cancel = true; return; }
        exportCancel?.Cancel();
        App.Quit(true);
    }

    // A picture from the clipboard (SPEC.md › Tags › Cover): PNG or JPEG data as copied by browsers, a bitmap, a copied picture file,
    // or a picture's web address. Returns the bytes, or the address to fetch
    static (byte[]? bytes, string url) ClipboardPicture()
    {
        try
        {
            foreach (var format in new[] { "PNG", "image/png", "JFIF", "image/jpeg" })
                if (Clipboard.ContainsData(format) && Clipboard.GetData(format) is MemoryStream stream && stream.Length > 0) return (stream.ToArray(), "");
            if (Clipboard.ContainsFileDropList())
                foreach (var file in Clipboard.GetFileDropList())
                    if (file != null && new[] { ".jpg", ".jpeg", ".png" }.Contains(Path.GetExtension(file).ToLowerInvariant()) && File.Exists(file)) return (File.ReadAllBytes(file), "");
            if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } image)
            {
                var encoder = new System.Windows.Media.Imaging.JpegBitmapEncoder { QualityLevel = 92 };
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
                using var output = new MemoryStream(); encoder.Save(output); return (output.ToArray(), "");
            }
            if (Clipboard.ContainsText() && Clipboard.GetText().Trim() is var text && (text.StartsWith("http://") || text.StartsWith("https://")) && !text.Contains(' ')) return (null, text);
        }
        catch { }
        return (null, "");
    }
}
