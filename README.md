# WinTapeRecorder

**WinTapeRecorder is a free, open-source Windows app.** Read more about it, and download it, on its website: [hazymat.github.io/WinTapeRecorder](https://hazymat.github.io/WinTapeRecorder/)

A tape recorder for Windows listening sessions. Record a whole session into one WAV file, let WinTapeRecorder mark where each track starts and ends, fine-tune the cuts on a waveform you can zoom down to single samples, audition the joins, then export every track as its own tagged file in one go. The session's WAV stays until you've checked the results and close the session.

- **Records** any input, or what any output device is playing (loopback).
- **Marks tracks** from what's playing (Windows media info, browser window titles through editable rules) and from gaps in the audio, with a gate you can tune (threshold, attack, release, pre- and post-roll).
- **Edits** on an overview and a zoomable waveform: drag starts and ends, snap to zero crossings, split, merge, fades. Undo for everything.
- **Auditions** the start, the end, or both of every track, a few seconds each.
- **Exports** FLAC, WAV, MP3 or AAC, with tags and folder/file names from templates (`{artist}`, `{title}`, `{track:00}`…).

It uses the standard Windows audio paths only, and doesn't get around DRM or protected audio.

## Download

Get the latest release from [Releases](https://github.com/hazymat/wintaperecorder/releases): `WinTapeRecorder-Setup.exe` installs per user (no admin needed), `WinTapeRecorder-Portable.exe` runs from anywhere. WinTapeRecorder updates itself (Settings › About: stable or dev builds, and any earlier version). Builds are not code-signed, so Windows SmartScreen may warn on first run (More info › Run anyway).

See [SPEC.md](SPEC.md) for the full specification.

## Licence

GPLv3. Uses [NAudio](https://github.com/naudio/NAudio) (MIT) and [TagLib#](https://github.com/mono/taglib-sharp) (LGPL).
