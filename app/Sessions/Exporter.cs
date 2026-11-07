using System.IO;
using System.Text.RegularExpressions;
using NAudio.MediaFoundation;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using WinTapeRecorder.Audio;
using WinTapeRecorder.Settings;
namespace WinTapeRecorder.Sessions;

// File and folder names and tags from tokens (SPEC.md › Export)
public static class NameTemplate
{
    static readonly Regex tokenPattern = new(@"\{(\w+)(?::(0+))?\}", RegexOptions.Compiled);

    public static string Expand(string template, Track track, int trackNumber, int trackCount, Session session, bool forPath)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["artist"] = track.Artist == "" ? "Unknown artist" : track.Artist,
            ["title"] = track.Title == "" ? $"Track {trackNumber}" : track.Title,
            ["album"] = track.Album == "" ? "Unknown album" : track.Album,
            ["albumartist"] = track.AlbumArtist != "" ? track.AlbumArtist : track.Artist == "" ? "Unknown artist" : track.Artist, // the album's artist, else the track's
            ["track"] = (track.TrackNumber > 0 ? track.TrackNumber : trackNumber).ToString(),
            ["tracks"] = (track.TrackCount > 0 ? track.TrackCount : trackCount).ToString(),
            ["genre"] = track.Genre,
            ["year"] = track.Year != "" ? track.Year : session.Created.Year.ToString(),
            ["date"] = session.Created.ToString("yyyy-MM-dd"),
            ["session"] = session.Name,
            ["source"] = session.Device,
        };
        var expanded = tokenPattern.Replace(template, match =>
        {
            if (!values.TryGetValue(match.Groups[1].Value, out var value)) return match.Value;
            if (match.Groups[2].Success && int.TryParse(value, out var number)) value = number.ToString(match.Groups[2].Value);
            return forPath ? SafeName(value) : value;
        });
        return expanded.Trim();
    }

    // What Windows won't allow in a file or folder name becomes a lookalike or goes
    public static string SafeName(string name)
    {
        var safe = name.Replace(':', '꞉').Replace('/', '⁄').Replace('\\', '⧵').Replace('?', '？').Replace('*', '＊').Replace('"', '\'').Replace('<', '‹').Replace('>', '›').Replace('|', '│');
        safe = new string(safe.Where(character => character >= 32).ToArray()).Trim().TrimEnd('.');
        return safe.Length > 120 ? safe[..120].Trim() : safe;
    }

    public static string Extension(string format) => format switch { "wav" => ".wav", "mp3" => ".mp3", "m4a" => ".m4a", _ => ".flac" };
}

public record ExportProgress(int Done, int Total, string Current, string Error);

// Verification (SPEC.md › Export): each written file is opened again and must decode, be the right length and carry the right title
public static class Verifier
{
    public static string Check(string path, Track track, int sampleRate)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return "the file is missing";
            if (info.Length < 1024) return "the file is empty";
            var expected = (track.End - track.Start) / (double)sampleRate;
            using (var tagFile = TagLib.File.Create(path))
            {
                var title = tagFile.Tag.Title ?? "";
                if (track.Title != "" && !string.Equals(title, track.Title, StringComparison.Ordinal)) return $"the title tag reads “{title}”";
                var tagged = tagFile.Properties.Duration.TotalSeconds;
                if (tagged > 0 && Math.Abs(tagged - expected) > Math.Max(1.5, expected * 0.02)) return $"its length is {tagged:0.0} s, not {expected:0.0} s";
            }
            // It must decode: Windows reads the start of it
            using var reader = new NAudio.Wave.MediaFoundationReader(path);
            var buffer = new byte[reader.WaveFormat.AverageBytesPerSecond];
            if (reader.Read(buffer, 0, buffer.Length) <= 0) return "it doesn't decode";
            var decoded = reader.TotalTime.TotalSeconds;
            if (decoded > 0 && Math.Abs(decoded - expected) > Math.Max(1.5, expected * 0.02)) return $"it decodes to {decoded:0.0} s, not {expected:0.0} s";
            return "";
        }
        catch (Exception error) { return error.Message; }
    }
}

