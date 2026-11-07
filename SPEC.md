# WinTapeRecorder: Specification

Source of truth. Propose changes here rather than silently diverging.

## Overview

A Windows tape recorder for listening sessions. Record one long session to a single WAV file, let the app mark where each track starts and ends, fine-tune the cuts on a zoomable waveform, audition the joins, then export every track as its own tagged file in one go. The session WAV stays until you have checked the results and close the session.

## Recording

- One WAV file per session, in the session's own folder, with a `session.json` beside it (tracks, markers, tags, profiles used).
- Source: any input device (WASAPI capture) or any output device's loopback (what the PC is playing). Standard Windows audio APIs only. Nothing that gets around DRM or protected audio paths.
- Format: the device's own sample rate and channels; 16-bit, 24-bit (default) or 32-bit float, set by the recording profile.
- The WAV header is kept up to date while recording, so a crash leaves a playable file; on open, a damaged header is repaired from the file length.
- Input level check: live peak meters per channel with peak hold, and a clip light that latches (count of clipped samples) until clicked. No other meters. Nothing is adjusted during recording: loopback is the player's digital signal as it is.
- Issues while recording: a dropout or a burst of clipping is shown at once with the track it hit and what to do (play that track again, turn the source down); a track with no name after 10 s is pointed out. The damaged take is marked, and when the same title and artist are recorded again later, the damaged take is left out of the export automatically (Skipped in the list, one click to use it anyway). A take can also be marked for a retake by hand.
- Dropout detection: the audio engine has a half-second buffer and disk writes happen off the audio thread; the audio clock is checked against the wall clock, and any samples the engine still threw away are logged as dropouts (frame and length), shown on the recorder, in the session header and on the waveform's ruler.
- Start/stop recording (Ctrl+R from anywhere); mark a new track by hand while recording (M).
- Live editing: while recording, every marker noted so far can be moved (nudge buttons, a typed time, or the live waveform), its artist, title and album retyped, or removed. Capture is never interrupted. The live waveform shows the whole recording with a 10 s gap at the right that fills before the view widens; clicking a marker zooms in on it with the marker pinned at the centre, and dragging the audio under it then letting go places it. This is for obvious slips: careful listening happens afterwards in Check the cuts.
- Output devices are recorded through WASAPI loopback; the app plays silence to that device while recording so gaps in the audio keep their real length.
- WAV size limit (4 GB): the app warns before it and stops cleanly at it.
- Beside the WAV size while recording, the size each output profile would make of it so far: FLAC from the encoder's own cost model on the latest audio, lossy from bitrate × time; the active profile inline, the rest on hover.

## Track detection

Generic, two sources, combined:

1. **Now-playing text.** Windows' media session info (title, artist, album that any app or browser tab reports) and browser window titles. Per-site rules, editable in Settings, turn a window title into artist and title with a regular expression (named groups `artist`, `title`, `album`). Shipped example rules: YouTube, Bandcamp, Lyrion web player. No service-specific rules or features beyond those.
2. **Gap detection.** Silence below a threshold for at least a minimum length marks a gap.

A change of now-playing text drops a marker at that moment (minus a latency offset from the profile), then snaps to the nearest gap within a search window. Without text, gaps alone split the session.

**Gap profile** (Settings › Detection): threshold (dBFS), minimum gap length, attack (sound must last this long to count as the start of a track), release (silence must last this long to count as the end), pre-roll and post-roll (audio kept before the start and after the end), snap window.

## Workflow

The session view is a step bar that says what is left and what to do next: **1 Check the cuts** (every start and end, doubtful ones first) → **2 Tags** (tracks missing artist, title, album, album artist or genre) → **3 Export** (what is still to export, then verification). Each step has its one primary action beside it.

## Editing

