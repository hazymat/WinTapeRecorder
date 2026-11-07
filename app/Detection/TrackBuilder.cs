using WinTapeRecorder.Audio;
using WinTapeRecorder.Sessions;
using WinTapeRecorder.Settings;
namespace WinTapeRecorder.Detection;

// Turns a session's name changes and its audio into tracks (SPEC.md › Track detection)
public static class TrackBuilder
{
    // Where there is sound, as [start, end) block ranges, through a gate: sound opens it after Attack, silence closes it after Release
    public static List<(long start, long end)> SoundRegions(Peaks peaks, GapSettings gaps, int sampleRate)
    {
        var threshold = (float)Math.Pow(10, gaps.ThresholdDb / 20);
        var blockMs = 1000.0 * Peaks.BlockFrames / sampleRate;
        int attackBlocks = Math.Max(1, (int)Math.Round(gaps.AttackMs / blockMs)), releaseBlocks = Math.Max(1, (int)Math.Round(gaps.ReleaseMs / blockMs));
        var regions = new List<(long, long)>();
        bool open = false; long openedAt = 0, loudRun = 0, quietRun = 0;
        for (long block = 0; block < peaks.BlockCount; block++)
        {
            var loud = peaks.Level(block) >= threshold;
            if (!open)
            {
                loudRun = loud ? loudRun + 1 : 0;
                if (loudRun >= attackBlocks) { open = true; openedAt = block - attackBlocks + 1; quietRun = 0; }
            }
            else
            {
                quietRun = loud ? 0 : quietRun + 1;
                if (quietRun >= releaseBlocks) { open = false; regions.Add((openedAt, block + 1)); loudRun = 0; }
            }
        }
        if (open) regions.Add((openedAt, peaks.BlockCount));
        return regions;
    }

    // Silent stretches at least MinGap long, as [start, end) block ranges, including any before the first sound and after the last
    public static List<(long start, long end)> Gaps(List<(long start, long end)> sound, long blockCount, GapSettings gaps, int sampleRate)
    {
        var minimumBlocks = (long)Math.Ceiling(gaps.MinGapMs / (1000.0 * Peaks.BlockFrames / sampleRate));
        var result = new List<(long, long)>();
        long previousEnd = 0;
        foreach (var (start, end) in sound) { if (start - previousEnd >= minimumBlocks) result.Add((previousEnd, start)); previousEnd = end; }
        if (blockCount - previousEnd >= minimumBlocks) result.Add((previousEnd, blockCount));
        return result;
    }

