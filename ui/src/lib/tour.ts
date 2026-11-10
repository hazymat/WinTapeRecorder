// The tour's steps (docs/TOUR.md): what it says and what it points at.
// "The app concept" holds the ideas behind the app; "Show me" walks the screen you're on; "Explore" shows the whole journey.
// "target" is a CSS selector ('@wave' is the biggest wave on screen); a step without one, or whose target isn't on screen, just talks.
export type TourContext = 'record' | 'waiting' | 'recording' | 'cuts' | 'tags' | 'export'
export type TourStep = { title: string; text: string; more?: string; target?: string; keys?: boolean }

export const contextNames: Record<TourContext, string> = {
  record: 'Getting ready to record', waiting: 'Waiting for the music', recording: 'While you record', cuts: 'Albums & cuts', tags: 'Fill gaps & covers', export: 'Export',
}

export const concepts: TourStep[] = [
  { title: 'Taking the tour', text: '', keys: true },
  { title: 'The idea', text: "Record a whole listening session into one file, as long as you like. Each time the track changes in your player, a new track is marked by itself, named and grouped by album. Afterwards you tidy up the cuts and the tags, and export each album to its own folder." },
  { title: 'Your work is protected', text: "Recording happens in real time, so losing it would cost you real time. There are quite a few protections in place.", more: "The audio reaches the disk every couple of seconds, so even a crash or a power cut keeps everything up to that moment. The PC is kept awake while recording. Files past 4 GB are no problem: it stays one file. Recording stops by itself after 5 minutes of silence (you can change that in Settings). Enter, clicking elsewhere or opening another screen never stops it, and Start over always asks first. A take you mark as a problem is kept until a better one replaces it. You can even update the app while paused: it comes back to the same recording." },
  { title: 'A quiet PC', text: "Everything the PC plays ends up in the recording. Silence anything that makes a sound: Windows sounds, notifications, mail.", more: "That includes not using the PC for general web browsing while recording. Even websites such as online shops can unexpectedly make little ding noises. Some sites also take over the Windows audio stream without playing anything, and WinTapeRecorder reads that as a new track starting. That can be fixed later, but it's easier not to do it at all." },
  { title: 'A mouse with a scroll wheel', text: "Strongly recommended. WinTapeRecorder does a decent job of placing track markers, but you'll want to check each one, and that means zooming in and out a lot. A touchpad works, but a scroll wheel is much easier.", more: "To find where one track ends and the next starts, you need to zoom in and out, repeatedly. If you've used Audacity, or professional software like Pro Tools, you may be pleasantly surprised: WinTapeRecorder lets you jump from one marker to the next, and does a lot of the zooming for you, both in time and in height. You still need to do some yourself, but once you get good at it you can do track marking with little more than the scroll wheel and the wave." },
  { title: 'Marking while recording is optional', text: "You can check markers while it records, but you don't have to finish that before moving on. It's a convenience: record a long session, and whenever you have a few spare minutes, check a few markers. That's less to do later." },
  { title: 'The green ticks', text: "Tick ✓ when you're happy with a marker. You don't need to, but it helps you remember which ones you've checked when you come back later. The ticks carry over from recording to Albums & cuts." },
  { title: 'Where does a track start?', text: "Zoom in and out until the marker sits in the gap. The wheel zooms in time; Ctrl + wheel zooms the height.", more: "Zoom in fully in height to tell very quiet music from real silence; you can zoom right down to single samples. Some albums have absolute silence between tracks, and then it's easy. Others have a quiet hiss that, fully zoomed, looks just like the music, so zoom in and out as each track needs. For live recordings with no gaps, you're the recording engineer now: put the marker where it sounds right. ↺ always puts a marker back where it was placed automatically." },
  { title: 'Problems and retakes', text: "If something spoils a take (a notification ding, say), mark it with Problem? and play the track again later. The new take replaces the bad one by itself.", more: "A retake is a later track with the same title, artist and album. It takes the bad take's place in the album and its number, and the bad take is left out. Dropouts, and moments where the sound cut out, are flagged by themselves. Already finished? Record more in Albums & cuts reopens the recording so you can add the retake." },
  { title: 'Leave it going', text: "You could leave it going overnight, and turn your speakers down. In most cases you'll come back to find everything split into tracks and grouped by album. Then it's just a case of tidying up and exporting.", more: "Turn down the speakers or amplifier themselves rather than the Windows volume: what the PC plays may be recorded at the Windows volume. Recording stops by itself after 5 minutes of silence, so it won't run on all night." },
  { title: 'Wave and spectrogram', text: "While recording you see the wave. In Albums & cuts you can switch to a spectrogram (W), which shows the music as colour by pitch. You don't need it, but it can make a gap easy to spot." },
  { title: 'The four steps', text: "Record, then Albums & cuts, Fill gaps & covers, and Export. Explore shows each one, and can open them even while you're recording." },
]

