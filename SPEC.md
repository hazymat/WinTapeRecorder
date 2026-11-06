# WinTapeRecorder: Specification

Source of truth. Propose changes here rather than silently diverging.

## Overview

A Windows tape recorder for listening sessions. Record one long session to a single WAV file, let the app mark where each track starts and ends, fine-tune the cuts on a zoomable waveform, audition the joins, then export every track as its own tagged file in one go. The session WAV stays until you have checked the results and close the session.

## Recording

- One WAV file per session, in the session's own folder, with a `session.json` beside it (tracks, markers, tags, profiles used).
- Source: any input device (WASAPI capture) or any output device's loopback (what the PC is playing). Standard Windows audio APIs only. Nothing that gets around DRM or protected audio paths.
- Format: the device's own sample rate and channels; 16-bit, 24-bit (default) or 32-bit float, set by the recording profile.
- The WAV header is kept up to date while recording, so a crash leaves a playable file; on open, a damaged header is repaired from the file length.
- Input level check: live peak meters per channel with peak hold, and a clip light that latches (count of clipped samples) until clicked. No other meters.
- Start/stop recording (Ctrl+R from anywhere); mark a new track by hand while recording (M).
- Output devices are recorded through WASAPI loopback; the app plays silence to that device while recording so gaps in the audio keep their real length.
- WAV size limit (4 GB): the app warns before it and stops cleanly at it.

## Track detection

Generic, two sources, combined:

1. **Now-playing text.** Windows' media session info (title, artist, album that any app or browser tab reports) and browser window titles. Per-site rules, editable in Settings, turn a window title into artist and title with a regular expression (named groups `artist`, `title`, `album`). Shipped example rules: YouTube, Bandcamp, Lyrion web player. No service-specific rules or features beyond those.
2. **Gap detection.** Silence below a threshold for at least a minimum length marks a gap.

A change of now-playing text drops a marker at that moment (minus a latency offset from the profile), then snaps to the nearest gap within a search window. Without text, gaps alone split the session.

**Gap profile** (Settings › Detection): threshold (dBFS), minimum gap length, attack (sound must last this long to count as the start of a track), release (silence must last this long to count as the end), pre-roll and post-roll (audio kept before the start and after the end), snap window.

## Editing

- Overview waveform of the whole session (tracks shaded), and a detail waveform below.
- Zoom with the mouse wheel (at the pointer) and keyboard, down to individual samples: sample points, zero line and zero crossings shown. Shift+wheel or drag the overview to scroll.
- Each track has a start and end handle; drag to move. Optional snap to zero crossings.
- Split at the playhead, merge with the next track, delete a track (its audio is skipped on export), set start or end at the playhead.
- Fade in and fade out per track (length in ms), for tidying problem sources. No normalisation or other processing.
- Track list under the waveform: number, start, length, artist, title, album, status. Edit tags inline. Multi-select with the selection bar (as WinSqueeze): audition, export, set album or artist for all, fades, snap to gaps, merge, delete.
- Edits save automatically; undo and redo (Ctrl+Z, Ctrl+Y).

## Auditioning

- Play from anywhere (click the waveform, Space).
- Run through the starts, the ends, or both of every track (or the selected ones), each for a chosen length (2, 5, 10 or 20 s), with a short pause between. The waveform follows the playhead.
- Output device for auditioning chosen in Settings › Audio (default: Windows default).

## Export

- One click exports all (or the selected) tracks using an output profile.
- **Output profile**: format (FLAC default, WAV, MP3, AAC/M4A, lossy bitrate), output folder, file and folder name template with tokens, tag defaults.
- Tokens: `{artist}` `{title}` `{album}` `{albumartist}` `{track}` `{track:00}` `{genre}` `{year}` `{date}` `{session}` `{source}`. Characters Windows forbids are replaced.
- Tags written: title, artist, album, album artist, track number and total, genre, year, comment.
- Exported tracks are marked in the list; exporting again overwrites.
- FLAC is written by the app's own encoder (lossless, no Windows codec needed); MP3 and AAC use Windows' encoders (16-bit, 44.1/48 kHz).

## Sessions

- Sessions list in the sidebar (name, date, length, tracks, exported count).
- Rename a session. **Close session**: after a confirmation (warning if any tracks are not exported), deletes the session folder including the WAV. Exported files are never touched.

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
- Keyboard shortcuts (Settings › Shortcuts lists them): Ctrl+R record/stop, M new track while recording, Space play/stop, S and E set start and end at the playhead, X split, N new track, +/- or the wheel zoom, Ctrl+0 fit, Z zoom to track, [ ] previous/next track, A audition, Left/Right nudge the playhead or a grabbed edge (Shift: 10×, Ctrl: one sample), Ctrl+E export, Ctrl+Z/Y undo/redo, Del remove, Ctrl+, settings, Esc clear.

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
