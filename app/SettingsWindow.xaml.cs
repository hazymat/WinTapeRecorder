using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using WinTapeRecorder.Audio;
using WinTapeRecorder.Detection;
using WinTapeRecorder.Sessions;
using WinTapeRecorder.Settings;
using WinTapeRecorder.Shell;
namespace WinTapeRecorder;

// Changes apply immediately (no OK/Apply), as WinSqueeze (SPEC.md › Settings window)
public partial class SettingsWindow : Window
{
    App App => App.Instance;
    AppSettings Settings => App.Settings;
    List<AudioDevice> devices = new();
    bool filling;

    public SettingsWindow()
    {
        InitializeComponent(); settingsTabs.SelectedItem = recordingTab;
        devices = SafeDevices();
        WireRecordingTab(); WireDetectionTab(); WireOutputTab(); WireAppearanceTab(); WireGeneralTab(); WireAudioTab(); WireAboutTab(); FillTables();
    }

    public void SelectTab(string tabName)
    {
        var tabByName = new Dictionary<string, TabItem> { ["Recording"] = recordingTab, ["Detection"] = detectionTab, ["Output"] = outputTab, ["Appearance"] = appearanceTab, ["General"] = generalTab, ["Audio"] = audioTab, ["Shortcuts"] = shortcutsTab, ["About"] = aboutTab };
        if (tabByName.TryGetValue(tabName, out var selectedTab)) settingsTabs.SelectedItem = selectedTab;
    }

    static List<AudioDevice> SafeDevices() { try { return AudioDevices.List(); } catch { return new(); } }
    void Changed() { Settings.Save(); App.NotifySettingsChanged(); }

    // A whole-number box: saved as you type when it parses, put back in range when you leave it
    void BindNumber(TextBox box, Func<int> read, Action<int> write, int minimum, int maximum)
    {
        box.TextChanged += (sender, args) => { if (filling) return; if (int.TryParse(box.Text.Trim(), out var value) && value >= minimum && value <= maximum) { write(value); Changed(); } };
        box.LostFocus += (sender, args) => box.Text = read().ToString();
    }

    static string FolderOrDefault(string folder, string fallback) => folder.Trim() == "" ? fallback : folder.Trim();
    static string? ChooseFolder(string startFolder)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { InitialDirectory = Directory.Exists(startFolder) ? startFolder : "" };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
    static void OpenFolder(string folder) { Directory.CreateDirectory(folder); Process.Start("explorer.exe", $"\"{folder}\""); }