export const steps: Record<TourContext, TourStep[]> = {
  record: [
    { title: 'A quiet PC', text: "This line checks Windows sounds and notifications for you.", target: '.quiet-check' },
    { title: 'Is the music coming in?', text: "Play something in another app. The level meter should move.", target: '.meter' },
    { title: 'Now playing', text: "What's playing shows here, with the artist, album and title. That's where track names come from: each time it changes, a new track is marked.", target: '.summary-panel' },
    { title: 'Your setup', text: "The recording profile says what to record (usually what your speakers play) and at what quality. Automatic track names suit most people.", target: '.settings-row' },
    { title: 'Start now, or on sound', text: "Start on sound waits for the music and keeps only a moment before it, so there's no silence to trim. Choose it, then press play in your player from the start of the album.", target: '.start-mode' },
    { title: 'Record', text: "Stop the music, press the round button (or Ctrl+R), then play the album from the beginning. The tour carries on from there.", target: '.record-button' },
  ],
  waiting: [
    { title: 'Waiting for the music', text: "Recording is ready, and waiting for you to play audio from another app. Press play in your player, from the start of the album. The tour moves on as soon as the music arrives.", target: '.deck' },
  ],
  recording: [
    { title: "Great: it's recording", text: "The music is coming in and being recorded. Let it play. Nothing on this screen stops it by accident: not Enter, not clicking elsewhere, not opening another screen.", target: '.deck' },
    { title: 'The tracks so far', text: "Each track gets a row, grouped by album. Artist, album, year, number and title usually come in by themselves.", target: '.rec-list' },
    { title: 'Jump to a marker', text: "Click a track's start time to jump to its marker. The wave zooms in with the marker in the middle, the marker pulses, and the start time stays highlighted so you don't lose your place.", target: '.rec-list .hear-start' },
    { title: 'Move the marker', text: "With a marker selected, drag the wave under it. Don't worry that it's still recording. Let go, zoom (wheel, or Ctrl + wheel for height) and drag again until the marker sits in the gap. You can also drag a marker by its line.", target: 'canvas.live-wave' },
    { title: 'Next and previous marker', text: "← and → step from one marker to the next once the tour is paused. Esc lets go of the selected marker.", target: '.marker-nav' },
    { title: 'Confirm it', text: "Happy with it? Tick ✓.", target: '.rec-list .hear .ok' },
    { title: 'Tags for the whole album', text: "Genre and composer usually need setting by hand. Type them into the album's header and press Enter: every track in the album gets them.", target: '.rec-list .album-row' },
    { title: 'A problem with a take?', text: "Problem? notes it, or marks it to be replaced by a later take, or leaves it out.", target: '.rec-list .retake' },
    { title: 'Marking by hand', text: "If the names don't change (a live album, say), press M or New track here when a new track starts.", target: 'button.marker' },
    { title: 'Pause or finish', text: "The round button pauses and resumes in the same recording. Finish ends it and takes you to Albums & cuts.", target: '.deck' },
    { title: 'Look around meanwhile', text: "Waiting for an album to finish? Explore shows the next screens, and can open them while the recording carries on." },
  ],
  cuts: [
    { title: 'Three steps', text: "A finished recording goes through three steps: Albums & cuts, Fill gaps & covers, and Export. You can go back and forth as you like.", target: 'nav.steps' },
    { title: 'Find next to confirm', text: "C jumps to the next start or end you haven't ticked, zoomed in, and plays it. Listen, adjust if needed, tick ✓, press C again.", target: 'nav.steps .primary' },
    { title: 'The wave', text: "Drag a start or end line to move it. The wheel zooms in time, Ctrl + wheel in height. Click to put the blue cursor somewhere.", target: '@wave' },
    { title: 'Wave or spectrogram', text: "Switch between them here, or with W. Optional.", target: '.wave-mode' },
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
