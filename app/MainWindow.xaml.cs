using System.ComponentModel;
using System.Diagnostics;
using System.IO;
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

    public MainWindow()
    {
        InitializeComponent();
        Loaded += async (sender, args) => await InitWebViewAsync();
        SourceInitialized += (sender, args) => ApplyWindowStyle();
        Activated += (sender, args) => OnFirstActivation();
        StateChanged += (sender, args) => { UpdateFrameMargin(); PushWindowState(); };
        App.StatusChanged += () => { Push(App.StatusJson()); levelTimer.IsEnabled = App.Recorder.Capturing; playheadTimer.IsEnabled = App.Player.Playing; if (!App.Player.Playing) Push("{\"type\":\"playhead\",\"frame\":-1}"); };
        App.SessionsChanged += () => Push("{\"type\":\"sessionsChanged\"}");
        App.HintsChanged += PushSettings;
        App.SettingsChanged += PushSettings;
        App.Tip += (title, text) => Push(JsonSerializer.Serialize(new { type = "tip", title, text }));
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
        webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
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
                case "stopRecording": var stopped = App.StopRecording(); Reply(message, new { sessionId = stopped?.Id ?? "" }); break;
                case "marker": App.AddMarker(); break;
                case "setSource": App.MonitorSource = Text(message, "source") is { Length: > 0 } chosenSource ? chosenSource : "auto"; App.RaiseNamesUpdated(); break;
                case "setActiveProfile":
                    if (Text(message, "kind") == "recording") App.Settings.ActiveRecordingProfileId = Text(message, "profileId"); else App.Settings.ActiveOutputProfileId = Text(message, "profileId");
                    App.Settings.Save(); PushSettings();
                    if (App.Recorder.Capturing && !App.Recorder.Recording && Text(message, "kind") == "recording") App.StartMonitoring(Text(message, "profileId"));
                    break;
                case "setAuditionSeconds": App.Settings.Audio.AuditionSeconds = (int)Number(message, "seconds", 5); App.Settings.Save(); break;
                // ---- Sessions ----
                case "sessions": Reply(message, await Task.Run(() => SessionStore.List(App.Settings).Select(Summary).ToList())); break;
                case "openSession":
                    var sessionId = Text(message, "id");
                    var opened = await App.OpenSessionAsync(sessionId, fraction => Push(JsonSerializer.Serialize(new { type = "openProgress", id = sessionId, fraction })));
                    Reply(message, SessionDetail(opened.session));
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
                case "nearestZero":
                    if (App.TryGetOpen(Text(message, "id"), out var zeroing)) Reply(message, await Task.Run(() => NearestZeroCrossing(zeroing.audio, (long)Number(message, "frame"), (int)Number(message, "radius", 2000))));
                    break;
                case "deleteSession": App.DeleteSession(Text(message, "id")); Reply(message, true); break;
                case "openFolder": var folder = Text(message, "path"); if (File.Exists(folder)) Process.Start("explorer.exe", $"/select,\"{folder}\""); else if (Directory.Exists(folder)) Process.Start("explorer.exe", $"\"{folder}\""); break;
                // ---- Auditioning ----
                case "play":
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
                    if (App.Exporting || !App.TryGetOpen(Text(message, "id"), out var exporting)) { Reply(message, new { done = 0, error = "An export is already running." }); break; }
                    var wanted = message.GetProperty("trackIds").EnumerateArray().Select(element => element.GetString() ?? "").ToHashSet();
                    var exportTracks = exporting.session.Tracks.Where(track => wanted.Contains(track.Id)).ToList();
                    var outputProfile = App.Settings.OutputProfileOrActive(Text(message, "profileId"));
                    App.Exporting = true; Push(App.StatusJson());
                    exportCancel = new CancellationTokenSource();
                    var errors = "";
                    var exportedCount = await Task.Run(() => Exporter.Export(exporting.session, exporting.audio, exportTracks, outputProfile, progress => { if (progress.Error != "") errors = progress.Error; Push(JsonSerializer.Serialize(new { type = "exportProgress", done = progress.Done, total = progress.Total, current = progress.Current })); }, exportCancel.Token));
                    exporting.session.OutputProfileId = outputProfile.Id;
                    exporting.session.Save();
                    App.Exporting = false; Push(App.StatusJson());
                    Reply(message, new { done = exportedCount, error = errors, tracks = exporting.session.Tracks, folder = outputProfile.Folder.Trim() == "" ? AppPaths.DefaultOutputFolder : outputProfile.Folder.Trim() });
                    App.NotifySessionsChanged();
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
        tracks = session.Tracks.Count, exported = session.Tracks.Count(track => track.Exported), recording = session.Recording && App.Instance.Recorder.Session?.Id == session.Id, crashed = session.Recording && App.Instance.Recorder.Session?.Id != session.Id,
    };

    static object SessionDetail(Session session) => new
    {
        id = session.Id, name = session.Name, created = session.Created, device = session.Device, loopback = session.Loopback, sampleRate = session.SampleRate, channels = session.Channels, bitDepth = session.BitDepth,
        totalFrames = session.TotalFrames, recordingProfileId = session.RecordingProfileId, outputProfileId = session.OutputProfileId, clippedSamples = session.ClippedSamples, changes = session.Changes, tracks = session.Tracks, folder = session.Folder,
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
        Push(JsonSerializer.Serialize(new { type = "levels", peaks = recorder.TakePeaks(), clipped = recorder.ClippedSamples, seconds = recorder.SecondsRecorded, bytes = recorder.BytesWritten, minutesLeft = recorder.Recording ? recorder.MinutesLeftUnderLimit : -1 }));
    }

    // The name sources the Record view offers, with what each says now
    public void PushSources() => Push(JsonSerializer.Serialize(new { type = "sources", sources = App.Watcher.Sources.Select(source => new { source.Key, source.Kind, source.Label, source.Artist, source.Title, source.Playing, source.Rule }), current = App.CurrentName == null ? null : new { App.CurrentName.Key, App.CurrentName.Artist, App.CurrentName.Title } }, camel));

    void PushSettings()
    {
        var settings = App.Settings;
        Push(JsonSerializer.Serialize(new
        {
            type = "settings",
            hints = new { hidden = settings.General.HideHints, dismissed = settings.General.DismissedHints },
            recordingProfiles = settings.RecordingProfiles.Select(profile => new { profile.Id, profile.Name, profile.Loopback, profile.DeviceName, profile.BitDepth }),
            outputProfiles = settings.OutputProfiles.Select(profile => new { profile.Id, profile.Name, profile.Format, profile.Template }),
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
    protected override void OnClosing(CancelEventArgs closingArgs)
    {
        if (App.IsQuitting) return;
        closingArgs.Cancel = true;
        if (!App.ConfirmQuit()) return;
        exportCancel?.Cancel();
        App.Quit();
    }
}