    // ---- Recording profiles ----
    RecordingProfile? EditedRecording => recordingList.SelectedItem as RecordingProfile;
    void WireRecordingTab()
    {
        recordingList.ItemsSource = Settings.RecordingProfiles;
        recordingList.SelectionChanged += (sender, args) => ShowRecordingProfile();
        recordingList.SelectedItem = Settings.ActiveRecordingProfile;
        addRecordingButton.Click += (sender, args) => { var profile = new RecordingProfile { Name = "New profile" }; Settings.RecordingProfiles.Add(profile); recordingList.Items.Refresh(); recordingList.SelectedItem = profile; Changed(); };
        copyRecordingButton.Click += (sender, args) =>
        {
            if (EditedRecording is not { } source) return;
            var copy = System.Text.Json.JsonSerializer.Deserialize<RecordingProfile>(System.Text.Json.JsonSerializer.Serialize(source))!; copy.Id = Guid.NewGuid().ToString("N"); copy.Name = source.Name + " copy";
            Settings.RecordingProfiles.Add(copy); recordingList.Items.Refresh(); recordingList.SelectedItem = copy; Changed();
        };
        removeRecordingButton.Click += (sender, args) =>
        {
            if (EditedRecording is not { } profile || Settings.RecordingProfiles.Count < 2) return;
            Settings.RecordingProfiles.Remove(profile); recordingList.Items.Refresh(); recordingList.SelectedIndex = 0; Changed();
        };
        recordingNameBox.TextChanged += (sender, args) => { if (filling || EditedRecording is not { } profile) return; profile.Name = recordingNameBox.Text; recordingList.Items.Refresh(); Changed(); };
        refreshRecordingDevicesButton.Click += (sender, args) => { devices = SafeDevices(); ShowRecordingProfile(); FillAuditionDevices(); };
        recordingDeviceCombo.SelectionChanged += (sender, args) =>
        {
            if (filling || EditedRecording is not { } profile || recordingDeviceCombo.SelectedItem is not AudioDevice device) return;
            profile.DeviceId = device.Id == "default-output" ? "" : device.Id; profile.DeviceName = device.Id == "default-output" ? "" : device.Name; profile.Loopback = device.IsOutput; Changed();
        };
        bitDepthCombo.SelectionChanged += (sender, args) => { if (!filling && EditedRecording is { } profile && bitDepthCombo.SelectedItem is ComboBoxItem { Tag: string tag }) { profile.BitDepth = int.Parse(tag); Changed(); } };
        chooseSessionsFolderButton.Click += (sender, args) => { if (EditedRecording is { } profile && ChooseFolder(SessionStore.FolderFor(profile)) is { } folder) { profile.SessionsFolder = folder; sessionsFolderBox.Text = folder; Changed(); App.NotifySessionsChanged(); } };
        openSessionsFolderButton.Click += (sender, args) => { if (EditedRecording is { } profile) OpenFolder(SessionStore.FolderFor(profile)); };
        useMediaInfoBox.Click += (sender, args) => { if (EditedRecording is { } profile) { profile.UseMediaInfo = useMediaInfoBox.IsChecked == true; Changed(); } };
        useWindowTitlesBox.Click += (sender, args) => { if (EditedRecording is { } profile) { profile.UseWindowTitles = useWindowTitlesBox.IsChecked == true; Changed(); } };
        splitOnGapsBox.Click += (sender, args) => { if (EditedRecording is { } profile) { profile.SplitOnGaps = splitOnGapsBox.IsChecked == true; Changed(); } };
        trimSilenceBox.Click += (sender, args) => { if (EditedRecording is { } profile) { profile.Gaps.TrimSilence = trimSilenceBox.IsChecked == true; Changed(); } };
        GapSettings Gaps() => EditedRecording?.Gaps ?? new GapSettings();
        BindNumber(latencyBox, () => EditedRecording?.LatencyMs ?? 0, value => { if (EditedRecording is { } profile) profile.LatencyMs = value; }, -10000, 10000);
        BindNumber(minGapBox, () => Gaps().MinGapMs, value => Gaps().MinGapMs = value, 50, 60000);
        BindNumber(attackBox, () => Gaps().AttackMs, value => Gaps().AttackMs = value, 1, 5000);
        BindNumber(releaseBox, () => Gaps().ReleaseMs, value => Gaps().ReleaseMs = value, 1, 10000);
        BindNumber(preRollBox, () => Gaps().PreRollMs, value => Gaps().PreRollMs = value, 0, 10000);
        BindNumber(postRollBox, () => Gaps().PostRollMs, value => Gaps().PostRollMs = value, 0, 10000);
        BindNumber(snapBox, () => Gaps().SnapWindowMs, value => Gaps().SnapWindowMs = value, 0, 60000);
        BindNumber(minTrackBox, () => Gaps().MinTrackSeconds, value => Gaps().MinTrackSeconds = value, 1, 3600);
        thresholdSlider.ValueChanged += (sender, args) => { thresholdText.Text = $"{thresholdSlider.Value:0} dBFS"; if (!filling && EditedRecording is { } profile) { profile.Gaps.ThresholdDb = thresholdSlider.Value; Changed(); } };
        ShowRecordingProfile();
    }

    void ShowRecordingProfile()
    {
        if (EditedRecording is not { } profile) return;
        filling = true;
        recordingNameBox.Text = profile.Name;
        var choices = new List<AudioDevice> { new("default-output", "Windows default output", true, false, 0, 0) };
        choices.AddRange(devices);
        var current = profile.DeviceId == "" ? choices[0] : choices.FirstOrDefault(device => device.Id == profile.DeviceId) ?? choices.FirstOrDefault(device => device.Name == profile.DeviceName && device.IsOutput == profile.Loopback);
        if (current == null && profile.DeviceId != "") { current = new AudioDevice(profile.DeviceId, profile.DeviceName + " (not connected)", profile.Loopback, false, 0, 0); choices.Add(current); }
        recordingDeviceCombo.ItemsSource = choices; recordingDeviceCombo.SelectedItem = current;
        bitDepthCombo.SelectedItem = bitDepthCombo.Items.Cast<ComboBoxItem>().FirstOrDefault(item => (string)item.Tag == profile.BitDepth.ToString());
        sessionsFolderBox.Text = SessionStore.FolderFor(profile);
        useMediaInfoBox.IsChecked = profile.UseMediaInfo; useWindowTitlesBox.IsChecked = profile.UseWindowTitles; splitOnGapsBox.IsChecked = profile.SplitOnGaps; trimSilenceBox.IsChecked = profile.Gaps.TrimSilence;
        latencyBox.Text = profile.LatencyMs.ToString(); minGapBox.Text = profile.Gaps.MinGapMs.ToString(); attackBox.Text = profile.Gaps.AttackMs.ToString(); releaseBox.Text = profile.Gaps.ReleaseMs.ToString();
        preRollBox.Text = profile.Gaps.PreRollMs.ToString(); postRollBox.Text = profile.Gaps.PostRollMs.ToString(); snapBox.Text = profile.Gaps.SnapWindowMs.ToString(); minTrackBox.Text = profile.Gaps.MinTrackSeconds.ToString();
        thresholdSlider.Value = profile.Gaps.ThresholdDb; thresholdText.Text = $"{profile.Gaps.ThresholdDb:0} dBFS";
        removeRecordingButton.IsEnabled = Settings.RecordingProfiles.Count > 1;
        filling = false;
    }