public static class Exporter
{
    // Exports the tracks in order; returns how many succeeded. The album's numbering counts every track in the session
    // Track numbers count within each album (SPEC.md › Export): runs of tracks sharing album name and album artist (tracks without an
    // album by artist), skipped takes left out. A track's own number wins; the count is the album's size unless the track has one
    static string Squash(string text) => new string(text.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
    public static Dictionary<Track, (int number, int count)> AlbumNumbers(Session session)
    {
        var result = new Dictionary<Track, (int, int)>();
        var runs = new List<List<Track>>(); string previous = "\u0000";
        foreach (var track in session.Tracks.Where(track => !track.Skip).OrderBy(track => track.Start))
        {
            var key = track.Album != "" ? "album:" + Squash(track.Album) + "|" + Squash(track.AlbumArtist) : "artist:" + Squash(track.Artist);
            if (key != previous || runs.Count == 0) runs.Add(new List<Track>()); previous = key;
            runs[^1].Add(track);
        }
        foreach (var run in runs) for (var index = 0; index < run.Count; index++) result[run[index]] = (index + 1, run.Count);
        return result;
    }

    // Where each track would be written with a profile, relative to the output folder (for the Export report)
    public static string RelativePath(Session session, Track track, OutputProfile profile, Dictionary<Track, (int number, int count)> numbers)
    {
        var (number, count) = numbers.TryGetValue(track, out var found) ? found : (1, 1);
        var relative = NameTemplate.Expand(profile.Template.Replace('\\', '/'), track, number, count, session, true);
        var parts = relative.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(part => part.Trim()).Where(part => part != "").ToArray();
        if (parts.Length == 0) parts = new[] { $"{number:00}" };
        return string.Join("\\", parts) + NameTemplate.Extension(profile.Format);
    }

    public static int Export(Session session, SessionAudio audio, List<Track> tracks, OutputProfile profile, Action<ExportProgress> progress, CancellationToken cancel)
    {
        var allTracks = session.Tracks.OrderBy(track => track.Start).ToList();
        var albumNumbers = AlbumNumbers(session);
        var root = profile.Folder.Trim() == "" ? AppPaths.DefaultOutputFolder : profile.Folder.Trim();
        var done = 0; var errors = new List<string>();
        foreach (var track in tracks.OrderBy(track => track.Start))
        {
            if (cancel.IsCancellationRequested) break;
            if (track.Skip) continue; // a damaged take recorded again later, or left out on purpose
            var (number, albumCount) = albumNumbers.TryGetValue(track, out var numbered) ? numbered : (allTracks.IndexOf(track) + 1, allTracks.Count);
            track.Verified = false; track.VerifyError = "";
            var relative = NameTemplate.Expand(profile.Template.Replace('\\', '/'), track, number, albumCount, session, true);
            var parts = relative.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(part => part.Trim()).Where(part => part != "").ToArray();
            if (parts.Length == 0) parts = new[] { $"{number:00}" };
            var path = Path.Combine(new[] { root }.Concat(parts).ToArray()) + NameTemplate.Extension(profile.Format);
            progress(new ExportProgress(done, tracks.Count, Path.GetFileName(path), ""));
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                WriteAudio(audio, track, path, profile, session);
                WriteTags(path, track, number, albumCount, session, profile);
                SaveCoverFile(path, track, profile);
                track.Exported = true; track.ExportedPath = path; done++;
                progress(new ExportProgress(done, tracks.Count, "Checking " + Path.GetFileName(path), ""));
                track.VerifyError = Verifier.Check(path, track, audio.SampleRate); track.Verified = track.VerifyError == "";
                if (!track.Verified) errors.Add($"{Path.GetFileName(path)} failed the check: {track.VerifyError}");
            }
            catch (Exception exportError) { errors.Add($"{Path.GetFileName(path)}: {exportError.Message}"); try { if (File.Exists(path)) File.Delete(path); } catch { } }
        }
        progress(new ExportProgress(done, tracks.Count, "", string.Join("\n", errors)));
        return done;
    }

    // The track's audio with its fades, as float frames, in chunks
    static IEnumerable<float[]> Chunks(SessionAudio audio, Track track)
    {
        var channels = audio.Channels; var rate = audio.SampleRate;
        long fadeInFrames = (long)track.FadeInMs * rate / 1000, fadeOutFrames = (long)track.FadeOutMs * rate / 1000;
        var buffer = new float[65536 * channels];
        for (var frame = track.Start; frame < track.End;)
        {
            var read = audio.Read(frame, (int)Math.Min(65536, track.End - frame), buffer);
            if (read <= 0) yield break;
            var chunk = new float[read * channels];
            for (var index = 0; index < read; index++)
            {
                var gain = FadeGain(frame + index, track, fadeInFrames, fadeOutFrames);
                for (var channel = 0; channel < channels; channel++) chunk[index * channels + channel] = buffer[index * channels + channel] * gain;
            }
            yield return chunk;
            frame += read;
        }
    }

    // Equal-power fades (quarter sine), as auditioning plays them
    public static float FadeGain(long frame, Track track, long fadeInFrames, long fadeOutFrames)
    {
        var gain = 1.0;
        if (fadeInFrames > 0 && frame < track.Start + fadeInFrames) gain *= Math.Sin(Math.PI / 2 * Math.Clamp((double)(frame - track.Start) / fadeInFrames, 0, 1));
        if (fadeOutFrames > 0 && frame > track.End - fadeOutFrames) gain *= Math.Sin(Math.PI / 2 * Math.Clamp((double)(track.End - frame) / fadeOutFrames, 0, 1));
        return (float)gain;
    }

