using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Threading;
using Velopack;
using Velopack.Sources;
using WinTapeRecorder.Settings;
namespace WinTapeRecorder.Shell;

public record ReleaseEntry(string Tag, string Version, string Title, bool IsDev, DateTime Published, string Notes, string Url, string DiscussionUrl);

// Background update check, silent download/stage, one-click restart, and switching to any published version (SPEC.md › Updates)
public sealed class AppUpdater
{
    public const string RepoUrl = "https://github.com/hazymat/wintaperecorder";
    static readonly HttpClient githubClient = CreateGithubClient();
    readonly DispatcherTimer checkTimer = new() { Interval = TimeSpan.FromMinutes(15) };
    UpdateManager? readyManager;
    UpdateInfo? readyUpdate;
    bool checking;

    public string StatusText { get; private set; } = "";
    string portableStagedPath = "", portableReadyVersion = "";
    public string ReadyVersion => readyUpdate?.TargetFullRelease.Version.ToString() ?? portableReadyVersion;
    public event Action? Changed;
    App App => App.Instance;

    static string AssemblyVersion => typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    public string CurrentVersion => AppPaths.IsPortable ? AssemblyVersion : SafeManager(App.Settings.General.UpdateChannel)?.CurrentVersion?.ToString() ?? AssemblyVersion;
    // Portable copies update themselves; installed copies through Velopack
    public bool IsInstalled => AppPaths.IsPortable || SafeManager(App.Settings.General.UpdateChannel)?.IsInstalled == true;

    public void Start()
    {
        // Dev builds land often, so the dev channel looks every 20 s. Checks read github.com's release feed, never the rate-limited API (60 calls an hour without sign-in)
        checkTimer.Tick += async (sender, args) =>
        {
            var isDev = App.Settings.General.UpdateChannel == "dev";
            checkTimer.Interval = isDev ? TimeSpan.FromSeconds(20) : TimeSpan.FromMinutes(15);
            if (App.Settings.General.AutoUpdate && !checking) await CheckNowAsync();
        };
        if (App.Settings.General.UpdateChannel == "dev") checkTimer.Interval = TimeSpan.FromSeconds(20);
        checkTimer.Start();
        if (App.Settings.General.AutoUpdate) _ = CheckNowAsync();
        CleanUpOldPackages();
    }

    // Channel feed for normal updates; a single release's assets when switching to a specific version
    static UpdateManager CreateManager(string channel, string releaseTag = "", bool allowDowngrade = true)
    {
        IUpdateSource updateSource = releaseTag == "" ? new GithubSource(RepoUrl, null, channel == "dev") : new SimpleWebSource($"{RepoUrl}/releases/download/{releaseTag}/");
        return new UpdateManager(updateSource, new UpdateOptions { ExplicitChannel = channel, AllowVersionDowngrade = releaseTag != "" && allowDowngrade });
    }

    // Newest release tag without the API: dev reads the Atom feed (newest first, pre-releases included, conditional so unchanged answers are tiny);
    // stable follows the /releases/latest redirect, which GitHub points at the newest non-pre-release
    static readonly HttpClient webClient = CreateWebClient();
    string feedEtag = "", feedTag = "";
    async Task<string> NewestTagAsync(string channel)
    {
        if (channel == "dev")
        {
            using var feedRequest = new HttpRequestMessage(HttpMethod.Get, $"{RepoUrl}/releases.atom");
            if (feedEtag != "") feedRequest.Headers.TryAddWithoutValidation("If-None-Match", feedEtag);
            using var feedResponse = await webClient.SendAsync(feedRequest);
            if (feedResponse.StatusCode == System.Net.HttpStatusCode.NotModified) return feedTag;
            feedResponse.EnsureSuccessStatusCode();
            // Highest version in the feed (a stable release can be the newest entry, but dev builds are numbered above it)
            var feedTags = System.Text.RegularExpressions.Regex.Matches(await feedResponse.Content.ReadAsStringAsync(), @"/releases/tag/([^""<>\s]+)").Select(match => Uri.UnescapeDataString(match.Groups[1].Value)).Distinct();
            feedEtag = feedResponse.Headers.ETag?.ToString() ?? ""; feedTag = feedTags.Where(tag => Version.TryParse(tag.TrimStart('v'), out _)).OrderByDescending(tag => Version.Parse(tag.TrimStart('v'))).FirstOrDefault() ?? "";
            return feedTag;
        }
        using var latestResponse = await webClient.GetAsync($"{RepoUrl}/releases/latest");
        var location = latestResponse.Headers.Location?.ToString() ?? "";
        var tagStart = location.LastIndexOf("/tag/", StringComparison.Ordinal);
        return tagStart < 0 ? "" : Uri.UnescapeDataString(location[(tagStart + 5)..]);
    }