    // ---- Detection rules ----
    DetectionRule? EditedRule => rulesList.SelectedItem as DetectionRule;
    void WireDetectionTab()
    {
        browsersBox.Text = Settings.BrowserProcesses;
        browsersBox.TextChanged += (sender, args) => { Settings.BrowserProcesses = browsersBox.Text; Changed(); };
        rulesList.ItemsSource = Settings.DetectionRules;
        rulesList.SelectionChanged += (sender, args) => ShowRule();
        if (Settings.DetectionRules.Count > 0) rulesList.SelectedIndex = 0;
        addRuleButton.Click += (sender, args) => { var rule = new DetectionRule { Name = "New rule", Pattern = @"^(?<artist>.+?) - (?<title>.+)$" }; Settings.DetectionRules.Add(rule); rulesList.Items.Refresh(); rulesList.SelectedItem = rule; Changed(); };
        removeRuleButton.Click += (sender, args) => { if (EditedRule is { } rule) { Settings.DetectionRules.Remove(rule); rulesList.Items.Refresh(); Changed(); ShowRule(); } };
        void Move(int step)
        {
            if (EditedRule is not { } rule) return;
            var index = Settings.DetectionRules.IndexOf(rule); var target = index + step;
            if (target < 0 || target >= Settings.DetectionRules.Count) return;
            Settings.DetectionRules.RemoveAt(index); Settings.DetectionRules.Insert(target, rule); rulesList.Items.Refresh(); rulesList.SelectedItem = rule; Changed();
        }
        ruleUpButton.Click += (sender, args) => Move(-1);
        ruleDownButton.Click += (sender, args) => Move(1);
        ruleNameBox.TextChanged += (sender, args) => { if (!filling && EditedRule is { } rule) { rule.Name = ruleNameBox.Text; rulesList.Items.Refresh(); Changed(); } };
        ruleEnabledBox.Click += (sender, args) => { if (EditedRule is { } rule) { rule.Enabled = ruleEnabledBox.IsChecked == true; rulesList.Items.Refresh(); Changed(); } };
        rulePatternBox.TextChanged += (sender, args) =>
        {
            if (filling || EditedRule is not { } rule) return;
            try { _ = new Regex(rulePatternBox.Text); rulePatternError.Text = ""; rule.Pattern = rulePatternBox.Text; Changed(); }
            catch (ArgumentException patternError) { rulePatternError.Text = patternError.Message; }
        };
        testRulesButton.Click += (sender, args) => TestRules();
        resetRulesButton.Click += (sender, args) =>
        {
            if (MessageBox.Show(this, "Put back the YouTube, Bandcamp and Lyrion example rules? Your other rules stay.", "WinTapeRecorder", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
            foreach (var example in DetectionRule.Examples()) { Settings.DetectionRules.RemoveAll(rule => rule.Name == example.Name); Settings.DetectionRules.Add(example); }
            rulesList.Items.Refresh(); Changed();
        };
        ShowRule();
    }

    void ShowRule()
    {
        filling = true;
        var rule = EditedRule; ruleEditor.IsEnabled = rule != null;
        ruleNameBox.Text = rule?.Name ?? ""; ruleEnabledBox.IsChecked = rule?.Enabled ?? false; rulePatternBox.Text = rule?.Pattern ?? ""; rulePatternError.Text = "";
        filling = false;
    }

    void TestRules()
    {
        var lines = new List<string>();
        foreach (var source in App.Watcher.Sources)
        {
            var name = source.Title == "" ? "(nothing playing)" : (source.Artist == "" ? "" : source.Artist + " — ") + source.Title;
            lines.Add(source.Kind == "media" ? $"{source.Label}{(source.Playing ? " (playing)" : "")}\n    → {name}" : $"{source.Label}\n    → {name}  [{(source.Rule == "" ? "no rule matched: whole title" : source.Rule)}]");
        }
        rulesTestBox.Text = lines.Count == 0 ? "No browser windows or media sessions found. Is the browser in the list above?" : string.Join("\n", lines);
    }

    // ---- Output profiles ----
    OutputProfile? EditedOutput => outputList.SelectedItem as OutputProfile;
    void WireOutputTab()
    {
        outputList.ItemsSource = Settings.OutputProfiles;
        outputList.SelectionChanged += (sender, args) => ShowOutputProfile();
        outputList.SelectedItem = Settings.ActiveOutputProfile;
        addOutputButton.Click += (sender, args) => { var profile = new OutputProfile { Name = "New profile" }; Settings.OutputProfiles.Add(profile); outputList.Items.Refresh(); outputList.SelectedItem = profile; Changed(); };
        copyOutputButton.Click += (sender, args) =>
        {
            if (EditedOutput is not { } source) return;
            var copy = System.Text.Json.JsonSerializer.Deserialize<OutputProfile>(System.Text.Json.JsonSerializer.Serialize(source))!; copy.Id = Guid.NewGuid().ToString("N"); copy.Name = source.Name + " copy";
            Settings.OutputProfiles.Add(copy); outputList.Items.Refresh(); outputList.SelectedItem = copy; Changed();
        };
        removeOutputButton.Click += (sender, args) => { if (EditedOutput is { } profile && Settings.OutputProfiles.Count > 1) { Settings.OutputProfiles.Remove(profile); outputList.Items.Refresh(); outputList.SelectedIndex = 0; Changed(); } };
        outputNameBox.TextChanged += (sender, args) => { if (!filling && EditedOutput is { } profile) { profile.Name = outputNameBox.Text; outputList.Items.Refresh(); Changed(); } };
        formatCombo.SelectionChanged += (sender, args) => { if (!filling && EditedOutput is { } profile && formatCombo.SelectedItem is ComboBoxItem { Tag: string format }) { profile.Format = format; Changed(); ShowOutputProfile(); } };
        bitrateCombo.SelectionChanged += (sender, args) => { if (!filling && EditedOutput is { } profile && bitrateCombo.SelectedItem is ComboBoxItem { Tag: string bitrate }) { profile.BitrateKbps = int.Parse(bitrate); Changed(); } };
        chooseOutputFolderButton.Click += (sender, args) => { if (EditedOutput is { } profile && ChooseFolder(FolderOrDefault(profile.Folder, AppPaths.DefaultOutputFolder)) is { } folder) { profile.Folder = folder; outputFolderBox.Text = folder; Changed(); ShowTemplateExample(); } };
        openOutputFolderButton.Click += (sender, args) => { if (EditedOutput is { } profile) OpenFolder(FolderOrDefault(profile.Folder, AppPaths.DefaultOutputFolder)); };
        templateBox.TextChanged += (sender, args) => { if (!filling && EditedOutput is { } profile) { profile.Template = templateBox.Text; Changed(); ShowTemplateExample(); } };
        tagAlbumBox.TextChanged += (sender, args) => { if (!filling && EditedOutput is { } profile) { profile.Album = tagAlbumBox.Text; Changed(); } };
        tagAlbumArtistBox.TextChanged += (sender, args) => { if (!filling && EditedOutput is { } profile) { profile.AlbumArtist = tagAlbumArtistBox.Text; Changed(); } };
        tagGenreBox.TextChanged += (sender, args) => { if (!filling && EditedOutput is { } profile) { profile.Genre = tagGenreBox.Text; Changed(); } };
        tagCommentBox.TextChanged += (sender, args) => { if (!filling && EditedOutput is { } profile) { profile.Comment = tagCommentBox.Text; Changed(); } };
        embedCoverBox.Click += (sender, args) => { if (EditedOutput is { } profile) { profile.EmbedCover = embedCoverBox.IsChecked == true; Changed(); } };
        saveCoverBox.Click += (sender, args) => { if (EditedOutput is { } profile) { profile.SaveCoverFile = saveCoverBox.IsChecked == true; coverNameBox.IsEnabled = profile.SaveCoverFile; Changed(); } };
        coverNameBox.TextChanged += (sender, args) => { if (!filling && EditedOutput is { } profile) { profile.CoverFileName = coverNameBox.Text.Trim(); Changed(); } };
        ShowOutputProfile();
    }

    void ShowOutputProfile()
    {
        if (EditedOutput is not { } profile) return;
        filling = true;
        outputNameBox.Text = profile.Name;
        formatCombo.SelectedItem = formatCombo.Items.Cast<ComboBoxItem>().FirstOrDefault(item => (string)item.Tag == profile.Format);
        bitrateCombo.SelectedItem = bitrateCombo.Items.Cast<ComboBoxItem>().FirstOrDefault(item => (string)item.Tag == profile.BitrateKbps.ToString()) ?? bitrateCombo.Items[3];
        bitratePanel.Visibility = profile.Format is "mp3" or "m4a" ? Visibility.Visible : Visibility.Collapsed;
        outputFolderBox.Text = FolderOrDefault(profile.Folder, AppPaths.DefaultOutputFolder);
        embedCoverBox.IsChecked = profile.EmbedCover; saveCoverBox.IsChecked = profile.SaveCoverFile; coverNameBox.Text = profile.CoverFileName; coverNameBox.IsEnabled = profile.SaveCoverFile;
        templateBox.Text = profile.Template; tagAlbumBox.Text = profile.Album; tagAlbumArtistBox.Text = profile.AlbumArtist; tagGenreBox.Text = profile.Genre; tagCommentBox.Text = profile.Comment;
        removeOutputButton.IsEnabled = Settings.OutputProfiles.Count > 1;
        filling = false;
        ShowTemplateExample();
    }

    void ShowTemplateExample()
    {
        if (EditedOutput is not { } profile) return;
        var example = new Track { Artist = "Nina Simone", Title = "Feeling Good", Album = "I Put a Spell on You", Year = "1965" };
        var session = new Session { Name = "Friday night", Created = DateTime.Now, Device = "Speakers" };
        var relative = NameTemplate.Expand(profile.Template.Replace('\\', '/'), example, 3, 12, session, true).Replace('/', '\\');
        templateExampleText.Text = Path.Combine(FolderOrDefault(profile.Folder, AppPaths.DefaultOutputFolder), relative) + NameTemplate.Extension(profile.Format);
    }

    // ---- Appearance (from WinSqueeze) ----
    void WireAppearanceTab()
    {
        var general = Settings.General;
        var skinNames = new[] { ("auto", "Follow Windows (light or dark)"), ("dark", "Frosted dark"), ("light", "Frosted light") };
        skinBox.ItemsSource = skinNames.Select(skin => new ComboBoxItem { Content = skin.Item2, Tag = skin.Item1 }).ToList();
        skinBox.SelectedItem = ((List<ComboBoxItem>)skinBox.ItemsSource).FirstOrDefault(item => (string)item.Tag == general.Skin) ?? ((List<ComboBoxItem>)skinBox.ItemsSource)[0];
        skinBox.SelectionChanged += (sender, args) => { if (skinBox.SelectedItem is ComboBoxItem { Tag: string skin }) { general.Skin = skin; Settings.Save(); App.ApplySkin(); } };
        void ShowAccent() => accentSwatch.Background = general.AccentColour == "" ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x5c, 0xcf, 0xff)) : new System.Windows.Media.BrushConverter().ConvertFromString(general.AccentColour) as System.Windows.Media.Brush;
        ShowAccent();
        accentChooseButton.Click += (sender, args) =>
        {
            using var colourDialog = new System.Windows.Forms.ColorDialog { FullOpen = true, AnyColor = true };
            if (general.AccentColour != "") colourDialog.Color = System.Drawing.ColorTranslator.FromHtml(general.AccentColour);
            if (colourDialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
            general.AccentColour = $"#{colourDialog.Color.R:x2}{colourDialog.Color.G:x2}{colourDialog.Color.B:x2}"; Settings.Save(); App.ApplySkin(); ShowAccent();
        };
        accentResetButton.Click += (sender, args) => { general.AccentColour = ""; Settings.Save(); App.ApplySkin(); ShowAccent(); };
        uiScaleSlider.Value = Math.Round(general.UiScale * 100); uiScaleText.Text = $"{uiScaleSlider.Value}%";
        uiScaleSlider.ValueChanged += (sender, args) => { general.UiScale = uiScaleSlider.Value / 100; uiScaleText.Text = $"{uiScaleSlider.Value}%"; Settings.Save(); App.ApplyUiScale(); };
        uiScaleResetButton.Click += (sender, args) => uiScaleSlider.Value = 100;
        void ShowGlass()
        {
            glassTransparencyText.Text = general.GlassTransparency < 0 ? $"{(int)glassTransparencySlider.Value}% (default)" : $"{general.GlassTransparency}%";
            glassTintText.Text = general.GlassTintHue == 0 ? "Neutral" : $"{general.GlassTintHue}°";
            glassTintSwatch.Background = new System.Windows.Media.SolidColorBrush(general.GlassTintHue == 0 ? System.Windows.Media.Color.FromRgb(30, 32, 38) : HueColour(general.GlassTintHue));
        }
        var fillingGlass = true;
        glassTransparencySlider.Value = general.GlassTransparency < 0 ? 62 : general.GlassTransparency; glassTintSlider.Value = general.GlassTintHue; fillingGlass = false; ShowGlass();
        glassTransparencySlider.ValueChanged += (sender, args) => { if (fillingGlass) return; general.GlassTransparency = (int)glassTransparencySlider.Value; Settings.Save(); App.ApplySkin(); ShowGlass(); };
        glassTintSlider.ValueChanged += (sender, args) => { if (fillingGlass) return; general.GlassTintHue = (int)glassTintSlider.Value; Settings.Save(); App.ApplySkin(); ShowGlass(); };
        glassResetButton.Click += (sender, args) => { general.GlassTransparency = -1; general.GlassTintHue = 0; fillingGlass = true; glassTransparencySlider.Value = 62; glassTintSlider.Value = 0; fillingGlass = false; Settings.Save(); App.ApplySkin(); ShowGlass(); };
        glassInactiveBox.IsChecked = general.GlassWhenInactive;
        glassInactiveBox.Click += (sender, args) => { general.GlassWhenInactive = glassInactiveBox.IsChecked == true; Settings.Save(); App.RefreshInactiveGlass(); };
        glassBox.IsChecked = general.Glass; framelessBox.IsChecked = general.Frameless;
        glassBox.Click += (sender, args) => { general.Glass = glassBox.IsChecked == true; Settings.Save(); App.ApplyWindowStyle(); };
        framelessBox.Click += (sender, args) => { general.Frameless = framelessBox.IsChecked == true; Settings.Save(); App.ApplyWindowStyle(); };
    }

    static System.Windows.Media.Color HueColour(int hue)
    {
        double hueSixth = hue / 60.0, chroma = 0.55, x = chroma * (1 - Math.Abs(hueSixth % 2 - 1)), lightness = 0.25;
        var (red, green, blue) = hueSixth switch { < 1 => (chroma, x, 0.0), < 2 => (x, chroma, 0.0), < 3 => (0.0, chroma, x), < 4 => (0.0, x, chroma), < 5 => (x, 0.0, chroma), _ => (chroma, 0.0, x) };
        byte Channel(double value) => (byte)Math.Round(Math.Clamp(value + lightness - chroma / 2, 0, 1) * 255);
        return System.Windows.Media.Color.FromRgb(Channel(red), Channel(green), Channel(blue));
    }

    // ---- General ----
    void WireGeneralTab()
    {
        showHintsBox.IsChecked = !Settings.General.HideHints;
        showHintsBox.Click += (sender, args) => { Settings.General.HideHints = showHintsBox.IsChecked != true; Settings.Save(); App.NotifyHintsChanged(); };
        discogsTokenBox.Password = Settings.General.DiscogsToken;
        discogsTokenBox.PasswordChanged += (sender, args) => { Settings.General.DiscogsToken = discogsTokenBox.Password.Trim(); Settings.Save(); };
        resetHintsButton.Click += (sender, args) => { Settings.General.DismissedHints.Clear(); Settings.General.HideHints = false; showHintsBox.IsChecked = true; Settings.Save(); App.NotifyHintsChanged(); };
        exportSettingsButton.Click += (sender, args) =>
        {
            var saveDialog = new Microsoft.Win32.SaveFileDialog { FileName = $"WinTapeRecorder-settings-{DateTime.Now:yyyy-MM-dd}.json", Filter = "WinTapeRecorder settings (*.json)|*.json" };
            if (saveDialog.ShowDialog(this) != true) return;
            File.WriteAllText(saveDialog.FileName, Settings.ExportJson());
            settingsFileText.Text = $"Exported to {saveDialog.FileName}.";
        };
        importSettingsButton.Click += async (sender, args) =>
        {
            var openDialog = new Microsoft.Win32.OpenFileDialog { Filter = "WinTapeRecorder settings (*.json)|*.json" };
            if (openDialog.ShowDialog(this) != true) return;
            try { await App.ReplaceSettingsAsync(AppSettings.Import(File.ReadAllText(openDialog.FileName))); Close(); App.OpenSettings("General"); }
            catch (Exception importError) { settingsFileText.Text = $"Could not import: {importError.Message}"; }
        };
    }

    // ---- Audio ----
    void WireAudioTab()
    {
        FillAuditionDevices();
        auditionDeviceCombo.SelectionChanged += (sender, args) => { if (!filling && auditionDeviceCombo.SelectedItem is AudioDevice device) { Settings.Audio.AuditionDeviceId = device.Id == "default-output" ? "" : device.Id; Changed(); } };
        refreshDevicesButton.Click += (sender, args) => { devices = SafeDevices(); FillAuditionDevices(); ShowRecordingProfile(); };
        auditionSecondsCombo.SelectedItem = auditionSecondsCombo.Items.Cast<ComboBoxItem>().FirstOrDefault(item => (string)item.Tag == Settings.Audio.AuditionSeconds.ToString()) ?? auditionSecondsCombo.Items[1];
        auditionSecondsCombo.SelectionChanged += (sender, args) => { if (auditionSecondsCombo.SelectedItem is ComboBoxItem { Tag: string seconds }) { Settings.Audio.AuditionSeconds = int.Parse(seconds); Changed(); } };
    }

    void FillAuditionDevices()
    {
        filling = true;
        var outputs = new List<AudioDevice> { new("default-output", "Windows default output", true, false, 0, 0) };
        outputs.AddRange(devices.Where(device => device.IsOutput));
        auditionDeviceCombo.ItemsSource = outputs;
        auditionDeviceCombo.SelectedItem = outputs.FirstOrDefault(device => device.Id == Settings.Audio.AuditionDeviceId) ?? outputs[0];
        devicesBox.Text = devices.Count == 0 ? "No audio devices found." : string.Join("\n", devices.Select(device => $"{device.Label}   {(device.SampleRate > 0 ? $"{device.SampleRate / 1000.0:0.#} kHz, {device.Channels} ch" : "")}"));
        filling = false;
    }

    // ---- About: channel, check now, versions with notes, switch to any version (from WinSqueeze) ----
    void WireAboutTab()
    {
        versionText.Text = $"WinTapeRecorder {App.Updater.CurrentVersion}";
        dataFolderText.Text = (AppPaths.IsPortable ? "Portable mode. " : "") + $"Settings and logs: {AppPaths.DataFolder}";
        openLogsButton.Click += (sender, args) => Process.Start(new ProcessStartInfo(AppPaths.LogsFolder) { UseShellExecute = true });
        channelCombo.SelectedItem = channelCombo.Items.Cast<ComboBoxItem>().First(item => (string)item.Tag == Settings.General.UpdateChannel);
        channelCombo.SelectionChanged += async (sender, args) =>
        {
            if (channelCombo.SelectedItem is not ComboBoxItem chosen) return;
            Settings.General.UpdateChannel = (string)chosen.Tag; Settings.Save();
            await LoadVersionsAsync(); await App.Updater.CheckNowAsync();
        };
        autoUpdateBox.IsChecked = Settings.General.AutoUpdate;
        autoUpdateBox.Click += (sender, args) => { Settings.General.AutoUpdate = autoUpdateBox.IsChecked == true; Settings.Save(); };
        checkNowButton.Click += async (sender, args) => await App.Updater.CheckNowAsync();
        restartToUpdateButton.Click += async (sender, args) => await App.Updater.RestartToUpdateAsync();
        refreshVersionsButton.Click += async (sender, args) => await LoadVersionsAsync();
        versionsList.SelectionChanged += (sender, args) => ShowSelectedVersion();
        switchVersionButton.Click += async (sender, args) => { if (versionsList.SelectedItem is VersionItem item) await App.Updater.SwitchToAsync(item.Release); };
        versionDiscussionLink.Click += (sender, args) => { if (versionsList.SelectedItem is VersionItem item) OpenUrl(item.Release.DiscussionUrl == "" ? item.Release.Url : item.Release.DiscussionUrl); };
        App.Updater.Changed += ShowUpdateStatus;
        Closed += (sender, args) => App.Updater.Changed -= ShowUpdateStatus;
        ShowUpdateStatus();
        _ = LoadVersionsAsync();
        if (Settings.General.AutoUpdate) _ = App.Updater.CheckNowAsync();
    }

    record VersionItem(ReleaseEntry Release, bool IsCurrent)
    {
        public string Label => $"{Release.Version}{(Release.IsDev ? "  dev" : "")}  ·  {Release.Published:d MMM yyyy HH:mm}";
        public Visibility CurrentVisibility => IsCurrent ? Visibility.Visible : Visibility.Collapsed;
        public override string ToString() => Label + (IsCurrent ? "  ·  running now" : "");
    }

    async Task LoadVersionsAsync()
    {
        try
        {
            var releases = await App.Updater.ListReleasesAsync(Settings.General.UpdateChannel == "dev");
            versionsList.ItemsSource = releases.Select(release => new VersionItem(release, release.Version == App.Updater.CurrentVersion)).ToList();
            if (releases.Count == 0) versionNotesText.Text = "No versions published yet.";
        }
        catch (Exception listError) { versionNotesText.Text = $"Could not load versions: {listError.Message}"; }
    }

    void ShowSelectedVersion()
    {
        var selected = versionsList.SelectedItem as VersionItem;
        switchVersionButton.IsEnabled = selected != null && !selected.IsCurrent && App.Updater.IsInstalled;
        versionNotesText.Text = selected == null ? "" : (selected.Release.Notes.Length > 600 ? selected.Release.Notes[..600] + "…" : selected.Release.Notes);
    }

    void ShowUpdateStatus()
    {
        Dispatcher.BeginInvoke(() =>
        {
            updateStatusText.Text = App.Updater.StatusText;
            restartToUpdateButton.Visibility = App.Updater.ReadyVersion == "" ? Visibility.Collapsed : Visibility.Visible;
            restartToUpdateButton.Content = $"Restart to update to {App.Updater.ReadyVersion}";
        });
    }

    // The reference tables: tokens, example rules and shortcuts
    static List<KeyValuePair<string, string>> Rows(params (string key, string value)[] rows) => rows.Select(row => new KeyValuePair<string, string>(row.key, row.value)).ToList();
    void FillTables()
    {
        tokensTable.ItemsSource = Rows(("{artist}", "The track's artist"), ("{title}", "The track's title"), ("{album}", "The album"), ("{albumartist}", "The album artist, or the track's artist when there is none"), ("{track}", "Track number; {track:00} pads it to two digits"), ("{tracks}", "How many tracks the album has"),
            ("{genre}", "Genre"), ("{year}", "Year of the album, else of the recording"), ("{date}", "The recording's date, 2026-11-07"), ("{session}", "The session's name"), ("{source}", "The device it was recorded from"));
        rulesExampleTable.ItemsSource = Rows(("YouTube", "Artist - Title - YouTube  →  artist, title"), ("Bandcamp", "Title | Artist  →  title, artist"), ("Lyrion web player", "Title • Artist • Album  →  title, artist, album"));
        recordingKeysTable.ItemsSource = Rows(("Ctrl+R", "Start or stop recording, from anywhere in the app"), ("M", "Mark a new track here, while recording"));
        waveformKeysTable.ItemsSource = Rows(("Wheel", "Zoom at the pointer (and reset the height)"), ("Ctrl+wheel", "Make the waveform taller or shorter"), ("Shift+wheel", "Scroll along"), ("+ and -", "Zoom in and out"), ("Ctrl+0", "The whole session"), ("Z", "Zoom to the current track"),
            ("Home / End", "Start / end of the session"), ("Click", "Place the playhead"), ("Double-click a track", "Zoom to it"), ("Drag the overview", "Scroll"));
        editingKeysTable.ItemsSource = Rows(("S / E", "Set the current track's start / end at the playhead"), ("X", "Split the track at the playhead"), ("N", "New track at the playhead"), ("← →", "Nudge the playhead, or a grabbed edge (Shift: 10×, Ctrl: one sample)"), ("[ ]", "Previous / next track"),
            ("Del", "Remove the selected tracks (their audio is skipped on export)"), ("Ctrl+Z", "Undo"), ("Ctrl+Y, Ctrl+Shift+Z", "Redo"), ("Esc", "Let go of a grabbed edge, or clear the selection"));
        listeningKeysTable.ItemsSource = Rows(("Space", "Play / stop from the playhead"), (", and .", "Hear the current track's start / end"), ("Enter", "Hear a grabbed edge"), ("T", "Tags for the selected tracks"), ("L", "Look up tags online"), ("F", "Fades for the selected tracks"), ("Ctrl+E", "Export the selected tracks, or all"));
        appKeysTable.ItemsSource = Rows(("Ctrl+,", "Settings"), ("Ctrl+Q", "Quit"));
    }

    static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
