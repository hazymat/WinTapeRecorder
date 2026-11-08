using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Media.Imaging;
namespace WinTapeRecorder.Sessions;

// One candidate from an online lookup (SPEC.md › Tags): what it would fill in, and where its cover is
public record TagMatch(string Source, string Title, string Artist, string Album, string AlbumArtist, string Year, string Genre, int TrackNumber, int TrackCount, string ArtUrl, string Detail, string ReleaseId = "");

// Online tag lookup: MusicBrainz (with Cover Art Archive covers) and iTunes need no account; Discogs needs the user's own token
public static class TagLookup
{
    static readonly HttpClient client = CreateClient();
    static HttpClient CreateClient()
    {
        var created = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        created.DefaultRequestHeaders.UserAgent.ParseAdd($"WinTapeRecorder/{typeof(App).Assembly.GetName().Version?.ToString(3)} (+https://github.com/hazymat/WinTapeRecorder)");
        return created;
    }

    static string Read(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ValueKind == JsonValueKind.Number ? value.GetRawText() : "" : "";
    static string Year(string date) => date.Length >= 4 ? date[..4] : date;

    public static async Task<List<TagMatch>> SearchAsync(string source, string artist, string title, string album, string discogsToken)
    {
        return source switch
        {
            "itunes" => await ITunesAsync(artist, title, album),
            "discogs" => await DiscogsAsync(artist, title, album, discogsToken),
            _ => await MusicBrainzAsync(artist, title, album),
        };
    }

    static string Quote(string text) => "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    static async Task<List<TagMatch>> MusicBrainzAsync(string artist, string title, string album, int releasesEach = 3, int most = 25)
    {
        var parts = new List<string>();
        if (title != "") parts.Add("recording:" + Quote(title));
        if (artist != "") parts.Add("artist:" + Quote(artist));
        if (album != "") parts.Add("release:" + Quote(album));
        if (parts.Count == 0) return new();
        using var document = JsonDocument.Parse(await client.GetStringAsync("https://musicbrainz.org/ws/2/recording/?fmt=json&limit={(releasesEach > 3 ? 25 : 15)}&query=" + Uri.EscapeDataString(string.Join(" AND ", parts))));
        var matches = new List<TagMatch>();
        foreach (var recording in document.RootElement.GetProperty("recordings").EnumerateArray())
        {
            var recordingArtist = recording.TryGetProperty("artist-credit", out var credits) ? string.Concat(credits.EnumerateArray().Select(credit => Read(credit, "name") + Read(credit, "joinphrase"))) : "";
            var genre = recording.TryGetProperty("tags", out var tags) ? tags.EnumerateArray().OrderByDescending(tag => tag.TryGetProperty("count", out var count) ? count.GetInt32() : 0).Select(tag => Read(tag, "name")).FirstOrDefault() ?? "" : "";
            if (!recording.TryGetProperty("releases", out var releases) || releases.GetArrayLength() == 0) { matches.Add(new TagMatch("MusicBrainz", Read(recording, "title"), recordingArtist, "", "", Year(Read(recording, "first-release-date")), Capitalise(genre), 0, 0, "", "")); continue; }
            foreach (var release in releases.EnumerateArray().Take(releasesEach))
            {
                var releaseArtist = release.TryGetProperty("artist-credit", out var releaseCredits) ? string.Concat(releaseCredits.EnumerateArray().Select(credit => Read(credit, "name") + Read(credit, "joinphrase"))) : recordingArtist;
                int trackNumber = 0, trackCount = 0;
                if (release.TryGetProperty("media", out var media) && media.GetArrayLength() > 0)
                {
                    var medium = media[0];
                    trackCount = medium.TryGetProperty("track-count", out var countValue) && countValue.ValueKind == JsonValueKind.Number ? countValue.GetInt32() : 0;
                    if (medium.TryGetProperty("track", out var trackList) && trackList.GetArrayLength() > 0) int.TryParse(Read(trackList[0], "number"), out trackNumber);
                }
                var kind = release.TryGetProperty("release-group", out var group) ? Read(group, "primary-type") : "";
                if (group.ValueKind == JsonValueKind.Object && group.TryGetProperty("secondary-types", out var secondary) && secondary.ValueKind == JsonValueKind.Array && secondary.GetArrayLength() > 0) kind += " (" + string.Join(", ", secondary.EnumerateArray().Select(type => type.GetString())) + ")";
                matches.Add(new TagMatch("MusicBrainz", Read(recording, "title"), recordingArtist, Read(release, "title"), releaseArtist, Year(Read(release, "date")), Capitalise(genre), trackNumber, trackCount,
                    $"https://coverartarchive.org/release/{Read(release, "id")}/front-500", string.Join(" · ", new[] { kind, Read(release, "country"), Read(release, "status") }.Where(text => text != "")), Read(release, "id")));
            }
        }
        return matches.Take(most).ToList();
    }

    static async Task<List<TagMatch>> ITunesAsync(string artist, string title, string album)
    {
        var term = string.Join(" ", new[] { artist, title, album }.Where(text => text != ""));
        if (term == "") return new();
        using var document = JsonDocument.Parse(await client.GetStringAsync("https://itunes.apple.com/search?media=music&entity=song&limit=20&term=" + Uri.EscapeDataString(term)));
        return document.RootElement.GetProperty("results").EnumerateArray().Select(result => new TagMatch("iTunes", Read(result, "trackName"), Read(result, "artistName"), Read(result, "collectionName"), Read(result, "collectionArtistName") is { Length: > 0 } collectionArtist ? collectionArtist : Read(result, "artistName"),
            Year(Read(result, "releaseDate")), Read(result, "primaryGenreName"), int.TryParse(Read(result, "trackNumber"), out var number) ? number : 0, int.TryParse(Read(result, "trackCount"), out var count) ? count : 0,
            Read(result, "artworkUrl100").Replace("100x100bb", "600x600bb"), Read(result, "country"))).ToList();
    }

    static async Task<List<TagMatch>> DiscogsAsync(string artist, string title, string album, string token)
    {
        if (token.Trim() == "") throw new InvalidOperationException("Discogs needs your own free token: discogs.com › Settings › Developers › Generate new token, then paste it in WinTapeRecorder's Settings › General.");
        var query = $"type=release&per_page=20&token={Uri.EscapeDataString(token.Trim())}" + (artist != "" ? "&artist=" + Uri.EscapeDataString(artist) : "") + (title != "" ? "&track=" + Uri.EscapeDataString(title) : "") + (album != "" ? "&release_title=" + Uri.EscapeDataString(album) : "");
        using var document = JsonDocument.Parse(await client.GetStringAsync("https://api.discogs.com/database/search?" + query));
        return document.RootElement.GetProperty("results").EnumerateArray().Select(result =>
        {
            var heading = Read(result, "title"); var split = heading.IndexOf(" - ", StringComparison.Ordinal);
            var genres = result.TryGetProperty("style", out var styles) && styles.GetArrayLength() > 0 ? styles : result.TryGetProperty("genre", out var genreList) ? genreList : default;
            var genre = genres.ValueKind == JsonValueKind.Array && genres.GetArrayLength() > 0 ? genres[0].GetString() ?? "" : "";
            var formats = result.TryGetProperty("format", out var formatList) ? string.Join(", ", formatList.EnumerateArray().Select(format => format.GetString()).Take(3)) : "";
            return new TagMatch("Discogs", title, artist, split > 0 ? heading[(split + 3)..] : heading, split > 0 ? heading[..split] : "", Read(result, "year"), genre, 0, 0, Read(result, "cover_image"), string.Join(" · ", new[] { formats, Read(result, "country"), Read(result, "label") }.Where(text => text != "")));
        }).ToList();
    }

    // The titles on a MusicBrainz release, in order (for the album check)
    public static async Task<List<string>> ReleaseTracksAsync(string releaseId)
    {
        using var document = JsonDocument.Parse(await client.GetStringAsync($"https://musicbrainz.org/ws/2/release/{releaseId}?inc=recordings&fmt=json"));
        var titles = new List<string>();
        if (document.RootElement.TryGetProperty("media", out var media))
            foreach (var medium in media.EnumerateArray()) if (medium.TryGetProperty("tracks", out var tracks)) foreach (var track in tracks.EnumerateArray()) titles.Add(Read(track, "title"));
        return titles;
    }

    // Background suggestion for one track (SPEC.md › Tags › Suggestions): MusicBrainz first (an album release if there is one), iTunes when it has nothing
    public static async Task<TagSuggestion?> SuggestAsync(string artist, string title, string album)
    {
        TagMatch? best = null;
        try
        {
            var matches = await MusicBrainzAsync(artist, title, "");
            var albums = matches.Where(match => match.Detail.StartsWith("Album", StringComparison.OrdinalIgnoreCase) && match.Album != "").ToList();
            best = (album != "" ? matches.FirstOrDefault(match => string.Equals(match.Album, album, StringComparison.OrdinalIgnoreCase)) : null) ?? albums.FirstOrDefault(match => match.Detail.Contains("Official")) ?? albums.FirstOrDefault() ?? matches.FirstOrDefault(match => match.Album != "");
        }
        catch { }
        if (best == null) { try { best = (await ITunesAsync(artist, title, album)).FirstOrDefault(); } catch { } }
        return best == null ? null : new TagSuggestion { Source = best.Source, Title = best.Title, Artist = best.Artist, Album = best.Album, AlbumArtist = best.AlbumArtist, Year = best.Year, Genre = best.Genre, TrackNumber = best.TrackNumber, TrackCount = best.TrackCount, ArtUrl = best.ArtUrl, ReleaseId = best.ReleaseId };
    }

    // The albums in a session, in order (SPEC.md › Tags › Album match). Every named track is searched (MusicBrainz, one a second);
    // releases vote by album name so different editions count together. Walking the tracks in order, a run continues while its
    // tracks still share an album; when the next track shares none, a new album starts (several albums recorded back to back).
    // For each run the best few releases of its album are fetched, and the one whose track list holds most of the run's titles wins
    public static async Task<List<(AlbumMatch match, Dictionary<int, (string title, int number, int count)> tracks)>> MatchAlbumsAsync(IReadOnlyList<(string artist, string title)> named, Action<int, int>? progress, CancellationToken cancel)
    {
        var results = new List<(AlbumMatch, Dictionary<int, (string, int, int)>)>();
        if (named.Count == 0) return results;
        var perTrack = new List<Dictionary<string, List<TagMatch>>>();
        for (int index = 0; index < named.Count; index++)
        {
            cancel.ThrowIfCancellationRequested();
            var keys = new Dictionary<string, List<TagMatch>>();
            try
            {
                // Every release of every matching recording, so the album the track really came from is among them; "(live)" and such are left out of the search
                var searchTitle = System.Text.RegularExpressions.Regex.Replace(named[index].title, @"\s*[\(\[].*?[\)\]]", "").Trim();
                foreach (var match in (await MusicBrainzAsync(named[index].artist, searchTitle == "" ? named[index].title : searchTitle, "", 40, 400)).Where(match => match.Album != "" && match.ReleaseId != ""))
                {
                    var key = Loose(match.Album) + "|" + Loose(match.AlbumArtist);
                    if (!keys.TryGetValue(key, out var list)) keys[key] = list = new();
                    list.Add(match);
                }
            }
            catch { }
            perTrack.Add(keys);
            progress?.Invoke(index + 1, named.Count);
            await Task.Delay(1100, cancel);
        }
        // Runs: tracks with nothing found join the run they sit in
        var runs = new List<(int from, int to, HashSet<string> keys)>();
        for (int index = 0; index < named.Count; index++)
        {
            var keys = perTrack[index].Keys.ToHashSet();
            if (runs.Count > 0)
            {
                var last = runs[^1];
                if (keys.Count == 0) { runs[^1] = (last.from, index, last.keys); continue; }
                var shared = last.keys.Count == 0 ? keys : last.keys.Intersect(keys).ToHashSet();
                if (shared.Count > 0) { runs[^1] = (last.from, index, shared); continue; }
                // One track that doesn't fit, with the next one back on the run's album: a stray (a bonus track, a bad name), not a new album
                if (index + 1 < named.Count && last.keys.Overlaps(perTrack[index + 1].Keys)) { runs[^1] = (last.from, index, last.keys); continue; }
            }
            runs.Add((index, index, keys));
        }
        foreach (var run in runs)
        {
            var indexes = Enumerable.Range(run.from, run.to - run.from + 1).ToList();
            var matches = indexes.SelectMany(index => perTrack[index].Where(pair => run.keys.Contains(pair.Key)).SelectMany(pair => pair.Value)).ToList();
            if (matches.Count == 0) continue;
            // The album name the run shares; an official studio album beats a compilation when they tie
            var bestKey = run.keys.OrderByDescending(key => indexes.Count(index => perTrack[index].ContainsKey(key)))
                .ThenBy(key => matches.Count(match => Loose(match.Album) + "|" + Loose(match.AlbumArtist) == key && match.Detail.Contains("Compilation")) > 0 ? 1 : 0)
                .ThenByDescending(key => matches.Count(match => Loose(match.Album) + "|" + Loose(match.AlbumArtist) == key && match.Detail.StartsWith("Album", StringComparison.OrdinalIgnoreCase)))
                .ThenByDescending(key => matches.Count(match => Loose(match.Album) + "|" + Loose(match.AlbumArtist) == key && match.Detail.Contains("Official"))).First();
            var albumMatches = matches.Where(match => Loose(match.Album) + "|" + Loose(match.AlbumArtist) == bestKey).ToList();
            var candidates = albumMatches.GroupBy(match => match.ReleaseId).OrderByDescending(group => group.Count())
                .ThenByDescending(group => group.First().Detail.Contains("Official")).Select(group => group.First()).Take(3).ToList();
            (AlbumMatch match, List<string> titles, double score)? chosen = null;
            foreach (var candidate in candidates)
            {
                cancel.ThrowIfCancellationRequested();
                try
                {
                    var release = await ReleaseAsync(candidate.ReleaseId);
                    var matched = indexes.Count(index => FindTitle(release.titles, named[index].title) >= 0);
                    var score = matched - Math.Abs(release.titles.Count - indexes.Count) * 0.05;
                    if (release.match.Genre == "") release.match.Genre = albumMatches.Select(match => match.Genre).Where(genre => genre != "").GroupBy(genre => genre).OrderByDescending(group => group.Count()).Select(group => group.Key).FirstOrDefault() ?? "";
                    if (chosen == null || score > chosen.Value.score) chosen = (release.match, release.titles, score);
                }
                catch { }
                await Task.Delay(1100, cancel);
            }
            if (chosen is not { } pick) continue;
            pick.match.TrackTitles = pick.titles;
            var tracks = new Dictionary<int, (string, int, int)>();
            foreach (var index in indexes) { var at = FindTitle(pick.titles, named[index].title); tracks[index] = at >= 0 ? (pick.titles[at], at + 1, pick.titles.Count) : ("", 0, 0); }
            results.Add((pick.match, tracks));
        }
        if (results.Count > 0) return results;
        // Nothing on MusicBrainz: iTunes, by collection, with each track's own number from its result
        var collections = new Dictionary<string, List<(int index, TagMatch match)>>();
        foreach (var index in Enumerable.Range(0, Math.Min(named.Count, 8)))
        {
            cancel.ThrowIfCancellationRequested();
            try
            {
                foreach (var match in (await ITunesAsync(named[index].artist, named[index].title, "")).Where(match => match.Album != "").Take(5))
                {
                    var key = Loose(match.Album) + "|" + Loose(match.AlbumArtist);
                    if (!collections.TryGetValue(key, out var list)) collections[key] = list = new();
                    if (!list.Any(entry => entry.index == index)) list.Add((index, match));
                }
            }
            catch { }
        }
        var top = collections.Values.OrderByDescending(list => list.Count).FirstOrDefault();
        if (top == null) return results;
        var sample = top[0].match;
        var album = new AlbumMatch { Source = "iTunes", Album = sample.Album, AlbumArtist = sample.AlbumArtist, Year = sample.Year, Genre = sample.Genre, ArtUrl = sample.ArtUrl, Detail = sample.Detail };
        var numbers = new Dictionary<int, (string, int, int)>();
        for (int index = 0; index < named.Count; index++) { var hit = top.Where(entry => entry.index == index).Select(entry => entry.match).FirstOrDefault(); numbers[index] = hit != null ? (hit.Title, hit.TrackNumber, hit.TrackCount) : ("", 0, 0); }
        results.Add((album, numbers));
        return results;
    }

    // The year an album first came out (MusicBrainz release group), for Now playing while recording; "" when not found
    public static async Task<string> AlbumYearAsync(string artist, string album)
    {
        if (album == "") return "";
        var query = "releasegroup:" + Quote(album) + (artist != "" ? " AND artist:" + Quote(artist) : "");
        using var document = JsonDocument.Parse(await client.GetStringAsync("https://musicbrainz.org/ws/2/release-group/?fmt=json&limit=5&query=" + Uri.EscapeDataString(query)));
        if (!document.RootElement.TryGetProperty("release-groups", out var groups)) return "";
        foreach (var group in groups.EnumerateArray())
            if (Loose(Read(group, "title")) == Loose(album) && Year(Read(group, "first-release-date")) is { Length: 4 } year) return year;
        return "";
    }

    // A title's place in a release's track list: the same loosely, or one containing the other (for "(Remastered)" and such)
    public static int FindTitle(List<string> titles, string title)
    {
        var loose = Loose(title); if (loose == "") return -1;
        var exact = titles.FindIndex(candidate => Loose(candidate) == loose);
        if (exact >= 0) return exact;
        return titles.FindIndex(candidate => { var other = Loose(candidate); return other.Length >= 4 && loose.Length >= 4 && (other.Contains(loose) || loose.Contains(other)); });
    }

    // A MusicBrainz release with its track list (all discs, in order) and the original year of its release group
    static async Task<(AlbumMatch match, List<string> titles)> ReleaseAsync(string releaseId)
    {
        using var document = JsonDocument.Parse(await client.GetStringAsync($"https://musicbrainz.org/ws/2/release/{releaseId}?inc=recordings+artist-credits+release-groups+genres&fmt=json"));
        var root = document.RootElement;
        var titles = new List<string>();
        if (root.TryGetProperty("media", out var media))
            foreach (var medium in media.EnumerateArray()) if (medium.TryGetProperty("tracks", out var tracks)) foreach (var track in tracks.EnumerateArray()) titles.Add(Read(track, "title"));
        var artist = root.TryGetProperty("artist-credit", out var credits) ? string.Concat(credits.EnumerateArray().Select(credit => Read(credit, "name") + Read(credit, "joinphrase"))) : "";
        var group = root.TryGetProperty("release-group", out var groupElement) ? groupElement : default;
        var year = Year(group.ValueKind == JsonValueKind.Object ? Read(group, "first-release-date") : "");
        if (year == "") year = Year(Read(root, "date"));
        var genre = root.TryGetProperty("genres", out var genres) ? genres.EnumerateArray().OrderByDescending(entry => entry.TryGetProperty("count", out var count) ? count.GetInt32() : 0).Select(entry => Read(entry, "name")).FirstOrDefault() ?? "" : "";
        var kind = group.ValueKind == JsonValueKind.Object ? Read(group, "primary-type") : "";
        var match = new AlbumMatch { Source = "MusicBrainz", ReleaseId = releaseId, Album = Read(root, "title"), AlbumArtist = artist, Year = year, Genre = Capitalise(genre),
            ArtUrl = $"https://coverartarchive.org/release/{releaseId}/front-500", Detail = string.Join(" · ", new[] { kind, Read(root, "country"), Read(root, "status"), titles.Count > 0 ? $"{titles.Count} tracks" : "" }.Where(text => text != "")) };
        return (match, titles);
    }

    // Loose title comparison: case, punctuation and bracketed asides don't count
    public static string Loose(string title) => new string(System.Text.RegularExpressions.Regex.Replace(title.ToLowerInvariant(), @"\s*[\(\[].*?[\)\]]", "").Where(char.IsLetterOrDigit).ToArray());

    static string Capitalise(string text) => text == "" ? "" : char.ToUpperInvariant(text[0]) + text[1..];

    // A cover saved into the session folder (once per picture), returned as its path
    public static async Task<string> SaveArtAsync(string url, string sessionFolder)
    {
        var bytes = await client.GetByteArrayAsync(url);
        return SaveArtBytes(bytes, sessionFolder, "art-web-"); // the prefix lets the UI tell an online cover from your own after reopening
    }

    public static async Task<byte[]> ArtBytesAsync(string url) => await client.GetByteArrayAsync(url);

    public static string SaveArtBytes(byte[] bytes, string sessionFolder, string prefix = "art-")
    {
        var isPng = bytes.Length > 4 && bytes[0] == 0x89 && bytes[1] == 0x50;
        var name = prefix + Convert.ToHexString(SHA1.HashData(bytes))[..12].ToLowerInvariant() + (isPng ? ".png" : ".jpg");
        var path = Path.Combine(sessionFolder, name);
        if (!File.Exists(path)) File.WriteAllBytes(path, bytes);
        return path;
    }

    // A small preview for the track list, as a data URL (the page can't read files itself)
    public static string Thumbnail(string path, int size = 512) // 512 px: the large cover preview stays sharp at 200% scaling
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.DecodePixelWidth = size; image.UriSource = new Uri(path); image.EndInit(); image.Freeze();
            var encoder = new JpegBitmapEncoder { QualityLevel = 85 }; encoder.Frames.Add(BitmapFrame.Create(image));
            using var stream = new MemoryStream(); encoder.Save(stream);
            return "data:image/jpeg;base64," + Convert.ToBase64String(stream.ToArray());
        }
        catch { return ""; }
    }
}