    static HttpClient CreateWebClient()
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("WinTapeRecorder");
        return client;
    }

    public static bool IsNewer(string candidate, string current) => Version.TryParse(candidate, out var candidateVersion) && Version.TryParse(current, out var currentVersion) && candidateVersion > currentVersion;

    static UpdateManager? SafeManager(string channel) { try { return CreateManager(channel); } catch { return null; } }

    public async Task CheckNowAsync()
    {
        if (checking) return;
        checking = true;
        try
        {
            // GitHub's release feed sometimes returns a 502/504 for a moment: retry quietly before reporting a failure
            for (var attempt = 1; ; attempt++)
            {
                try { await CheckOnceAsync(); return; }
                catch (HttpRequestException networkError) when (attempt < 3) { SetStatus($"GitHub didn't answer ({networkError.StatusCode?.ToString() ?? "network"}), retrying…"); await Task.Delay(attempt * 3000); }
            }
        }
        catch (HttpRequestException) { SetStatus($"GitHub isn't answering right now. WinTapeRecorder will try again automatically (last try {DateTime.Now:HH:mm})."); }
        catch (Exception updateError) { SetStatus($"Update check failed: {updateError.Message}"); }
        finally { checking = false; }
    }

    async Task CheckOnceAsync()
    {
        {
            if (AppPaths.IsPortable) { await CheckPortableAsync(); return; }
            var updateManager = CreateManager(App.Settings.General.UpdateChannel);
            if (!updateManager.IsInstalled) { SetStatus("Updates work once WinTapeRecorder is installed with Setup.exe (this copy is running from a zip)."); return; }
            SetStatus("Checking for updates…");
            var newestTag = await NewestTagAsync(App.Settings.General.UpdateChannel);
            var installedVersion = updateManager.CurrentVersion?.ToString() ?? AssemblyVersion;
            if (newestTag == "" || !IsNewer(newestTag.TrimStart('v'), installedVersion) || newestTag.TrimStart('v') == ReadyVersion) { if (ReadyVersion == "") SetStatus($"Up to date ({installedVersion}). Checked {DateTime.Now:HH:mm:ss}."); return; }
            // That release's own Velopack feed (a plain download from github.com, not the API)
            updateManager = CreateManager(App.Settings.General.UpdateChannel, newestTag, false);
            var foundUpdate = await updateManager.CheckForUpdatesAsync();
            if (foundUpdate == null) { SetStatus($"Up to date ({installedVersion}). Checked {DateTime.Now:HH:mm:ss}."); return; }
            SetStatus($"Downloading {foundUpdate.TargetFullRelease.Version}…");
            await updateManager.DownloadUpdatesAsync(foundUpdate);
            readyManager = updateManager; readyUpdate = foundUpdate;
            SetStatus($"WinTapeRecorder {ReadyVersion} is downloaded and ready. Restart to update.");
            App.ShowTip("Update ready", $"WinTapeRecorder {ReadyVersion} is ready. Click Restart to update in the window or Settings › About.");
        }
    }

    // Relaunches into the staged version, with the window where it was
    public async Task RestartToUpdateAsync()
    {
        if (!App.CanRestart()) return; // never in the middle of a recording or export
        if (portableStagedPath != "") { await SwapPortableAndRestartAsync(portableStagedPath); return; }
        if (readyManager == null || readyUpdate == null) return;
        var restartArguments = await App.ResumeArgumentsAsync();
        App.PrepareForRestart();
        readyManager.ApplyUpdatesAndRestart(readyUpdate.TargetFullRelease, restartArguments);
    }

    public async Task SwitchToAsync(ReleaseEntry release)
    {
        try
        {
            if (!App.CanRestart()) return;
            // Already downloaded in the background: just restart into it
            if (release.Version == ReadyVersion) { await RestartToUpdateAsync(); return; }
            if (AppPaths.IsPortable) { SetStatus($"Downloading {release.Version}…"); await SwapPortableAndRestartAsync(await DownloadPortableAsync(release.Tag)); return; }
            SetStatus($"Downloading {release.Version}…");
            var switchManager = CreateManager(release.IsDev ? "dev" : "stable", release.Tag);
            var switchUpdate = await switchManager.CheckForUpdatesAsync();
            if (switchUpdate == null) { SetStatus($"{release.Version} is already running."); return; }
            await switchManager.DownloadUpdatesAsync(switchUpdate);
            var restartArguments = await App.ResumeArgumentsAsync();
            App.PrepareForRestart();
            switchManager.ApplyUpdatesAndRestart(switchUpdate.TargetFullRelease, restartArguments);
        }
        catch (Exception switchError) { SetStatus($"Could not switch to {release.Version}: {switchError.Message}"); }
    }

    public async Task<List<ReleaseEntry>> ListReleasesAsync(bool includeDev)
    {
        var releasesJson = await githubClient.GetStringAsync("https://api.github.com/repos/hazymat/wintaperecorder/releases?per_page=60");
        using var releasesDocument = JsonDocument.Parse(releasesJson);
        var releases = new List<ReleaseEntry>();
        foreach (var releaseElement in releasesDocument.RootElement.EnumerateArray())
        {
            if (releaseElement.GetProperty("draft").GetBoolean()) continue;
            var isDev = releaseElement.GetProperty("prerelease").GetBoolean();
            if (isDev && !includeDev) continue;
            var tag = releaseElement.GetProperty("tag_name").GetString() ?? "";
            releases.Add(new ReleaseEntry(tag, tag.TrimStart('v'), releaseElement.GetProperty("name").GetString() ?? tag, isDev, releaseElement.GetProperty("published_at").GetDateTime().ToLocalTime(), releaseElement.GetProperty("body").GetString() ?? "", releaseElement.GetProperty("html_url").GetString() ?? "", releaseElement.TryGetProperty("discussion_url", out var discussionUrl) ? discussionUrl.GetString() ?? "" : ""));
        }
        return releases;
    }

    // ---- Portable single-file exe: download the release's exe next to this one, swap on restart ----
    async Task CheckPortableAsync()
    {
        var newestTag = await NewestTagAsync(App.Settings.General.UpdateChannel);
        var newestVersion = newestTag.TrimStart('v');
        if (newestTag == "" || !IsNewer(newestVersion, AssemblyVersion) || newestVersion == portableReadyVersion) { if (portableReadyVersion == "") SetStatus($"Up to date ({AssemblyVersion}). Checked {DateTime.Now:HH:mm:ss}."); return; }
        SetStatus($"Downloading {newestVersion}…");
        portableStagedPath = await DownloadPortableAsync(newestTag);
        portableReadyVersion = newestVersion;
        SetStatus($"WinTapeRecorder {ReadyVersion} is downloaded and ready. Restart to update.");
        App.ShowTip("Update ready", $"WinTapeRecorder {ReadyVersion} is ready. Click Restart to update in the window or Settings › About.");
    }

    static async Task<string> DownloadPortableAsync(string releaseTag)
    {
        var stagedPath = AppPaths.ExePath + ".update";
        using var download = await githubClient.GetAsync($"{RepoUrl}/releases/download/{releaseTag}/WinTapeRecorder-Portable.exe", HttpCompletionOption.ResponseHeadersRead);
        download.EnsureSuccessStatusCode();
        await using (var stagedFile = File.Create(stagedPath)) await download.Content.CopyToAsync(stagedFile);
        return stagedPath;
    }

    // Windows lets a running exe be renamed, so: current -> .old, staged -> current, start it, exit
    async Task SwapPortableAndRestartAsync(string stagedPath)
    {
        var restartArguments = await App.ResumeArgumentsAsync();
        var oldPath = AppPaths.ExePath + ".old";
        if (File.Exists(oldPath)) File.Delete(oldPath);
        File.Move(AppPaths.ExePath, oldPath);
        File.Move(stagedPath, AppPaths.ExePath);
        App.PrepareForRestart();
        var restartInfo = new ProcessStartInfo(AppPaths.ExePath) { UseShellExecute = false, WorkingDirectory = AppPaths.ExeFolder };
        foreach (var argument in restartArguments) restartInfo.ArgumentList.Add(argument);
        Process.Start(restartInfo);
        App.Shutdown();
    }

    // Velopack keeps downloaded packages next to the app; keep only the newest so switching versions doesn't pile them up
    static void CleanUpOldPackages()
    {
        try
        {
            var packagesFolder = Path.Combine(Path.GetDirectoryName(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)) ?? "", "packages");
            if (AppPaths.IsPortable || !Directory.Exists(packagesFolder)) return;
            foreach (var oldPackage in new DirectoryInfo(packagesFolder).GetFiles("*.nupkg").OrderByDescending(file => file.LastWriteTimeUtc).Skip(1)) oldPackage.Delete();
        }
        catch { }
    }

    public static void CleanUpAfterPortableUpdate() { try { File.Delete(AppPaths.ExePath + ".old"); File.Delete(AppPaths.ExePath + ".update"); } catch { } }

    void SetStatus(string newStatus) { StatusText = newStatus; Changed?.Invoke(); }

    static HttpClient CreateGithubClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("WinTapeRecorder");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }
}
