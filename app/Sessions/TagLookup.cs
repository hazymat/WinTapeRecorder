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

    static async Task<List<TagMatch>> MusicBrainzAsync(string artist, string title, string album)
    {
        var parts = new List<string>();
        if (title != "") parts.Add("recording:" + Quote(title));
        if (artist != "") parts.Add("artist:" + Quote(artist));
        if (album != "") parts.Add("release:" + Quote(album));
        if (parts.Count == 0) return new();
        using var document = JsonDocument.Parse(await client.GetStringAsync("https://musicbrainz.org/ws/2/recording/?fmt=json&limit=15&query=" + Uri.EscapeDataString(string.Join(" AND ", parts))));
        var matches = new List<TagMatch>();
        foreach (var recording in document.RootElement.GetProperty("recordings").EnumerateArray())
        {
            var recordingArtist = recording.TryGetProperty("artist-credit", out var credits) ? string.Concat(credits.EnumerateArray().Select(credit => Read(credit, "name") + Read(credit, "joinphrase"))) : "";
            var genre = recording.TryGetProperty("tags", out var tags) ? tags.EnumerateArray().OrderByDescending(tag => tag.TryGetProperty("count", out var count) ? count.GetInt32() : 0).Select(tag => Read(tag, "name")).FirstOrDefault() ?? "" : "";
            if (!recording.TryGetProperty("releases", out var releases) || releases.GetArrayLength() == 0) { matches.Add(new TagMatch("MusicBrainz", Read(recording, "title"), recordingArtist, "", "", Year(Read(recording, "first-release-date")), Capitalise(genre), 0, 0, "", "")); continue; }
            foreach (var release in releases.EnumerateArray().Take(3))
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
                matches.Add(new TagMatch("MusicBrainz", Read(recording, "title"), recordingArtist, Read(release, "title"), releaseArtist, Year(Read(release, "date")), Capitalise(genre), trackNumber, trackCount,
                    $"https://coverartarchive.org/release/{Read(release, "id")}/front-500", string.Join(" · ", new[] { kind, Read(release, "country"), Read(release, "status") }.Where(text => text != "")), Read(release, "id")));
            }
        }
        return matches.Take(25).ToList();
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

    // Loose title comparison: case, punctuation and bracketed asides don't count
    public static string Loose(string title) => new string(System.Text.RegularExpressions.Regex.Replace(title.ToLowerInvariant(), @"\s*[\(\[].*?[\)\]]", "").Where(char.IsLetterOrDigit).ToArray());

    static string Capitalise(string text) => text == "" ? "" : char.ToUpperInvariant(text[0]) + text[1..];

    // A cover saved into the session folder (once per picture), returned as its path
    public static async Task<string> SaveArtAsync(string url, string sessionFolder)
    {
        var bytes = await client.GetByteArrayAsync(url);
        return SaveArtBytes(bytes, sessionFolder);
    }

    public static string SaveArtBytes(byte[] bytes, string sessionFolder)
    {
        var isPng = bytes.Length > 4 && bytes[0] == 0x89 && bytes[1] == 0x50;
        var name = "art-" + Convert.ToHexString(SHA1.HashData(bytes))[..12].ToLowerInvariant() + (isPng ? ".png" : ".jpg");
        var path = Path.Combine(sessionFolder, name);
        if (!File.Exists(path)) File.WriteAllBytes(path, bytes);
        return path;
    }

    // A small preview for the track list, as a data URL (the page can't read files itself)
    public static string Thumbnail(string path, int size = 96)
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