    public static List<Track> Build(Session session, Peaks peaks, RecordingProfile profile)
    {
        var gapSettings = profile.Gaps; var rate = session.SampleRate;
        var sound = SoundRegions(peaks, gapSettings, rate);
        var gaps = Gaps(sound, peaks.BlockCount, gapSettings, rate);
        var totalFrames = session.TotalFrames;
        long ToFrame(long block) => Math.Min(totalFrames, block * Peaks.BlockFrames);
        var snapFrames = (long)gapSettings.SnapWindowMs * rate / 1000;

        // Boundaries with the name that starts there, and how sure the placement is
        var boundaries = new List<(long frame, NameChange? name, string how)>();
        var changes = session.Changes.OrderBy(change => change.Frame).ToList();
        if (changes.Count > 0)
        {
            if (changes[0].Frame > rate) boundaries.Add((0, null, "session")); // started recording before the first name
            foreach (var change in changes)
            {
                if (change.Manual) { boundaries.Add((change.Frame, change, "marker")); continue; }
                if (change.Frame < rate / 2) { boundaries.Add((0, change, "session")); continue; } // the name that was already playing when recording started
                var (snapped, inGap) = SnapToGap(change.Frame, gaps, peaks, snapFrames, rate, totalFrames);
                boundaries.Add((snapped, change, inGap ? "gap" : "hard"));
            }
        }
        else
        {
            boundaries.Add((0, null, "session"));
            if (profile.SplitOnGaps) foreach (var (start, end) in gaps) if (start > 0 && end < peaks.BlockCount) boundaries.Add((ToFrame((start + end) / 2), null, "gap"));
        }

        var tracks = new List<Track>();
        for (var index = 0; index < boundaries.Count; index++)
        {
            var segmentStart = boundaries[index].frame;
            var segmentEnd = index + 1 < boundaries.Count ? boundaries[index + 1].frame : totalFrames;
            if (segmentEnd <= segmentStart) continue;
            var (start, end) = gapSettings.TrimSilence ? Trim(segmentStart, segmentEnd, sound, gapSettings, rate) : (segmentStart, segmentEnd);
            var name = boundaries[index].name;
            var named = name != null && name.Title != "";
            var minimumSeconds = named || name != null ? 3 : Math.Max(1, gapSettings.MinTrackSeconds); // a name change or marker means a track was meant to start here
            if (end - start < (long)minimumSeconds * rate) continue;
            var track = new Track { Start = start, End = end, Artist = name?.Artist ?? "", Title = name?.Title ?? "", Album = name?.Album ?? "", AlbumArtist = name?.AlbumArtist ?? "", TrackNumber = name?.TrackNumber ?? 0, PlayerArtPath = name?.ArtPath ?? "", Year = name?.Year ?? "" };
            // What the user needs to know about each edge: an edge inside silence is a safe bet; a hard cut or a hand-placed marker wants a listen
            var startHow = boundaries[index].how; var endHow = index + 1 < boundaries.Count ? boundaries[index + 1].how : "session";
            (track.StartNote, track.StartSure) = start > segmentStart ? ("In the silence before the first sound", true) : startHow switch
            {
                "session" => ("Start of the recording", true),
                "gap" => ("In a gap near the name change", true),
                "marker" => ("Where you marked it by hand", false),
                _ => ("No silence near the name change: a hard cut (crossfade or gapless?)", false),
            };
            (track.EndNote, track.EndSure) = end < segmentEnd ? ("In the silence after the last sound", true) : endHow switch
            {
                "session" => ("End of the recording", true),
                "gap" => ("In a gap before the next name change", true),
                "marker" => ("Where you marked the next track", false),
                _ => ("No silence here: a hard cut where the next track starts (crossfade or gapless?)", false),
            };
            // A problem noted while recording (SPEC.md › Recording › Problems): "note" is only a note; "skip" leaves the take out;
            // the others (dropout, clipping, retake) leave it out when the same track was recorded again later
            track.Note = name?.Note ?? "";
            if (name != null) { if (name.Genre != "") track.Genre = name.Genre; if (name.Composer != "") track.Composer = name.Composer; if (name.Confirmed) track.StartChecked = true; }
            var problem = name?.Problem ?? "";
            if (problem == "skip") { track.Skip = true; track.SkipReason = "Left out while recording"; }
            else track.SkipReason = problem == "note" ? "" : problem;
            tracks.Add(track);
        }
        // Ends meet starts (SPEC.md › Track detection): a fade's quiet tail sits under the gate, so a trimmed end lands early; instead each
        // track runs right up to where the next one starts (whose start is trimmed to its first sound), unless something long lies between
        if (gapSettings.EndsMeetNextStart)
            for (var index = 0; index + 1 < tracks.Count; index++)
            {
                var track = tracks[index]; var next = tracks[index + 1];
                if (next.Start <= track.End || next.Start - track.End > 30L * rate) continue;
                track.End = next.Start; track.EndNote = "Right up to where the next track starts"; track.EndSure = next.StartSure;
            }
        foreach (var track in tracks) { track.DetectedStart = track.Start; track.DetectedEnd = track.End; }
        // A cut confirmed while recording confirms the start there and the end of the track before it, when they meet
        for (var index = 1; index < tracks.Count; index++) if (tracks[index].StartChecked && tracks[index - 1].End == tracks[index].Start) tracks[index - 1].EndChecked = true;
        // A note made on a marker that didn't become a track's own start (two markers at one spot) still reaches the track it falls in
        foreach (var track in tracks) if (track.Note == "") track.Note = session.Changes.Where(change => change.Note != "" && change.Frame >= track.Start - rate / 2 && change.Frame < track.End).Select(change => change.Note).FirstOrDefault() ?? "";
        // Retakes (SPEC.md › Recording › Issues): a take with a problem is left out when the same title and artist were recorded again later
        for (var index = 0; index < tracks.Count; index++)
        {
            var track = tracks[index];
            if (track.SkipReason == "" || track.Title == "" || track.Skip) continue;
            var again = tracks.Skip(index + 1).Any(later => string.Equals(later.Title, track.Title, StringComparison.OrdinalIgnoreCase) && string.Equals(later.Artist, track.Artist, StringComparison.OrdinalIgnoreCase));
            track.Skip = again;
            track.SkipReason = again ? track.SkipReason switch { "dropout" => "Dropout during this take; recorded again later", "clipping" => "Clipping during this take; recorded again later", _ => "Marked for a retake; recorded again later" }
                : track.SkipReason switch { "dropout" => "A dropout happened during this take", "clipping" => "Clipping during this take", _ => "Marked for a retake, but not recorded again" };
        }
        return tracks;
    }

