// The tour's steps (docs/TOUR.md): what it says and what it points at, for each view.
// "target" is a CSS selector; a step without one (or whose target isn't on screen) just talks.
export type TourContext = 'record' | 'recording' | 'cuts' | 'tags' | 'export'
export type TourStep = { title: string; text: string; more?: string; target?: string }

export const contextNames: Record<TourContext, string> = {
  record: 'Getting ready to record', recording: 'While you record', cuts: 'Albums & cuts', tags: 'Fill gaps & covers', export: 'Export',
}

export const steps: Record<TourContext, TourStep[]> = {
  record: [
    { title: 'Welcome', text: "This tour walks you through each screen in your own time: → for the next step, ← to go back, Esc to pause. You can open it again any time from Tour on the left, even while recording." },
    { title: 'A quiet PC', text: "Everything the PC plays ends up in the recording, so silence anything that makes a sound: Windows sounds, notifications, mail. This line checks the first two for you.", more: "That includes not using the PC for general web browsing while recording. Even websites such as online shops can unexpectedly make little ding noises. Some sites also take over the Windows audio stream without playing anything, and WinTapeRecorder reads that as a new track starting. That can be fixed later, but it's easier not to do it at all.", target: '.quiet-check' },
    { title: 'Is the music coming in?', text: "Start playing something in another app. The level meter should move, and what's playing should show up here, with the artist, album and title.", target: '.meter' },
    { title: 'Now playing', text: "This is where the track names come from: the player's own information. Each time it changes, a new track is marked by itself.", target: '.summary-panel' },
    { title: 'Your setup', text: "The recording profile says what to record (usually what your speakers play) and at what quality. Track names says where names come from. Automatic is right for most people.", target: '.settings-row' },
    { title: 'Start now, or on sound', text: "Start on sound arms the recorder: it waits for the music and keeps only a moment before it, so there's no silence to trim. Pick it, then press play in your player from the start of the album.", target: '.start-mode' },
    { title: 'Record', text: "Press the round button (or Ctrl+R). Then cue up an album in your player and let it play. Once the recording is running, open the tour again and it carries on from there.", target: '.record-button' },
  ],
  recording: [
    { title: "It's recording", text: "Everything from here can be done while the recording carries on. Nothing on this screen stops it by accident: not Enter, not clicking elsewhere, not opening another screen." },
    { title: 'Your work is protected', text: "Recording happens in real time, so losing it would cost you real time. Before anything else: there are quite a few protections in place.", more: "The audio reaches the disk every couple of seconds, so even a crash or a power cut keeps everything up to that moment. The PC is kept awake while recording. Files past 4 GB are no problem: it stays one file. Recording stops by itself after 5 minutes of silence (you can change that in Settings), so it won't run on all night. Start over always asks first. A take you mark as a problem is kept until a better one replaces it. And you can even update the app while paused: it comes back to the same recording." },
    { title: 'Leave it going', text: "You could leave it going overnight, and turn your speakers or amplifier down. In most cases you'll come back to find everything split into tracks and grouped by album. Then it's just a case of tidying up and exporting.", more: "Turn down the speakers or amplifier themselves rather than the Windows volume: what the PC plays may be recorded at the Windows volume.", target: '.deck' },
    { title: 'Silence other apps', text: "Anything else the PC plays is recorded too. If another app makes a sound, it's flagged in Issues so you know which track to play again.", target: '.deck' },
    { title: 'A mouse with a scroll wheel', text: "Strongly recommended. WinTapeRecorder does a decent job of placing track markers, but you'll want to check each one, and that means zooming in and out a lot. You can use a touchpad, but a scroll wheel is much easier.", more: "To find where one track ends and the next starts, you need to zoom in and out, repeatedly. If you've used Audacity, or professional software like Pro Tools, you may be pleasantly surprised: WinTapeRecorder lets you jump from one marker to the next, and does a lot of the zooming for you, both in time and in height. You still need to do some yourself, but you can whip through tracks quickly." },
    { title: 'Marking now is optional', text: "You can check markers while recording, but you don't have to. It's a convenience: record a long session, and when you have a few spare minutes, check a few markers. That's less to do later." },
    { title: 'The tracks so far', text: "Each track gets a row, grouped by album. Artist, album, year, number and title usually come in by themselves.", target: '.rec-list' },
    { title: 'Jump to a marker', text: "Click a track's start time to jump to its marker. The wave zooms in with the marker in the middle, the marker pulses, and the start time stays highlighted so you don't lose your place.", target: '.rec-list .hear-start' },
    { title: 'Move the marker', text: "With a marker selected, drag the wave under it. Don't worry that it's still recording. Let go, zoom to look closer, and drag again until the marker sits in the gap. You can also drag a marker by its line.", more: "The wheel zooms in time; Ctrl + wheel zooms the height. Zoom in fully in height to tell very quiet music from real silence; you can zoom right down to single samples. But some albums have a quiet hiss between tracks that, fully zoomed, looks just like the music, so zoom in and out as each track needs. For live recordings with no gaps, you're the recording engineer now: put the marker where it sounds right. ↺ puts it back where it was placed automatically.", target: 'canvas.live-wave' },
    { title: 'Next and previous marker', text: "← and → step from one marker to the next (when the tour isn't open). Esc lets go of the selected marker.", target: '.marker-nav' },
    { title: 'The green ticks', text: "Happy with a marker? Tick ✓ so you know it's done. You don't need to, but it helps when you come back later. The ticks carry over to Albums & cuts.", target: '.rec-list .hear .ok' },
    { title: 'Tags for the whole album', text: "Genre and composer usually need setting by hand. Set them for the whole album: type into the album's header and press Enter. Enter never stops the recording.", target: '.rec-list .album-row' },
    { title: 'A problem with a take?', text: "If something goes wrong, a notification ding, say, click Problem? on that track. Play it again later and the new take replaces the bad one by itself.", more: "Note it and carry on: keeps the take, with your note. I'll play it again later: when the same track (same title, artist and album) is recorded again, the new take replaces this one, takes its place in the album and its number, and the bad one is left out. Restart it now: marks this take and starts a new track straight away, so go back to the start of the song in your player. Leave this take out: it won't be exported. Dropouts and moments where the sound cut out are flagged by themselves in Issues, and the track is marked the same way. Already finished? Record more in Albums & cuts reopens the recording so you can add the retake.", target: '.rec-list .retake' },
    { title: 'Marking by hand', text: "If the names don't change (a live album, say), press M or New track here at the moment a new track starts.", target: 'button.marker' },
    { title: 'Pause or finish', text: "The round button pauses and resumes in the same recording. Finish ends it and takes you to Albums & cuts.", target: '.deck' },
    { title: 'Wave now, spectrogram later', text: "Here you see the wave. In Albums & cuts you can switch to a spectrogram (W), which shows the music as colour by pitch. You don't need it, but it can make a gap easy to spot.", target: 'canvas.live-wave' },
    { title: 'Look around meanwhile', text: "Waiting for an album to finish? Open the Explore tab to see the next screens. Your recording carries on while you look." },
  ],
  cuts: [
    { title: 'Three steps', text: "A finished recording goes through three steps: Albums & cuts, Fill gaps & covers, and Export. You can go back and forth as you like.", target: 'nav.steps' },
    { title: 'Find next to confirm', text: "C jumps to the next start or end you haven't ticked, zoomed in, and plays it. Listen, adjust if needed, tick ✓, press C again.", target: 'nav.steps .primary' },
    { title: 'The wave', text: "Drag a start or end line to move it. The wheel zooms in time, Ctrl + wheel in height. Click to put the blue cursor somewhere.", target: '@wave' },
    { title: 'Wave or spectrogram', text: "Switch between them here, or with W. The spectrogram shows pitch as colour, which can show a gap the wave hides. Optional.", target: '.wave-mode' },
    { title: 'Amber, blue and green', text: "Amber start or end: no clear gap was found, so the cut is a best guess and worth a listen. Blue: it fell in a gap. Green: you've confirmed it.", target: '.hear' },
    { title: 'Tick to act on several', text: "Tick tracks to tag, merge, fade or remove several at once. Shift+click ticks a range. With one track ticked, Start at cursor and End at cursor move its edges to the blue cursor.", target: '.row.head' },
    { title: 'Record more', text: "Missed a track or need a retake? Record more reopens this recording where it left off.", target: '.record-more' },
    { title: 'Undo and history', text: "Every edit can be undone (Ctrl+Z), and History… goes back to any earlier state of the track list.", target: '.toolbar' },
  ],
  tags: [
    { title: 'What still needs something', text: "Each album says Complete or what's missing. Tick the box to see only what still needs something.", target: '.tags-summary' },
    { title: 'Covers', text: "Click a picture to use it. Each shows its size and resolution, and Compare at full size puts them side by side. You can also drop or paste your own.", target: '.cover-panel' },
    { title: 'For the whole album', text: "Fields here apply to every track. Amber means missing. Suggestions from the online match are in the right-hand column: click one to use it.", target: '.tag-sections .tag-panel' },
    { title: 'Each track', text: "Titles and artists per track, with the online title beside each. Use all takes every suggested title at once.", target: '.tag-sections .tag-panel ~ .tag-panel' },
  ],
  export: [
    { title: 'Export', text: "Choose the output profile (FLAC, MP3 and so on), then export. Each album goes into its own folder, with its cover and tags.", target: '.export-size' },
    { title: 'The report', text: "Every file and tag before it's written, and a mark once each one is done and checked.", target: '.report-card' },
    { title: 'Carry on meanwhile', text: "Exports run in the background, so you can record or edit another session while one runs." },
  ],
}

// The whole journey, for the Explore tab
export const stages: { context: TourContext; title: string; text: string }[] = [
  { context: 'record', title: '1. Record', text: "Record a whole listening session into one file. Tracks are marked by themselves as the names change, and you can check markers while it records if you like." },
  { context: 'cuts', title: '2. Albums & cuts', text: "Check where each track starts and ends, with the wave or the spectrogram, and tick each one once you're happy. Anything you ticked while recording is already done." },
  { context: 'tags', title: '3. Fill gaps & covers', text: "Fill in what's missing for each album, from the online match or by hand, and choose the cover." },
  { context: 'export', title: '4. Export', text: "Write each album to its own folder, as FLAC, MP3 or whatever your profile says, with tags and cover." },
]
