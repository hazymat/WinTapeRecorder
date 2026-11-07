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

public static class Exporter
{
    // Exports the tracks in order; returns how many succeeded. The album's numbering counts every track in the session
    public static int Export(Session session, SessionAudio audio, List<Track> tracks, OutputProfile profile, Action<ExportProgress> progress, CancellationToken cancel)
    {
        var allTracks = session.Tracks.OrderBy(track => track.Start).ToList();
        var root = profile.Folder.Trim() == "" ? AppPaths.DefaultOutputFolder : profile.Folder.Trim();
        var done = 0; var errors = new List<string>();
        foreach (var track in tracks.OrderBy(track => track.Start))
        {
            if (cancel.IsCancellationRequested) break;
            if (track.Skip) continue; // a damaged take recorded again later, or left out on purpose
            var number = allTracks.IndexOf(track) + 1;
            var relative = NameTemplate.Expand(profile.Template.Replace('\\', '/'), track, number, allTracks.Count, session, true);
            var parts = relative.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(part => part.Trim()).Where(part => part != "").ToArray();
            if (parts.Length == 0) parts = new[] { $"{number:00}" };
            var path = Path.Combine(new[] { root }.Concat(parts).ToArray()) + NameTemplate.Extension(profile.Format);
            progress(new ExportProgress(done, tracks.Count, Path.GetFileName(path), ""));
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                WriteAudio(audio, track, path, profile, session);
                WriteTags(path, track, number, allTracks.Count, session, profile);
                SaveCoverFile(path, track, profile);
                track.Exported = true; track.ExportedPath = path; done++;
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
        tag.Track = (uint)(track.TrackNumber > 0 ? track.TrackNumber : number); tag.TrackCount = (uint)(track.TrackCount > 0 ? track.TrackCount : count);
        tag.Year = uint.TryParse(track.Year, out var year) && year > 0 ? year : (uint)session.Created.Year;
        if (profile.EmbedCover && track.ArtPath != "" && File.Exists(track.ArtPath)) tag.Pictures = new TagLib.IPicture[] { new TagLib.Picture(track.ArtPath) { Type = TagLib.PictureType.FrontCover, Description = "Cover" } };
        tagFile.Save();
    }
}
