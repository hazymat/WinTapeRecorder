# Tour (planned, not built yet)

Mat's notes for an in-app tour, organised. Text in quotes is Mat's own wording, to be used as the tour copy (lightly edited when it's built). Open questions are at the end. Mat will add more detail before this is built.

## 1. First run: welcome

Shown once, the first time the app starts.

> "Welcome to WinTapeRecorder. If you're anything like me, then I expect you're a little impatient to try this out, to see if it fits your needs. But when you're ready, look for the TOUR button on the left. My humble suggestion:
> 1. Start playing music from another app, then check here to see the VU meter and track info is being brought in. Yes? Stop the music, start recording in Arm mode, then play from the beginning.
> 2. Watch the magic when tracks change.
> 3. Click on the TOUR button, which you can safely do whilst recording!"

Needs: a **Tour** button in the sidebar on the left, always there.

## 2. How the tour works

- A special mode that **adapts to the view the user is in**.
- The user can sit back and step through it in their own time with the **left and right arrow keys**.
- It **highlights UI elements** with a bright border (yellow, or whatever colour stands out best against the current colour scheme).
- A **small floating window** carries the text. It moves around, the user can drag it, and it is visibly connected to the thing it highlights.
- Three standard buttons, each showing its keyboard shortcut:
  - **Previous** (←)
  - **Next** (→)
  - **Pause tour / End tour** (Esc). Wording to decide.
- Some steps only **say** something (tips, background on capturing and splitting audio). Others **show** something (highlight a control). Steps follow a "thought order" that Mat will set out, not the order of the screen.
- Main focus: teach the basics of capturing and splitting audio, and show how efficiently the app does it.
- Longer explanations sit behind an expandable **More info**.

### Two tabs

1. **Show me.** Steps through the *current* view only, with Previous and Next. While recording, the tour never navigates away from the Record screen.
2. **Explore.** Explains the other screens and how the current step fits the whole process (Record → Albums & cuts → Fill gaps & covers → Export). While recording is a good moment for this, because the user is keen and has an album's length to wait. It offers to show the other screens while recording carries on, and reassures the user nothing will be lost. To decide: open the real screens (they can be opened while recording, see below), or show screenshots in an enlarged tour window.
   Key message: marking and confirming tracks during recording is an *optional convenience*, not a job that must be finished before moving on. The user can record a long session and, when they have a few spare minutes, set markers so there's less to do later.

## 3. Content

### 3a. Before recording (the Record screen, not yet recording)

- Confirm what's being listened to: the music is coming in (VU meter) and the track info is being picked up.
- Check settings.
- Record now vs **Start on sound** (Arm).
- Walk them through starting to record: check the audio is coming in, arm, then start. Suggest they cue up an album in another app. Then carry on into the during-recording tour (3b).

### 3b. During recording: the basics (in this order)

1. **Silence other apps.**
   > "You need to silence any apps that make sounds on your computer. Windows sounds, notifications, mail, etc. This includes not using it for general web browsing."

   More info:
   > "Even websites such as online shops can unexpectedly make little ding noises (popups etc.). Also some sites attempt to take over the Windows audio stream even when they aren't playing audio, and doing so indicates to WinTapeRecorder the start of a new track. The latter can be resolved later, but it's just easier not to do it at all."

2. **A mouse with a scroll wheel (strongly recommended).**
   > "WinTapeRecorder does a really decent job at detecting track markers, but you'll need to confirm each one and adjust as needed. You *can* do this on a laptop's touchpad but using a mouse with scroll wheel is much easier."

   More info:
   > "If you've ever worked with waveforms before, you will know this: to successfully find where one track ends, and the next track starts, you need to be able to zoom in and out, repeatedly. If you've used Audacity, or even any professional software like Pro Tools, I think you're going to be pleasantly surprised by WinTapeRecorder, because it lets you quickly browse from one marker to the next, and does a lot of the zooming in and out automatically for you so you get the best chance at finding the gap between tracks: both on the X axis (time) and on the Y axis (waveform amplitude). It's really optimised for this, so you can whip through your tracks very quickly. But you still need to do some yourself!"

   Also: "once you get good at this … you can pretty much do track marking with a mousewheel and a waveform, you just need to get good at using keyboard and scrollwheel as specialist tools! It's very easy to do and we will show you here."

3. **Marking can start during recording (optional).** The point from Explore above: it's a convenience, not a requirement.

4. **Green ticks.**
   > "You'll see green ticks at various places in the UI. These are there for you to CONFIRM that you have checked the track marker. You don't need to use them, but they can help you remember which marks you've confirmed for sure, in case you want to come back to the process later."

   The ticks carry over from the Record screen to Albums & cuts.

### 3c. During recording: finding and placing a marker

- **The start and length pills** take you to a track's marker and zoom in automatically. At least the first one will exist once recording has started, so it can always be demonstrated.
- **Two ways to select a marker:** click it in the wave, or click its track's start or end pill. Clicking the pill in the list also:
  - highlights that pill, so you keep your place in a long session,
  - pulses the marker in the wave,
  - zooms the wave so the marker is in the middle, with as much wave as possible either side.
- **Then drag the wave under the marker.** Don't worry that recording is still going. Let go, then zoom in or out horizontally and vertically to get the context you need.
- **The automatic marker positions** stay visible as dotted lines, for reference.
- **The previous and next marker keys** (← and →) step through markers. Shown beside "Tracks marked so far".

### 3d. How to decide where a track starts or ends

Mat's notes (he'll add more):