    // The middle of the gap nearest the name change, within the snap window; with no gap there, the quietest 50 ms nearby
    static (long frame, bool inGap) SnapToGap(long frame, List<(long start, long end)> gaps, Peaks peaks, long snapFrames, int rate, long totalFrames)
    {
        long best = -1, bestDistance = long.MaxValue;
        foreach (var (start, end) in gaps)
        {
            long gapStart = start * Peaks.BlockFrames, gapEnd = Math.Min(totalFrames, end * Peaks.BlockFrames);
            var distance = frame < gapStart ? gapStart - frame : frame > gapEnd ? frame - gapEnd : 0;
            if (distance <= snapFrames && distance < bestDistance) { bestDistance = distance; best = (gapStart + gapEnd) / 2; }
        }
        if (best >= 0) return (best, true);
        // No gap: the quietest 50 ms close by (within 1.5 s, not the whole snap window: a hard cut shouldn't wander far from the name change)
        var windowBlocks = Math.Max(1, rate / 20 / Peaks.BlockFrames); var nearFrames = Math.Min(snapFrames, rate * 3 / 2);
        long fromBlock = Math.Max(0, (frame - nearFrames) / Peaks.BlockFrames), toBlock = Math.Min(peaks.BlockCount - windowBlocks, (frame + nearFrames) / Peaks.BlockFrames);
        var quietest = frame / Peaks.BlockFrames; var quietestLevel = float.MaxValue;
        for (var block = fromBlock; block <= toBlock; block++)
        {
            var level = 0f; for (var offset = 0; offset < windowBlocks; offset++) level = Math.Max(level, peaks.Level(block + offset));
            if (level < quietestLevel - 1e-6f || (Math.Abs(level - quietestLevel) <= 1e-6f && Math.Abs(block - frame / Peaks.BlockFrames) < Math.Abs(quietest - frame / Peaks.BlockFrames))) { quietestLevel = level; quietest = block + windowBlocks / 2; }
        }
        return (Math.Clamp(quietest * Peaks.BlockFrames, 0, totalFrames), false);
    }

    // From the first sound to the last sound in the segment, plus pre- and post-roll
    static (long start, long end) Trim(long segmentStart, long segmentEnd, List<(long start, long end)> sound, GapSettings gaps, int rate)
    {
        long firstSound = -1, lastSound = -1;
        foreach (var (start, end) in sound)
        {
            long soundStart = start * Peaks.BlockFrames, soundEnd = end * Peaks.BlockFrames;
            if (soundEnd <= segmentStart || soundStart >= segmentEnd) continue;
            if (firstSound < 0) firstSound = Math.Max(soundStart, segmentStart);
            lastSound = Math.Min(soundEnd, segmentEnd);
        }
        if (firstSound < 0) return (segmentStart, segmentStart); // silence only
        return (Math.Max(segmentStart, firstSound - (long)gaps.PreRollMs * rate / 1000), Math.Min(segmentEnd, lastSound + (long)gaps.PostRollMs * rate / 1000));
    }
}