    static void WriteAudio(SessionAudio audio, Track track, string path, OutputProfile profile, Session session)
    {
        var integerBits = audio.BitsPerSample == 16 ? 16 : 24; // a float session exports as 24-bit
        switch (profile.Format)
        {
            case "wav":
            {
                using var writer = new WaveFileWriter(path, new WaveFormat(audio.SampleRate, integerBits, audio.Channels));
                var bytes = Array.Empty<byte>();
                foreach (var chunk in Chunks(audio, track))
                {
                    var count = chunk.Length * integerBits / 8; if (bytes.Length < count) bytes = new byte[count];
                    SampleConvert.FromFloat(chunk, bytes.AsSpan(0, count), integerBits); writer.Write(bytes, 0, count);
                }
                break;
            }
            case "mp3": case "m4a":
            {
                // Windows' own encoders: 16-bit, 44.1 or 48 kHz, stereo at most
                var temporary = path + ".tmp.wav";
                try
                {
                    using (var writer = new WaveFileWriter(temporary, WaveFormat.CreateIeeeFloatWaveFormat(audio.SampleRate, audio.Channels)))
                        foreach (var chunk in Chunks(audio, track)) writer.WriteSamples(chunk, 0, chunk.Length);
                    using var reader = new AudioFileReader(temporary);
                    ISampleProvider source = reader;
                    if (source.WaveFormat.Channels > 2) source = new MultiplexingSampleProvider(new[] { source }, 2);
                    if (source.WaveFormat.SampleRate is not (44100 or 48000)) source = new WdlResamplingSampleProvider(source, source.WaveFormat.SampleRate % 44100 == 0 ? 44100 : 48000);
                    var pcm = new SampleToWaveProvider16(source);
                    MediaFoundationApi.Startup();
                    if (profile.Format == "mp3") MediaFoundationEncoder.EncodeToMp3(pcm, path, Math.Clamp(profile.BitrateKbps, 96, 320) * 1000);
                    else MediaFoundationEncoder.EncodeToAac(pcm, path, Math.Clamp(profile.BitrateKbps, 96, 320) * 1000);
                }
                finally { try { File.Delete(temporary); } catch { } }
                break;
            }
            default:
            {
                using var flac = new FlacWriter(path, audio.SampleRate, audio.Channels, integerBits);
                foreach (var chunk in Chunks(audio, track)) flac.Write(chunk);
                break;
            }
        }
    }

    // The cover as a picture in the track's folder (Settings › Output profiles), e.g. folder.jpg, which players and Windows show
    static void SaveCoverFile(string trackPath, Track track, OutputProfile profile)
    {
        if (!profile.SaveCoverFile || track.ArtPath == "" || !File.Exists(track.ArtPath)) return;
        var name = NameTemplate.SafeName(Path.GetFileNameWithoutExtension(profile.CoverFileName.Trim() == "" ? "folder" : profile.CoverFileName.Trim()));
        File.Copy(track.ArtPath, Path.Combine(Path.GetDirectoryName(trackPath)!, name + Path.GetExtension(track.ArtPath).ToLowerInvariant()), true);
    }

    static void WriteTags(string path, Track track, int number, int count, Session session, OutputProfile profile)
    {
        using var tagFile = TagLib.File.Create(path);
        var tag = tagFile.Tag;
        string Expand(string template) => NameTemplate.Expand(template, track, number, count, session, false);
        tag.Title = track.Title == "" ? $"Track {number}" : track.Title;
        if (track.Artist != "") tag.Performers = new[] { track.Artist };
        var album = track.Album != "" ? track.Album : Expand(profile.Album);
        if (album != "") tag.Album = album;
        // The track's own tags win; the output profile fills what's left
        var albumArtist = track.AlbumArtist != "" ? track.AlbumArtist : Expand(profile.AlbumArtist); if (albumArtist != "") tag.AlbumArtists = new[] { albumArtist };
        var genre = track.Genre != "" ? track.Genre : Expand(profile.Genre); if (genre != "") tag.Genres = new[] { genre };
        var comment = track.Comment != "" ? track.Comment : Expand(profile.Comment); if (comment != "") tag.Comment = comment;
        if (track.Composer != "") tag.Composers = new[] { track.Composer };
        // Disc number, with the album's disc count worked out from its tracks (SPEC.md › Export)
        if (track.DiscNumber > 0) { tag.Disc = (uint)track.DiscNumber; var discs = session.Tracks.Where(other => !other.Skip && other.Album == track.Album && other.AlbumArtist == track.AlbumArtist).Select(other => other.DiscNumber).DefaultIfEmpty(0).Max(); if (discs > 0) tag.DiscCount = (uint)discs; }
        tag.Track = (uint)(track.TrackNumber > 0 ? track.TrackNumber : number); tag.TrackCount = (uint)(track.TrackCount > 0 ? track.TrackCount : count);
        if (uint.TryParse(track.Year, out var year) && year > 0) tag.Year = year; // a blank year stays blank (the recording date isn't the album's year)
        if (profile.EmbedCover && track.ArtPath != "" && File.Exists(track.ArtPath)) tag.Pictures = new TagLib.IPicture[] { new TagLib.Picture(track.ArtPath) { Type = TagLib.PictureType.FrontCover, Description = "Cover" } };
        tagFile.Save();
    }
}