- "I find it useful to zoom all the way in vertically so that you can tell the difference between very quiet points at the end / beginning of a track and genuine actual silence. We can zoom in even to the sample to see where it's technically silent as well."
- "Sometimes there is some very quiet noise between tracks that doesn't stop, and when zoomed in all the way vertically it just shows that noise as pretty much the same solid block of blue as the first sound of the next track or the last of the previous, hence being zoomed in fully works in some cases, but is completely useless in other cases. There's no middle ground, it just depends on the track."
- "Some albums have very clearly defined starts and ends, with absolute silence between tracks; in that case you can keep vertically zoomed in to the max and it's very easy. But this is not the case for all tracks, so you need to zoom in and out vertically a lot."
- **Ctrl + mouse wheel** zooms vertically. **The mouse wheel** zooms horizontally, to find your place between two tracks.
- Live concerts: "we do need to make decisions about where that marker goes ourselves, like we are the new recording engineer boss! We can always refer back to the automatic track marks, as they are shown with dotted lines."

### 3e. During recording: other things to do

- **Confirm a marker** (✓) once you're 100% sure, so you don't have to remember to check it again later.
- **Tags:** usually the artist, album, year, track number and title come in automatically, so you don't need to touch them. Genre and composer can be set per track or, as recommended, **for the whole album**: click into that album's header, type the genre and press Enter.
- **Reassurance:** pressing Enter at any point won't stop the recording. The app is built so recording is resilient. Clicking out of it, or into another view, won't accidentally stop it.
- **Other sessions can be viewed while recording** (confirmed, see below). Maybe mention this later in the tour rather than here.

### 3f. Leave it going overnight

Mat wants this highlighted in the recording part of the tour:

- "You could leave it going overnight. You don't need to worry about the 4 GB file limit, because we've accounted for that. You could leave it going and turn your speakers down overnight or something. And in most cases, you'll come back the next morning to find everything's been tagged and segmented by album. Intelligently. Then it's just a case of tidying things up and then exporting."
- "Recording automatically stops by itself after 5 minutes of silence. This can be adjusted in settings. So another reason that it's good to leave it going overnight: you don't have to worry that it's just going to carry on recording."

Facts behind this, for the wording:
- Past 4 GB the WAV becomes RF64 and stays one file. Only at 256 GB (about 10 days at 24-bit) does the recording carry on into a new session.
- The silence stop is 5 minutes below -70 dB by default, set per recording profile (Settings › Recording profiles). The silence at the end isn't part of any track.
- The Record page shows how many hours fit in the free disk space.
- The app keeps the PC from sleeping while it records. A laptop lid closing, a low battery or a Windows Update restart can still stop it, so the tour should say to set those to never for overnight sessions.
- "Turn your speakers down" should mean the speakers or amplifier themselves, not the Windows volume. Recording what the PC plays may capture at the Windows volume level (the app already warns about this), so turning Windows down could make the recording quieter. To check before the wording is final.

## 4. Confirmed facts (checked in the app)

- **Viewing other sessions while recording: yes.** Clicking another session in the list opens it while the recording carries on. Its Albums & cuts, Fill gaps & covers and Export steps all work: you can edit, tag and export. Clicking **Recording…** at the top left goes straight back to the recording. One limit: you can't listen to (audition) another session while recording what the PC plays through the same speakers, because the recording would pick it up. The app says so if you try.
- Enter in any field only applies that field. It never stops or pauses the recording.

## 5. Open questions

- Wording of the third button: Pause tour, Hide tour or End tour?
- Explore tab: real read-only screens, or screenshots in a larger tour window?
- The exact "thought order" of steps for each view (Mat to set out).
- The highlight colour per colour scheme.
- Does the welcome appear only once, or until the tour has been opened?
- Tours for Albums & cuts, Fill gaps & covers and Export (Mat to provide content).