- Checking the cuts: each edge carries a note on how it was placed (in silence, in a gap near the name change, a hard cut with no silence, by hand, or the session's edge) and whether that is a safe bet. "Check next" jumps to the next unchecked edge (doubtful first), zoomed to a few seconds for a start and more for an end, with the edge grabbed, and plays it. Y marks it right and moves on; editing an edge also counts as checking it. With an edge grabbed, dragging the waveform moves the audio under the pinned edge; dragging the edge's own line moves it finely.
- Auto height (on by default): zoomed in, the wave grows to fill the lanes so quiet starts and fades are plain to see; Ctrl+wheel overrides it until the next zoom.

- Overview waveform of the whole session (tracks shaded), and a detail waveform below.
- Zoom with the mouse wheel (at the pointer) and keyboard, down to individual samples: sample points, zero line and zero crossings shown. Shift+wheel or drag the overview to scroll.
- Each track has a start and end handle; drag to move. Optional snap to zero crossings. Between tracks: trim silence to a short roll (default), or keep as recorded; the choice sits in the Cuts step and applies when tracks are (re)detected.
- Split at the playhead, merge with the next track, delete a track (its audio is skipped on export), set start or end at the playhead.
- Fade in and fade out per track (length in ms), for tidying problem sources. No normalisation or other processing.
- Track list under the waveform: number, start, length, artist, title, album, status. Edit tags inline. Multi-select with the selection bar (as WinSqueeze): audition, export, set album or artist for all, fades, snap to gaps, merge, delete.
- Edits save automatically; undo and redo (Ctrl+Z, Ctrl+Y).

## Auditioning

- Play from anywhere (click the waveform, Space).
- Hear just the current track's start or just its end, for working on one edge.
- Each row in the track list has small buttons to hear that track's start or end.
- Run through the starts or the ends of every track (or the selected ones), each for a chosen length (2, 5, 10 or 20 s), with a short pause between. The waveform follows the playhead. Cuts fade over a few milliseconds so jumps don't click.
- Dotted lines mark where the playing name changed while recording; hovering a line says what it is.
- Output device for auditioning chosen in Settings › Audio (default: Windows default).

## Tags

- Tick boxes on every track and a select-all box; the selection bar acts on the ticked tracks.
- Tags per track: title, artist, album, album artist, genre, year, composer, track number and count, comment, cover picture. Several tracks can be tagged at once (empty fields stay as each track has them).
- Album match: once a session is open, every named track is looked up on MusicBrainz in the background (one request a second, as its terms require; "(live)" and such left out of the search). Releases vote by album name, so editions count together. Walking the tracks in order, an album continues while its tracks share a release; a single track that doesn't fit, with the next one back on the album, stays in it as a stray; otherwise a new album starts, so several albums recorded back to back in one session are found separately and each exports to its own folder. For each album the best few releases are fetched and the one whose track list holds most of the titles wins (compilations lose ties). iTunes is used when MusicBrainz finds nothing.
- Tags step: one card per album, never per track: album, album artist, year (the original release year), genre and cover, each with Use or Keep mine (kept choices are remembered), Use all, "Not this album?" (look it up by hand for those tracks) and how many of the release's tracks are here and which are missing. Below, per track, only the title as the release spells it and its number (Use, Use for all, Keep mine); tracks not on their album's release are flagged with Look it up. Match albums again repeats the lookup.
- Covers can be pasted: Ctrl+V in the Tags step (or Paste on an album card, or in the tag editor) takes a picture from the clipboard: image data as browsers copy it, a copied picture file, or a picture's web address. It goes on the selected tracks, else that album's tracks.
- Online lookup by hand: MusicBrainz (covers from the Cover Art Archive) and iTunes need no account; Discogs uses the user's own token (Settings › General). Amazon has no public catalogue API. Pick a result and choose which fields to take.
- Cover pictures are fetched or chosen from disk (JPEG or PNG), kept in the session folder, and on export embedded in the tags and/or saved beside the tracks as a picture file (output profile: on/off and its name, default folder.jpg).

## Export

- One Export button, in the Export step beside the output profile picker, exports what's left (or the selected tracks, or all). The cut-checking tools (hear starts and ends, snap, auto height, split, find tracks again) show only in Check the cuts.
- **Output profile**: format (FLAC default, WAV, MP3, AAC/M4A, lossy bitrate), output folder, file and folder name template with tokens (default `{albumartist} - {album}/{artist} - {album} - {track:00} {title}`), tag defaults (used only where a track has none; normally empty).
- Before exporting, tracks missing artist, album, album artist or genre are pointed out, with the choice to fill them in first or export anyway.
- Tokens: `{artist}` `{title}` `{album}` `{albumartist}` (the album artist, else the track's artist) `{track}` `{track:00}` `{tracks}` `{genre}` `{year}` `{date}` `{session}` `{source}`. Characters Windows forbids are replaced.
- Tags written: title, artist, album, album artist, track number and total, genre, year, comment.
- Exported tracks are marked in the list; exporting again overwrites.
- Verification: every written file is opened again straight away: it must exist, decode (Windows reads its start), be the track's length within 2%, and carry the track's title. A file that fails shows "Failed check" with the reason and one click exports it again. The step bar says "all verified" when done.
- The export runs in the background: the next session can be recorded meanwhile; the sidebar shows "exporting 3/12…" on the old session and the end is announced wherever you are.
- FLAC is written by the app's own encoder (lossless, no Windows codec needed); MP3 and AAC use Windows' encoders (16-bit, 44.1/48 kHz).

## Sessions

- Sessions list in the sidebar (name, date, length, tracks, exported count).
- Rename a session. Right-click a session: Show session files (Explorer, WAV selected), Open, Close session. **Close session**: after a confirmation (warning if any tracks are not exported), deletes the session folder including the WAV. Exported files are never touched.

## Profiles (Settings)

- **Recording profiles**: name, device and mode (input or loopback), bit depth, sessions folder, detection sources on or off, latency offset, gap profile.
- **Output profiles**: as under Export.
- **Detection rules**: list of site rules (name, enabled, process names, title pattern), with a live test against the current window titles.

## Settings window

Same layout as WinSqueeze: native window, left sidebar of pages grouped under small headings, changes apply immediately.

- Recording: Recording profiles, Detection
- Output: Output profiles
- Look: Appearance (glass, transparency, tint, skin, accent, scale)
- App: General (hints, close behaviour notes, settings import/export), Audio (devices, refresh), Shortcuts, About

No start with Windows, no tray.

## Window and look

- Normal Windows app. Close (X) quits, after warnings about an active recording or export in progress.
- Frameless by default, glass (Windows 11 acrylic) with transparency and tint, skins (follow Windows, dark, light). Taken from WinSqueeze.
- Hints around the app, dismissable, all hidden or reset from Settings › General.
- Keyboard shortcuts (Settings › Shortcuts lists them): Ctrl+R record/stop, M new track while recording, Space play/stop, S and E set start and end at the playhead, X split, N new track, +/- or the wheel zoom (and reset the height), Ctrl+wheel waveform height, Ctrl+0 fit, Z zoom to track, [ ] previous/next track, , and . hear this track's start or end, Left/Right nudge the playhead or a grabbed edge (Shift: 10×, Ctrl: one sample), Ctrl+E export, Ctrl+Z/Y undo/redo, Del remove, Ctrl+, settings, Esc clear.

## Updates and versions

As WinSqueeze:

- Velopack, per-user install (Setup.exe) and portable exe.
- Every push to main publishes a dev pre-release; stable releases are promoted from a dev build (workflow dispatch with its version).
- Settings › About: channel (stable or dev), download automatically, check now, restart to update, list of versions with notes, switch to any version (roll back or forward), comments link.
- What's new card after an update.

## Diagnostics

Crash and hang log in the data folder; the next launch says so. Settings › About › Open logs.

## Architecture

- `/app`: C# .NET 8 WPF shell, WebView2 hosts the UI. NAudio (WASAPI capture, loopback, playback; Media Foundation encoders), TagLibSharp (tags).
- `/ui`: Svelte 5 + TypeScript + Vite, built into `/app/wwwroot`.
- Shell owns audio, files and settings; UI talks to it over the WebView2 message bridge.
- Peaks: min/max/RMS per 256-frame block, kept beside the WAV (`peaks.bin`), built while recording or on first open.

## Build phases

1. Shell, look, settings window, updater, CI, releases.
2. Recording, level check, detection, sessions.
3. Waveform editor, auditioning.
4. Export and tags, close session.
5. Polish from testing.
