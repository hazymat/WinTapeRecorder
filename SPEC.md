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
- Gap detection: when the player itself stalls (a busy PC, say an export in another app), the sound drops from loud to digital silence within one buffer and comes back 0.1 to 5 s later. That is shown in Issues as the sound cutting out, and the take is marked like a dropout, so a later take of the same track replaces it. A gap within 3 s of a name change is the player moving to the next track and isn't flagged.
- Dropout detection: the audio engine has a half-second buffer and disk writes happen off the audio thread; the audio clock is checked against the wall clock, and any samples the engine still threw away are logged as dropouts (frame and length), shown on the recorder, in the session header and on the waveform's ruler.
- Start/stop recording (Ctrl+R from anywhere); mark a new track by hand while recording (M).
- Compact view while recording: the heading, device line and recording profile go away and the record panel shrinks to a small bar (stop button, clock, size, New track here). Source and Now playing are one line each; Now playing lists only what the source gives, in the order artist, album (year), title. Albums so far: a side-scrolling strip of album cards, newest on the left (the newest shows its latest track, older ones how many tracks), click one to see its tracks. Tracks marked so far are grouped by album (runs of markers with the same artist and album), with Track names beside its heading. Each album header edits that album's artist, name and year for all its tracks. Track numbers count up within each album by themselves (a retake doesn't count) and are what the exported tracks get. Every row reads artist, album, year, number, title. A track that belongs to the album above or below: drag it by its handle onto that album, or ⤒ / ⤓ on the album's edge track; then choose whether it takes that album's names or gives that album its own.
- Live editing: while recording, every marker noted so far can be moved (a typed time, or the live waveform), its artist, title and album retyped, or removed. Capture is never interrupted. The live waveform follows the latest minute, newest at the right (Catch up returns to that minute); a thin strip above shows the whole recording so far with the shown part boxed, and clicking or dragging on it looks there. Dragging it or zooming it (wheel) stops following: pan and zoom freely while capture carries on. Hovering a marker highlights it; clicking it selects it, and it then stays put on screen while dragging moves the audio under it; letting go places it, and it stays selected for another go (the wheel zooms around it). Ctrl+wheel sets the wave height there too, and it stays while zooming. Clicking it again or Esc lets go of it; Catch up (or Esc again) goes back to following. A marker's number in the list selects it zoomed in around it. In a session's waveform the same works for a grabbed start or end: click it again to let go. This is for obvious slips: careful listening happens afterwards in Check the cuts.
- Output devices are recorded through WASAPI loopback; the app plays silence to that device while recording so gaps in the audio keep their real length.
- No 4 GB limit: the session WAV is a plain WAV up to 4 GB and becomes RF64 (the standard WAV for bigger files, EBU Tech 3306) past it, so a whole night stays one session on one timeline. A 256 GB guard stops a runaway recording, carrying on in a new "… (part 2)" session.
- Beside the WAV size while recording, the size each output profile would make of it so far: FLAC from the encoder's own cost model on the latest audio, lossy from bitrate × time; the active profile inline, the rest on hover.
- A name that repeats the previous marker's title within 30 s (a player flickering between a placeholder artist and the real one) doesn't add a marker: the existing one keeps its place and takes the fuller of the two names.
- Now playing on the Record page is a small table of every field the chosen source gives: where from, title, artist, album, and for Windows media info also album artist, track number and count, genre and whether a picture came with it; anything missing reads "not provided". Windows media info has no year, so the playing album's year is looked up on MusicBrainz (once per album) and shown there, and marked tracks get it. Album, album artist and track number are carried into the marked tracks.
- Crash recovery: the WAV and its header reach the disk every 2 s, and the session's notes (markers at once; dropouts, clipping and quality every 30 s) are saved as they come. After a crash or power cut the app says a recording was cut off; opening it repairs the WAV header, rebuilds the waveform and tracks, and the session header says it was cut off.
- Players show an idle title for a moment between songs: a name with no album that lasted under 5 s is dropped when the next arrives. If that next name is the song it interrupted (a video took over "now playing" for a moment), the song carries on with no new track. A retake is a later track with the same title, artist and album (a blank album matches). A new track takes the year, genre and composer of the latest track of the same album, even with other names in between, and again when the player sends the album a moment after the title. A length under a second shows as ! and milliseconds.
- Removing one track (or marker) that sits between two others asks how its neighbours meet: the one before runs on to the next, the next one starts where the one before ends, or they meet in the middle (in Albums & cuts, also: leave the gap). Removing several at once doesn't ask.
- In the live wave, a marker's line (anywhere along it, not only the flag) drags the marker itself, as in Albums & cuts. A click on the line zooms in on that marker and selects it; then dragging anywhere else moves the audio under it. Double-click the flag to confirm the cut.
- A marker moved by hand in Record shows ↺ in its start pill, as in Albums & cuts: click it to put the marker back where it was placed.
- Summary: Source, Now playing and Albums so far sit in one Summary panel that folds away with one click (remembered). While recording, the page fills the window and only the track list scrolls, so the live wave stays in view. Each album in the track list folds away on its own (▾/▸); all start open.
- "Spectrum stops at … kHz" is clickable wherever it appears: a short explanation with a 0 to 24 kHz scale marking the cut against typical places (128 kbps, 256 kbps, lossless), and what it suggests about the source's encoding.
- Updating during a recording: Restart to update pauses the recording (after an OK), saves the session with its audio, waveform and notes, updates, and reopens the same session paused; the round button carries on in the same file. Nothing is recorded during the restart. If the device's format changed meanwhile, it says so and keeps what was recorded.
- The live wave zooms in as far as Albums & cuts, down to single samples: closer than the live peaks go, it reads the WAV being written.
- Issues belong to the take they hit (fixed when first seen), so moving or removing markers never hands one to a neighbour; an issue whose take was removed goes with it. A faulty take shows "Replaced by N" (dimmed) once the same title and artist are recorded again, and is left out of the export by itself. ✕ on a faulty take explains that and offers Leave this take out now, rather than handing its audio to a neighbour; removing the marker is still possible.
- The player's picture for an album is the one most of its tracks came with. While recording, a new album's track that still shows the album before's picture is looked at again a few seconds later, and left without a picture rather than the wrong one.
- Album artist: an album whose tracks all have one artist gets that artist as album artist wherever it's blank (when the tracks are built and when a session opens); an album with several artists is left for Use main artist or the MusicBrainz suggestion.
- File and folder names: characters Windows forbids are dropped, not swapped for lookalikes ("Title: Part" becomes "Title - Part", / \ | become -, ? * < > go, " becomes '); the tags keep the real characters. The cover file is always a JPEG (folder.jpg); a PNG cover is converted.
- Settings › About › Copy diagnostics: version, channel, Windows, what's happening now, audio devices, recording and output profiles and the end of crash.log as plain text, with the profile folder shown as %USERPROFILE%, the user name as <user> and the PC name as <pc>. Next to it, where to send it (a GitHub issue, Report a problem) and a reminder that GitHub posts are public.
- Long sessions: the Record page says how many hours fit in the free space where the recording goes (at the profile's rate, channels and bit depth), before and during recording, in red under an hour. While recording, Windows is asked not to sleep the PC for being idle (lid, low battery and Windows Update restarts can still stop it, which the page says). One WAV holds it all: past 4 GB it becomes RF64, and only past 256 GB (about 10 days at 24-bit/48 kHz) does recording carry on into a new session. The live waveform takes about 11 MB of memory per hour of stereo.
- Each track in the Record list has a ✓ next to its start, as in Albums & cuts: click it to confirm the cut (green), again to undo. Double-clicking the marker's flag in the wave does the same.
- Stepping through markers while recording: the left and right arrow keys, or Previous marker and Next marker beside "Tracks marked so far", select the previous or next marker, jump the wave to it and show its row (opening its album if folded). With nothing selected they carry on from the last marker selected; if none has been, they start at the first. Deselect (Esc) lets go. The arrows only ever select a marker: they are left alone while typing in a field, in a dialog and with a modifier held.
- Adding to a recording: Add to this recording in Albums & cuts reopens the finished session on the Record page, paused, carrying on in the same file; the round button starts it. Only what's being added is listed. Finish keeps every track, cut and tag already there and adds the new tracks after them. A retake made during the recording is numbered where the bad take was. A new track with the same title, artist and album as an existing one is its retake: it takes that track's tags and number, and the old take is left out (one click to use it anyway). Start over isn't offered, so the session can't be thrown away by accident. If the app stops while adding, opening the session finishes the job. A name change while nothing has been recorded since the last marker renames that marker instead of adding another.
- Track numbers count by album name and album artist, so a band name or guest on one track doesn't restart them; a retake takes the bad take's place wherever it was played, and the album's track count includes it. Sessions numbered before this are renumbered once when opened, if they have retakes. Use main artist also applies to a retake of the album further down the list.
- Hear starts and Hear ends light up the row being heard and keep it in view, so a run can be followed and ticked as it plays; ticking doesn't interrupt it. The select bar has Confirm (Y) for all the chosen tracks, or unconfirms them if all are confirmed.
- Tick boxes in every track list (Record, Albums & cuts, the Export report, album boxes too): Shift+click runs from the box last clicked to this one and sets them all as this box now is, ticked or unticked.
- Quiet PC check (Record page, before recording): whether Windows sounds are off (sound scheme No Sounds) and notification banners are off, read from the user's settings and checked again whenever the window comes back to the front. Windows offers apps no supported way to switch these, so Turn off… and Do not disturb… open the exact Settings page.
- Other sounds: recording an output device records everything on it, so while recording, every other app's audio on that device is watched (Windows meters each app separately). The app audible longest is the one being recorded; any other app, or Windows' system sounds, above -40 dBFS is listed in Issues with the time, track, length and level, and counted in the session header. A tip on the Record page suggests Do not disturb. Sounds from the same app as the player (another tab in the same browser) can't be told apart.
- Start now or Start on sound (beside the record button, remembered): armed, nothing counts until the first sound above about -50 dBFS, but up to 30 s before it is kept, so the first track starts at the sound and its start can still be dragged earlier (for a slow fade-in); so you can press record, go to your player and press play. The clock reads Armed until then.
- Start over… (red): while recording, after "Are you sure?", throws the recording so far away (WAV, markers and all) and stops.
- While recording, the round button pauses and resumes: paused, nothing is written and the session stays open; resuming carries on in the same session (the gap isn't recorded); the clock reads Paused. "Finish and start editing the cuts" (Ctrl+R) ends the recording and opens it in Albums & cuts.
- Problems: each track marked while recording has a Problem? button with a dialog and an optional note (shown in the app only, as ⚠ on the track afterwards): Note it and carry on; I'll play it again later (a later take with the same name replaces this one); Restart it now (marks this take bad and starts a new track at once, so you restart it in your player); Leave this take out; No problem after all.
- In the live wave, a marker's flag (the top) drags the marker itself; elsewhere a drag moves the view. The side wheel or Shift+wheel pans.
- While recording, the track list is the same as in Albums & cuts (column headings, row size, album headers with a tick box and **Album** - Artist (Year), fields with ↓, tick boxes and the bar at the bottom, press and hold to move a track into another album), without auditioning: a row's Start | Length finds that line in the wave, zoomed in. Both lists have Genre and Composer. Double-click a marker's flag in the live wave to confirm that cut (the flag turns green with ✓); it reaches Albums & cuts confirmed, along with the end of the track before it.
- Auto-stop: after the recording profile's minutes of silence (default 5; 0 = never) the recording finishes by itself. Silence means below -70 dBFS, nothing playing, not a quiet passage. After 30 s of it Issues shows a countdown with Keep recording (no auto-stop for the rest of this recording) and Stop now. On stopping, the last track ends a second after the sound did, and the session opens.

## Track detection

Generic, two sources, combined:

1. **Now-playing text.** Windows' media session info (title, artist, album that any app or browser tab reports) and browser window titles. Per-site rules, editable in Settings, turn a window title into artist and title with a regular expression (named groups `artist`, `title`, `album`). Shipped example rules: YouTube, Bandcamp, Lyrion web player. No service-specific rules or features beyond those.
2. **Gap detection.** Silence below a threshold for at least a minimum length marks a gap.

A change of now-playing text drops a marker at that moment (minus a latency offset from the profile), then snaps to the nearest gap within a search window. Without text, gaps alone split the session.

**Gap profile** (Settings › Detection): threshold (dBFS), minimum gap length, attack (sound must last this long to count as the start of a track), release (silence must last this long to count as the end), pre-roll and post-roll (audio kept before the start and after the end), snap window.

- Track ends (Settings › Recording profiles, and the Track ends menu in Check the cuts): by default each track ends where the next one starts, and only the start is trimmed to the first sound; a trimmed end used to land a second or two early because a fade's quiet tail sits under the gate. "Trim silence" trims both ends; "Keep as recorded" trims nothing. Each edge remembers where detection put it: ↺ beside a moved start or length puts it back.
## Workflow
- While a session records what the PC plays, another session can be tagged, filled and exported (export runs at low priority so recording never drops samples), but not listened to on that device: an audition would be recorded too, so the app says so instead of playing.

The session view is a step bar that says what is left and what to do next: **1 Check the cuts** (every start and end, doubtful ones first) → **2 Tags** (tracks missing artist, title, album, album artist or genre) → **3 Export** (what is still to export, then verification). Each step has its one primary action beside it.

## Editing

- Checking the cuts: each edge carries a note on how it was placed (in silence, in a gap near the name change, a hard cut with no silence, by hand, or the session's edge) and whether that is a safe bet. "Find next to confirm" (C) jumps to the next start or end without a ✓, counting from the track you're on (the one you clicked), zoomed in, and plays it (the line isn't selected for you). Every jump to an edge (a row's Start or End, , and ., Find next to confirm) zooms to what you'll hear: for a start, half the Audition length before it and the full length after (Audition 2 s: 1 s before, 2 s after); for an end, the mirror. Zoom freely from there. Y confirms that edge (or undoes it). With an edge grabbed, dragging the waveform moves the audio under the pinned edge; dragging the edge's own line moves it finely. Wave / Spectrogram sits on the wave itself (W switches); the start and end lines are drawn over the spectrogram. Audition sets how much of each start or end you hear (A steps through 2, 5, 10, 20 s; not L, which is Look up while tracks are ticked). The toolbar has no per-track hear buttons: each row has its own. Buttons show their shortcuts.
- Auto height (on by default): zoomed in, the wave grows to fill the lanes so quiet starts and fades are plain to see; Ctrl+wheel sets the height by hand and it stays through zooming; switching Auto height goes back to automatic.

- Overview waveform of the whole session (tracks shaded), and a detail waveform below.
- Zoom with the mouse wheel (at the pointer) and keyboard, down to individual samples: sample points, zero line and zero crossings shown. Shift+wheel or drag the overview to scroll.
- Each track has a start and end handle; drag to move. Optional snap to zero crossings. Between tracks: trim silence to a short roll (default), or keep as recorded; the choice sits in the Cuts step and applies when tracks are (re)detected.
- Split at the playhead, merge with the next track, delete a track (its audio is skipped on export), set start or end at the playhead.
- Fade in and fade out per track (length in ms), for tidying problem sources. No normalisation or other processing.
- Track list under the waveform: number, start, length, artist, title, album, status. Edit tags inline. Multi-select with the selection bar (as WinSqueeze): audition, export, set album or artist for all, fades, snap to gaps, merge, delete.
- Edits save automatically; undo and redo (Ctrl+Z, Ctrl+Y).

- Track list columns are narrow by default and resizable: drag any column header's right edge (remembered); double-click the header row to reset. The last column takes the space left. Start and length live in the audition pill: ▶ start time | length ▶, each half playing that edge, with ↺ inside when that edge has moved since detection (click it to put it back), and the ✓ after it. Fades show as a mark (◢ in, ◣ out), with the lengths on hover.
- A splitter between the wave and the track list shares the height (drag it; remembered; never below 200 px so the wave stays whole); the wave starts at 240 px and the list rows are compact.
- A selected marker (Record) or grabbed start or end (Albums & cuts) pulses, so it's plain it's still selected.
- Flags: above the wave each start has a green flag sticking out to the right of its line and each end a red flag to the left, so where an end meets the next start either can be grabbed by its own flag.
- A row's Start or End shows which edge you last heard (a bar under it) and which line is selected on the wave (an outline in the line's colour); hearing an edge doesn't select it. Lines are selected by hand on the wave.
- Confirming cuts: click a row's Start or End to hear it (zoomed in). The ✓ after them confirms that track's start and end together (the Start and End halves turn green); click again to undo. Y does the same for the current track. Moving an edge doesn't confirm it. Export points out unconfirmed starts and ends but can always go ahead.

## Auditioning
- Only one audition plays at a time: starting one silences the previous at once. Space plays and stops from anywhere and never also presses the button last clicked.

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
- Fill gaps & covers (step 2): compact, like the Export report, with every album's tracks always shown and bigger covers; a JPEG or PNG dropped anywhere on an album's panel becomes its cover. One card per album with your values beside the database's. "Fill blanks from MusicBrainz" fills only fields you left empty (and the cover if there's none); your values change only when you click one of theirs. Covers: the database's, the player's and your own (click to choose a file, right-click or Ctrl+V to paste); click one to use it, and only the one in use is outlined. The album's tracks with editable artist and title and the release's titles to click.
- The picture a player sends with its media info is saved with each name change and offered on the album card as the player's cover, beside the database's (it is often smaller).
- Captured names: every name the player sent while recording is kept with the session. "Revert to captured tags…" on ticked tracks puts them back to it, choosing which of artist, album and title. The open album's header has Select all to tick its tracks.
- Track numbers: a No. column between Year and Title shows each track's number (its place in the album unless you type another).
- Covers can be pasted: Ctrl+V in the Tags step (or Paste on an album card, or in the tag editor) takes a picture from the clipboard: image data as browsers copy it, a copied picture file, or a picture's web address. It goes on the selected tracks, else that album's tracks.
- Online lookup by hand: MusicBrainz (covers from the Cover Art Archive) and iTunes need no account; Discogs uses the user's own token (Settings › General). Amazon has no public catalogue API. Pick a result and choose which fields to take.
- Cover pictures are fetched or chosen from disk (JPEG or PNG), kept in the session folder, and on export embedded in the tags and/or saved beside the tracks as a picture file (output profile: on/off and its name, default folder.jpg).

- Albums & cuts (step 1, where most tagging happens): every album is an always-open, full-width panel in session order, with compact rows and its header (a tick box in line with the tracks' for all its tracks, **Album** - Artist (Year), track and confirmed counts) (artist, album, year, genre for all its tracks, lined up over the tracks' own columns; Enter applies even when the value didn't change, and each field has a ↓ beside it that puts just that field on every track; the header's artist sets every track's artist and album artist) and its tracks to hear, confirm and tag; Press and hold an album's header to pick up the whole album and drop it on the album above or below to merge them. Press and hold a track (it wiggles, then lifts) and drop it on the album above or below to join that album (it takes that album's name, album artist, year and genre and keeps its own artist; Undo puts it back), or on the gap after the album to give it an album of its own (you name it). Order never changes. Albums group by album name and album artist, so a featured artist doesn't split one.

- Tags… (T, on ticked tracks in any step) edits album artist, artist, album, year, title, genre, composer, track number and count, disc number and comment for one track or many at once.

## Export
- Before exporting, tracks whose file is already in the output folder (an earlier export that was interrupted) are pointed out: keep them (each is checked like a fresh export and marked Exported) and export the rest, or write them all again. While a session exports, its Export button becomes Cancel export (n/m); a cancelled export leaves no half-written file.
- Exports run in the background with their own copy of the session, so opening other sessions or recording meanwhile never stops them. One runs at a time: exporting another session while one runs queues it (its button says Export queued, click to unqueue; the sidebar shows exporting n/m… or export queued). Each track shows Exported as soon as it is written and checked. Once an album has a file, its header has Open folder, beside the folder name and the album's size.
- Track numbers in the files count within each album (1, 2, 3… with the album's size as the count), never the session position; a track's own number wins. A blank year stays blank.
- On export, a blank album artist becomes the album's main artist (the artist most of its tracks have).
- The Export step is one scrolling report: a line with the session's track and album count, total size and Open output folder, then each album with a tick box (its tracks) and the folder it is written to (which follows from the output profile), a compact output-profile picker (the session's unless chosen, so one album can be FLAC and another MP3 320; ends with Manage output profiles…), its size and Export album; and under it each album's folder, and every track, compactly, with the tags that will be written (number/count within the album, album artist, artist per track, title, album, year, genre, disc number, composer, comment, cover) and its file name; while exporting, the file being written is highlighted and kept in view. The disc count is worked out from the album's disc numbers, all editable at the last minute, and its export status. Sizes are estimates: FLAC at the usual ratio, MP3 and AAC from the bitrate, WAV exact.
- Track numbers on export: an album whose tracks all have unique numbers keeps them; one with any missing or repeated is numbered by position in the album.

- One Export button, in the Export step beside the output profile picker, exports what's left (or the selected tracks, or all). The cut-checking tools (hear starts and ends, snap, auto height, split, find tracks again) show only in Check the cuts.
- **Output profile**: format (FLAC default, WAV, MP3, AAC/M4A, lossy bitrate), output folder, file and folder name template with tokens (default `{albumartist} - {album} ({year})/{track:00} {title}`, e.g. `Gary Burton - Reunion (1990)/01 Autumn.flac`; profiles still on the old default move to it; an empty token leaves no empty brackets), tag defaults (used only where a track has none; normally empty).
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
- Closing a session from the list on the left asks right there, under it (Delete or Keep), not in a dialog in the middle.
- The window can't be made smaller than 1100 × 680 (or the screen, if that's smaller), so the layout never breaks. A compact mode for smaller windows is planned.
- The interface uses plain hyphens, never long dashes.
- A session's name (editable) and its details sit in the title-bar row beside the window buttons. The open session in the list on the left shows two small buttons, Files and Close…; every session there also has a ⋯ (on hover) with the same menu as right-click.
- The bar at the bottom is a permanent status bar of fixed height: selections and their actions, messages with Undo, and otherwise the last message dimmed, with the running version at the right. It never appears or disappears, so nothing above it moves.

- Normal Windows app. Close (X) quits, after warnings about an active recording or export in progress.
- Frameless by default, glass (Windows 11 acrylic) with transparency and tint, skins (follow Windows, dark, light). Taken from WinSqueeze.
- Hints around the app, dismissable, all hidden or reset from Settings › General.
- Keyboard shortcuts (Settings › Shortcuts lists them): Ctrl+R record/stop, M new track while recording, Space play/stop, S and E set start and end at the playhead, X split, N new track, +/- or the wheel zoom, Ctrl+wheel waveform height (kept while zooming), Ctrl+0 fit, Z zoom to track, [ ] previous/next track, , and . hear this track's start or end, Left/Right nudge the playhead or a grabbed edge (Shift: 10×, Ctrl: one sample), Ctrl+E export, Ctrl+Z/Y undo/redo, Del remove, Ctrl+, settings, Esc clear.

- Right-click in any text field gives Cut, Copy, Paste and Select all; elsewhere only the app's own menus.

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
