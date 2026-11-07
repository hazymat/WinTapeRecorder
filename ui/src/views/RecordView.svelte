<script lang="ts">
  import { request, sendToShell } from '../bridge'
  import { app, nav, toast, formatTime, writeSetting, readSetting } from '../state.svelte'
  import LevelMeter from '../parts/LevelMeter.svelte'
  import Dropdown from '../parts/Dropdown.svelte'
  import Dialog from '../parts/Dialog.svelte'
  import Hint from '../parts/Hint.svelte'
  import Icon from '../parts/Icon.svelte'
  // The recorder (SPEC.md › Recording): choose a profile and where track names come from, check the level, record
  let sessionName = $state('')
  let source = $state('auto')
  $effect(() => { sendToShell({ type: 'setSource', source }) }) // Now playing follows the chosen source before recording too
  const recording = $derived(!!app.status?.recording)
  const profileId = $derived(app.activeRecording)

  // Level check: the input is watched while this view is open
  $effect(() => { if (profileId) sendToShell({ type: 'monitor', on: true, profileId }); return () => { if (!app.status?.recording) sendToShell({ type: 'monitor', on: false }) } })

  const sourceChoices = $derived([['auto', 'Automatic'] as [string, string]].concat(app.sources.filter(candidate => candidate.kind === 'media' || candidate.title).map(candidate => [candidate.key, candidate.label.length > 70 ? candidate.label.slice(0, 68) + '…' : candidate.label] as [string, string])).concat([['none', 'None: gaps only']]))
  $effect(() => { if (source !== 'auto' && source !== 'none' && !app.sources.some(candidate => candidate.key === source) && !recording) source = 'auto' })
  const profileChoices = $derived(app.recordingProfiles.map(profile => [profile.id, profile.name] as [string, string]))
  const profile = $derived(app.recordingProfiles.find(candidate => candidate.id === profileId))

  export function toggleRecording() {
    if (recording) stopRecording()
    else { sendToShell({ type: 'record', profileId, source, name: sessionName.trim(), armed: startOnSound }); sessionName = '' }
  }
  async function stopRecording() {
    const result = await request<{ sessionId: string }>({ type: 'stopRecording' })
    if (result?.sessionId) { nav.view = 'session'; nav.sessionId = result.sessionId; writeSetting('session', result.sessionId); toast('Recording saved. Check the tracks, then export.') }
  }

  // The live waveform (SPEC.md › Recording › Live editing). Following: the whole recording fitted to the width, with a 10 s gap at
  // the right that fills before the view rescales. Grab it (drag) or zoom it (wheel) and it stops following: pan and zoom freely while
  // recording carries on. Hover a marker and click it to select it: it stays put on screen and dragging moves the audio under it;
  // letting go places it. Click it again or Esc to let go of it; Catch up (or Esc again) goes back to following
  let liveCanvas = $state<HTMLCanvasElement>(), liveWidth = $state(600)
  const liveHeight = 110
  let liveTimer = 0
  let following = $state(true)
  let viewStart = 0, viewSpan = 0 // the paused view, in frames
  let selected = $state(-1) // the selected marker's index
  let liveScale = 1 // Ctrl+wheel: wave height, as in the session's waveform; kept while zooming
  let hovered = $state(-1)
  let followSeconds = $state(60) // following: the latest minute, newest at the right; Catch up comes back to it
  let overviewSpan = 0 // the whole recording so far, for the strip above
  let dragging: null | { startX: number; startViewStart: number; pinX: number; moved: boolean; onMarker: number } = null
  function liveRange() {
    const status = app.status!; const rate = status.sampleRate, recorded = app.levels.seconds * rate
    overviewSpan = Math.max(recorded, rate)
    if (!following) return { start: viewStart, span: viewSpan }
    const span = followSeconds * rate
    return { start: Math.max(0, recorded - span * 0.97), span }
  }
  // Where a marker is: the selected one being dragged follows the audio under its fixed screen position
  function markerFrame(index: number) { const change = app.status?.changes[index]; if (!change) return 0; if (flagDrag && flagDrag.index === index) return flagDrag.frame; if (dragging && dragging.moved && index === selected) { const { span } = liveRange(); return Math.max(0, Math.min(app.levels.seconds * (app.status?.sampleRate ?? 48000), viewStart + dragging.pinX * span / liveWidth)) } return change.frame }
  function markerX(index: number) { const { start, span } = liveRange(); return (markerFrame(index) - start) / span * liveWidth }
  function markerAt(x: number) { const changes = app.status?.changes ?? []; let best = -1, bestDistance = 7; changes.forEach((_, index) => { const distance = Math.abs(markerX(index) - x); if (distance < bestDistance || (index === selected && distance < 9)) { best = index; bestDistance = distance } }); return best }
  function pause() { if (!following || !app.status) return; const { start, span } = liveRange(); viewStart = start; viewSpan = span; following = false }
  export function catchUp() { followSeconds = 60; following = true; selected = -1; dragging = null; drawLive() }
  function selectMarker(index: number, centre = false) {
    const status = app.status; if (!status) return
    pause()
    if (centre) { const rate = status.sampleRate; viewSpan = Math.min(viewSpan || 8 * rate, 8 * rate); viewStart = status.changes[index].frame - viewSpan / 2 }
    selected = index; drawLive()
  }
  function deselect() { selected = -1; drawLive() }
  // The whole recording as a thin strip, with the part shown below boxed; click or drag on it to look there
  let stripCanvas = $state<HTMLCanvasElement>(), stripPeaks: number[] = [], stripAsked = 0
  const stripHeight = 26
  function drawStrip(start: number, span: number, recorded: number, rate: number, colour: (name: string, fallback: string) => string) {
    const status = app.status; if (!stripCanvas || !status) return
    const ratio = devicePixelRatio, width = liveWidth, context = stripCanvas.getContext('2d')!
    if (stripCanvas.width !== Math.round(width * ratio)) { stripCanvas.width = Math.round(width * ratio); stripCanvas.height = stripHeight * ratio }
    if (Date.now() - stripAsked > 2000) { stripAsked = Date.now(); request<{ mode: string; channels: number[][] }>({ type: 'peaks', id: status.sessionId, start: 0, end: Math.max(recorded, 1), columns: Math.round(width) }).then(data => { if (data.mode === 'peaks') stripPeaks = data.channels[0] ?? [] }).catch(() => { }) }
    context.setTransform(ratio, 0, 0, ratio, 0, 0); context.clearRect(0, 0, width, stripHeight)
    context.fillStyle = colour('--wave', '#5ccfff'); context.globalAlpha = 0.7
    const columns = stripPeaks.length / 2
    for (let column = 0; column < columns; column++) { const high = Math.min(1, stripPeaks[column * 2 + 1]), low = Math.max(-1, stripPeaks[column * 2]); context.fillRect(column * width / columns, stripHeight / 2 - high * (stripHeight / 2 - 1), Math.max(1, width / columns), Math.max(1, (high - low) * (stripHeight / 2 - 1))) }
    context.globalAlpha = 1
    context.fillStyle = colour('--warn', '#ffb74d'); for (const change of status.changes) context.fillRect(change.frame / overviewSpan * width, 0, 1, stripHeight)
    const left = Math.max(0, start) / overviewSpan * width, right = Math.min(recorded, start + span) / overviewSpan * width
    context.strokeStyle = colour('--accent', '#5ccfff'); context.lineWidth = 1.5; context.strokeRect(left, 1, Math.max(3, right - left), stripHeight - 2)
  }
  function onStrip(event: PointerEvent) {
    if (!app.status || (event.type === 'pointermove' && !(event.buttons & 1))) return
    if (event.type === 'pointerdown') stripCanvas?.setPointerCapture(event.pointerId)
    pause()
    const frame = event.offsetX / liveWidth * overviewSpan
    viewStart = frame - viewSpan / 2; selected = -1; drawLive()
  }
  let drawing = false
  async function drawLive() {
    const status = app.status
    if (!liveCanvas || !status?.recording || !status.sampleRate || drawing) return
    drawing = true
    try {
      const rate = status.sampleRate, recorded = app.levels.seconds * rate, { start, span } = liveRange()
      const data = await request<{ mode: string; channels: number[][] }>({ type: 'peaks', id: status.sessionId, start: Math.max(0, start), end: Math.max(start + span, 1), columns: Math.round(liveWidth) })
      if (!liveCanvas || data.mode !== 'peaks') return
      const now = liveRange() // the view may have moved while the peaks were on their way
      const ratio = devicePixelRatio, height = liveHeight, context = liveCanvas.getContext('2d')!
      if (liveCanvas.width !== Math.round(liveWidth * ratio)) { liveCanvas.width = Math.round(liveWidth * ratio); liveCanvas.height = height * ratio }
      context.setTransform(ratio, 0, 0, ratio, 0, 0); context.clearRect(0, 0, liveWidth, height)
      const style = getComputedStyle(document.documentElement), colour = (name: string, fallback: string) => style.getPropertyValue(name).trim() || fallback
      const toX = (frame: number) => (frame - now.start) / now.span * liveWidth
      // The audio before the start (a view starting before 0) is simply blank
      const dataStart = Math.max(0, start), lanes = data.channels.length, laneHeight = height / lanes, columns = data.channels[0]?.length / 2 || 0, framesPerColumn = (start + span - dataStart) / columns
      context.fillStyle = colour('--wave', '#5ccfff')
      data.channels.forEach((values, lane) => { const middle = laneHeight * (lane + 0.5); for (let column = 0; column < columns; column++) { const at = dataStart + column * framesPerColumn; if (at > recorded) break; const x = toX(at), low = Math.max(-1, values[column * 2] * liveScale), high = Math.min(1, values[column * 2 + 1] * liveScale); context.fillRect(x, middle - high * (laneHeight / 2 - 1), Math.max(1, liveWidth * framesPerColumn / now.span), Math.max(1, (high - low) * (laneHeight / 2 - 1))) } })
      // What's still to come, and where "now" is
      context.fillStyle = colour('--panel-strong', 'rgba(255,255,255,0.1)'); const nowX = toX(recorded); if (nowX < liveWidth) context.fillRect(Math.max(0, nowX), 0, liveWidth - Math.max(0, nowX), height)
      drawStrip(now.start, now.span, recorded, rate, colour)
      // Markers: the selected one in the accent colour with a band; the hovered one brighter
      context.font = '10.5px "Segoe UI Variable Text", "Segoe UI", sans-serif'; context.textBaseline = 'top'
      status.changes.forEach((change, index) => {
        const x = markerX(index); if (x < -2 || x > liveWidth + 2) return
        const isSelected = index === selected, isHovered = index === hovered && !isSelected
        context.fillStyle = isSelected ? colour('--accent', '#5ccfff') : colour('--warn', '#ffb74d')
        if (isSelected || isHovered) { context.globalAlpha = isSelected ? 0.2 : 0.12; context.fillRect(x - 8, 0, 16, height); context.globalAlpha = 1 }
        context.fillRect(x - 1, 0, isSelected || isHovered ? 3 : 2, height); context.fillRect(x, 0, 11, 13) // the flag: drag it to move the marker
        context.fillText(`${index + 1} ${change.title || ''}`.trim(), x + 15, 3)
      })
      if (liveScale > 1.05) { context.fillStyle = colour('--fg-quiet', '#888'); context.textBaseline = 'top'; context.fillText(`Height ×${liveScale < 10 ? liveScale.toFixed(1) : Math.round(liveScale)} (Ctrl+wheel)`, liveWidth - 120, 3) }
      // The time scale
      context.fillStyle = colour('--fg-quiet', '#888'); context.textBaseline = 'bottom'; context.font = '10px "Segoe UI Variable Text", "Segoe UI", sans-serif'
      const stepSeconds = [0.1, 0.2, 0.5, 1, 2, 5, 10, 30, 60, 120, 300, 600, 900, 1800].find(step => step * rate / now.span * liveWidth >= 70) ?? 1800
      for (let second = Math.ceil(Math.max(0, now.start) / rate / stepSeconds) * stepSeconds; second * rate < now.start + now.span; second += stepSeconds) { const x = toX(second * rate); context.fillRect(x, height - 10, 1, 4); context.fillText(stepSeconds < 1 ? `${formatTime(second * rate, rate, false)}.${Math.round((second % 1) * 10)}` : formatTime(second * rate, rate, false), x + 3, height - 1) }
    } catch { } finally { drawing = false }
  }
  $effect(() => { if (recording) { liveTimer = window.setInterval(drawLive, 250); return () => clearInterval(liveTimer) } else { following = true; selected = -1; overviewSpan = 0; dragging = null } })
  // A marker's flag (the top of the wave) drags the marker itself; anywhere else drags the view (SPEC.md › Recording › Live editing)
  const flagZone = 16
  let flagDrag: null | { index: number; frame: number } = null
  function onLiveDown(event: PointerEvent) {
    if (event.button !== 0 || !app.status) return
    const onMarker = markerAt(event.offsetX)
    if (event.offsetY < flagZone && onMarker >= 0) { pause(); flagDrag = { index: onMarker, frame: app.status.changes[onMarker].frame }; liveCanvas?.setPointerCapture(event.pointerId); return }
    pause()
    dragging = { startX: event.offsetX, startViewStart: viewStart, pinX: selected >= 0 ? markerX(selected) : 0, moved: false, onMarker }
    liveCanvas?.setPointerCapture(event.pointerId)
  }
  function onLiveMove(event: PointerEvent) {
    if (flagDrag && app.status) { flagDrag.frame = Math.max(0, Math.min(app.levels.seconds * app.status.sampleRate, viewStart + event.offsetX * viewSpan / liveWidth)); if (liveCanvas) liveCanvas.style.cursor = 'ew-resize'; drawLive(); return }
    if (!dragging) {
      const index = markerAt(event.offsetX)
      if (index !== hovered) { hovered = index; drawLive() }
      if (liveCanvas) liveCanvas.style.cursor = index >= 0 ? (event.offsetY < flagZone ? 'ew-resize' : 'pointer') : 'grab'
      if (index >= 0 && event.offsetY < flagZone) { liveCanvas?.setAttribute('title', `Drag the flag to move marker ${index + 1}`); return }
      liveCanvas?.setAttribute('title', index >= 0 ? (index === selected ? `Marker ${index + 1} is selected: drag the audio under it, click it to let go` : `Click to select marker ${index + 1}, then drag the audio under it`) : following ? 'Drag to stop following and look around; the wheel zooms, Ctrl+wheel sets the wave height' : 'Drag to pan, wheel to zoom, Ctrl+wheel for height')
      return
    }
    if (!app.status) return
    if (!dragging.moved && Math.abs(event.offsetX - dragging.startX) < 3) return
    dragging.moved = true
    if (liveCanvas) liveCanvas.style.cursor = 'grabbing'
    viewStart = dragging.startViewStart - (event.offsetX - dragging.startX) * viewSpan / liveWidth
    drawLive()
  }
  function onLiveUp() {
    if (flagDrag) { const done = flagDrag; flagDrag = null; sendToShell({ type: 'editChange', index: done.index, frame: Math.round(done.frame) }); drawLive(); return }
    const drag = dragging; if (!drag) return
    if (drag.moved && selected >= 0) {
      const frame = Math.round(markerFrame(selected)); const index = selected
      dragging = null
      sendToShell({ type: 'editChange', index, frame })
    } else {
      dragging = null
      // A click (no drag): on a marker selects it, or lets go of it when it's the selected one
      if (!drag.moved && drag.onMarker >= 0) { if (drag.onMarker === selected) deselect(); else selectMarker(drag.onMarker) }
    }
    if (liveCanvas) liveCanvas.style.cursor = 'grab'
    drawLive()
  }
  function onLiveWheel(event: WheelEvent) {
    if (!app.status) return
    event.preventDefault()
    if (event.ctrlKey) { liveScale = Math.max(1, Math.min(10000, liveScale * Math.pow(1.0025, -event.deltaY))); drawLive(); return }
    pause()
    // The side wheel (or Shift+wheel) pans, like dragging
    if (event.shiftKey || Math.abs(event.deltaX) > Math.abs(event.deltaY)) { const delta = Math.abs(event.deltaX) > Math.abs(event.deltaY) ? event.deltaX : event.deltaY; viewStart += delta * viewSpan / liveWidth; drawLive(); return }
    const rate = app.status.sampleRate, factor = Math.exp(event.deltaY * 0.0015)
    const anchorX = selected >= 0 ? markerX(selected) : event.offsetX
    const anchorFrame = viewStart + anchorX * viewSpan / liveWidth
    viewSpan = Math.max(0.2 * rate, Math.min(Math.max(overviewSpan, 60 * rate) * 1.5, viewSpan * factor))
    viewStart = anchorFrame - anchorX * viewSpan / liveWidth
    drawLive()
  }
  $effect(() => { const canvas = liveCanvas; if (!canvas) return; canvas.addEventListener('wheel', onLiveWheel, { passive: false }); return () => canvas.removeEventListener('wheel', onLiveWheel) })
  function onLiveKey(event: KeyboardEvent) {
    if (event.key !== 'Escape' || !recording || (event.target as HTMLElement)?.matches?.('input, textarea')) return
    if (selected >= 0) { event.preventDefault(); deselect() } else if (!following) { event.preventDefault(); catchUp() }
  }

  function nudge(index: number, frame: number, seconds: number) { sendToShell({ type: 'editChange', index, frame: Math.max(0, frame + seconds * (app.status?.sampleRate ?? 48000)) }) }
  function setTime(index: number, text: string, rate: number) {
    const parts = text.trim().split(':').map(Number)
    if (parts.some(part => Number.isNaN(part))) return
    const seconds = parts.reduce((total, part) => total * 60 + part, 0)
    sendToShell({ type: 'editChange', index, frame: Math.round(seconds * rate) })
  }
  // Issues while recording (SPEC.md › Recording › Issues): each one names the track it hit and what to do about it
  type Issue = { kind: 'dropout' | 'clipping' | 'noname' | 'retake' | 'silence' | 'other'; frame: number; index: number; text: string; action: string }
  const issues = $derived.by<Issue[]>(() => {
    const status = app.status; if (!status?.recording) return []
    const rate = status.sampleRate || 48000, recorded = app.levels.seconds * rate
    const trackAt = (frame: number) => { let found = -1; status.changes.forEach((change, index) => { if (change.frame <= frame) found = index }); return found }
    const name = (index: number) => { const change = status.changes[index]; return change?.title ? `“${change.title}”` : index >= 0 ? `track ${index + 1}` : 'the recording' }
    const list: Issue[] = []
    for (const dropout of status.dropoutList ?? []) { const index = trackAt(dropout.frame); list.push({ kind: 'dropout', frame: dropout.frame, index, text: `Dropout at ${formatTime(dropout.frame, rate, false)} in ${name(index)}: about ${dropout.ms} ms lost.`, action: 'Play that track again; the new take replaces this one.' }) }
    for (const other of status.otherSounds ?? []) { const index = trackAt(other.frame); list.push({ kind: 'other', frame: other.frame, index, text: `${other.app} made a sound at ${formatTime(other.frame, rate, false)} in ${name(index)} (${(other.ms / 1000).toFixed(1)} s, peak ${other.peakDb} dB).`, action: 'It is in the recording: play that track again, or turn on Do not disturb so it does not happen again.' }) }
    for (const clip of status.clips ?? []) { const index = trackAt(clip.frame); list.push({ kind: 'clipping', frame: clip.frame, index, text: `Clipping at ${formatTime(clip.frame, rate, false)} in ${name(index)}: ${clip.samples} samples at full scale.`, action: 'Turn the source down a little and play that track again.' }) }
    status.changes.forEach((change, index) => {
      if (!change.title && recorded - change.frame > 10 * rate && !change.problem) list.push({ kind: 'noname', frame: change.frame, index, text: `Track ${index + 1} (from ${formatTime(change.frame, rate, false)}) has no name.`, action: 'Type its artist and title in the list below.' })
      if (change.problem === 'retake' && !list.some(issue => issue.index === index)) list.push({ kind: 'retake', frame: change.frame, index, text: `Track ${index + 1} ${name(index)} is marked for a retake.`, action: 'Play it again; the new take replaces this one.' })
    })
    list.sort((first, second) => second.frame - first.frame)
    // Silence: a countdown to the auto-stop once it has lasted 30 s (SPEC.md › Recording › Auto-stop)
    if (app.levels.silentSeconds >= 30) {
      const left = app.levels.silenceStopIn, clock = (seconds: number) => `${Math.floor(seconds / 60)}:${String(Math.floor(seconds % 60)).padStart(2, '0')}`
      list.unshift({ kind: 'silence', frame: recorded, index: -1, text: `Nothing playing for ${clock(app.levels.silentSeconds)}.`, action: left >= 0 ? `Recording stops by itself in ${clock(left)}; the silence won't be part of any track.` : 'Recording carries on until you stop it.' })
    }
    return list
  })
  const sizeText = $derived(app.levels.bytes > 1e9 ? `${(app.levels.bytes / 1e9).toFixed(2)} GB` : `${(app.levels.bytes / 1e6).toFixed(0)} MB`)
  // What each output profile would make of the recording so far: FLAC from the encoder's running estimate, lossy from bitrate × time, WAV as is (SPEC.md › Recording)
  const megabytes = (bytes: number) => bytes > 1e9 ? `${(bytes / 1e9).toFixed(2)} GB` : `${(bytes / 1e6).toFixed(0)} MB`
  const estimates = $derived.by(() => {
    const status = app.status; if (!status?.recording) return [] as { name: string; text: string }[]
    const seconds = app.levels.seconds, bits = status.bitDepth === 32 ? 24 : status.bitDepth || 24, pcmBytes = seconds * status.sampleRate * (status.channels || 2) * bits / 8
    return app.outputProfiles.map(profile => {
      const bytes = profile.format === 'flac' ? pcmBytes * (status.flacRatio || 0.6) : profile.format === 'wav' ? pcmBytes : seconds * (profile.bitrateKbps || 320) * 125
      return { name: profile.name, text: `~${megabytes(bytes)}${profile.format === 'flac' && !status.flacRatio ? ' (measuring)' : ''}` }
    })
  })
  const firstEstimate = $derived(estimates.find(estimate => app.outputProfiles.find(profile => profile.name === estimate.name)?.id === app.activeOutput) ?? estimates[0])
  const current = $derived(app.status?.current)
  let startingOver = $state(false)
  // Problem with a track (SPEC.md › Recording › Problems)
  let problemFor = $state(-1), problemNote = $state('')
  const problemLabel = (problem: string) => problem === 'note' ? 'Noted' : problem === 'skip' ? 'Left out' : problem === 'dropout' ? 'Dropout' : problem === 'clipping' ? 'Clipping' : 'Retake'
  function setProblem(problem: string, restartNow = false) {
    const index = problemFor; problemFor = -1; if (index < 0) return
    sendToShell({ type: 'setProblem', index, problem, note: problemNote.trim() })
    if (restartNow) sendToShell({ type: 'marker' }) // the new take starts here, now
  }
  // Start now, or arm: recording waits for sound and drops the silence before it (SPEC.md › Recording); remembered
  let startOnSound = $state(readSetting('startOnSound', '') === '1')
  function setStartOnSound(on: boolean) { startOnSound = on; writeSetting('startOnSound', on ? '1' : '') }
  // Albums so far (SPEC.md › Recording › Live editing): runs of markers sharing artist and album, in order. Track numbers count up
  // within each (a marker set for a retake doesn't count); the list below and the strip above both use these
  type Change = NonNullable<typeof app.status>['changes'][number]
  type Album = { key: string; artist: string; album: string; year: string; indexes: number[] }
  const loose = (text: string) => (text ?? '').toLowerCase().replace(/[^\p{L}\p{N}]/gu, '')
  const changes = $derived<Change[]>(app.status?.changes ?? [])
  const albums = $derived.by(() => {
    const list: Album[] = []
    changes.forEach((change, index) => {
      const key = change.album ? 'album:' + loose(change.album) : 'artist:' + loose(change.artist), last = list[list.length - 1] // by album, so a featured artist doesn't split it
      if (last && last.key === key) { last.indexes.push(index); if (!last.year && change.year) last.year = change.year }
      else list.push({ key, artist: change.artist, album: change.album, year: change.year ?? '', indexes: [index] })
    })
    return list
  })
  function trackNumber(group: Album, index: number) { if (changes[index]?.problem) return 0; return group.indexes.filter(other => !changes[other]?.problem).indexOf(index) + 1 }
  let openAlbum = $state('') // the album card showing its tracks
  function hue(text: string) { let hash = 0; for (const char of text) hash = (hash * 31 + char.charCodeAt(0)) | 0; return Math.abs(hash) % 360 }
  function initials(group: Album) { return ((group.album || group.artist || '?').match(/\p{L}|\p{N}/gu) ?? ['?']).slice(0, 2).join('').toUpperCase() }
  function showAlbum(group: Album) { openAlbum = openAlbum === group.key + group.indexes[0] ? '' : group.key + group.indexes[0]; document.getElementById('album-' + group.indexes[0])?.scrollIntoView({ block: 'nearest', behavior: 'smooth' }) }
  // An album's artist, name or year, for all its tracks at once
  function editAlbum(group: Album, field: 'artist' | 'album' | 'year', value: string) { sendToShell({ type: 'editChanges', indexes: group.indexes, [field]: value.trim() }) }
  // A track joining the album above or below: take that album's names, or give that album this track's names
  let joining = $state<null | { index: number; target: Album }>(null)
  let dragIndex = -1
  function joinAs(mode: 'theirs' | 'mine') {
    const join = joining; joining = null; if (!join) return
    const change = changes[join.index]; if (!change) return
    if (mode === 'theirs') sendToShell({ type: 'editChanges', indexes: [join.index], artist: join.target.artist, album: join.target.album, year: join.target.year })
    else sendToShell({ type: 'editChanges', indexes: join.target.indexes, artist: change.artist, album: change.album, year: change.year ?? '' })
  }
  const albumLine = (group: { artist: string; album: string; year?: string }) => [group.artist || 'Unknown artist', group.album ? group.album + (group.year ? ` (${group.year})` : '') : 'no album'].join(' - ')
  // Every field the chosen source gives, so it's clear what is picked up (SPEC.md › Recording › Now playing)
  const currentSource = $derived(current?.title ? (app.sources.find(candidate => candidate.key === current.key) ?? { key: '', kind: '', label: current.label, artist: current.artist, title: current.title, album: current.album ?? '', playing: true, rule: '' }) : null)
</script>

<svelte:window onkeydown={onLiveKey} />
{#if problemFor >= 0}
  {@const change = changes[problemFor]}
  <div class="backdrop" role="presentation" onpointerdown={(event) => { if (event.target === event.currentTarget) problemFor = -1 }}>
    <div class="problem-box" role="dialog" aria-label="Problem with this track">
      <h3>Problem with track {problemFor + 1}{change?.title ? `: ${change.title}` : ''}?</h3>
      <label class="note-field">Note (shown in this app only, e.g. "a notification sound at 2:10")<input value={problemNote} oninput={(event) => problemNote = (event.target as HTMLInputElement).value} placeholder="What went wrong" /></label>
      <button class="choice" onclick={() => setProblem('note')}><strong>Note it and carry on</strong><span>The take stays as it is; the note shows next to it later so you can listen to that part.</span></button>
      <button class="choice" onclick={() => setProblem('retake')}><strong>I'll play it again later</strong><span>Recording carries on. When this track has been recorded again (same name), the new take replaces this one; until then this take is kept.</span></button>
      <button class="choice" onclick={() => setProblem('retake', true)}><strong>Restart it now</strong><span>Marks this take as bad and starts a new track here, now. Go back to the start of the track in your player and play it again; the new take replaces this one. Everything recorded before stays.</span></button>
      <button class="choice" onclick={() => setProblem('skip')}><strong>Leave this take out</strong><span>Recording carries on; this take won't be exported.</span></button>
      <div class="problem-foot">{#if change?.problem}<button class="ghost" onclick={() => { problemNote = ''; setProblem('') }}>No problem after all</button>{/if}<span class="spacer"></span><button class="ghost" onclick={() => problemFor = -1}>Cancel</button></div>
    </div>
  </div>
{/if}
{#if startingOver}
  <Dialog title="Are you sure?" text={`This throws away the recording so far (${formatTime(app.levels.seconds * 1000, 1000, false)}) and its markers. It can't be undone.`} danger confirmLabel="Throw it away" onconfirm={() => { startingOver = false; sendToShell({ type: 'discardRecording', restart: false }) }} oncancel={() => startingOver = false} />
{/if}
<div class="record" class:recording>
  {#if !recording}
    <h2>Record</h2>
    <Hint id="record-intro" text="Record a whole listening session into one file. Each time the playing track's name changes, a new track is marked; with no names, gaps split them. You fine-tune and export afterwards." />
  {/if}
  <Hint id="other-sounds" text="Recording what the PC plays records everything it plays: turn on Do not disturb (Windows notifications) and close apps that ding. Any other app's sound during a recording is flagged in Issues." />
  {#if !recording}
    <div class="settings-row">
      <Dropdown label="Recording profile" value={profileId} options={profileChoices} align="left" onchange={(value) => sendToShell({ type: 'setActiveProfile', kind: 'recording', profileId: value })} />
      <Dropdown label="Track names" value={source} options={sourceChoices} align="left" onchange={(value) => source = value} />
      <button class="ghost" onclick={() => sendToShell({ type: 'openSettings', tab: 'Recording' })}>Edit profiles…</button>
    </div>
    <p class="quiet device">{app.status?.device ? `From ${app.status.device}${profile?.loopback ? ' (what it plays)' : ''}` : 'No audio device open.'}{profile?.bitDepth ? ` · ${profile.bitDepth === 32 ? '32-bit float' : profile.bitDepth + '-bit'} WAV` : ''}</p>
  {/if}
  {#if recording && app.status?.dropouts}<p class="warn">{app.status.dropouts} dropout{app.status.dropouts === 1 ? '' : 's'}: the PC was too busy and samples were lost. They're marked in the session.</p>{/if}

  <div class="deck" class:live={recording}>
    <button class="record-button" class:on={recording && !app.status?.paused} class:paused={recording && app.status?.paused} onclick={() => recording ? sendToShell({ type: 'pauseRecording', paused: !app.status?.paused }) : toggleRecording()} title={recording ? (app.status?.paused ? 'Resume recording into this session' : 'Pause: nothing is recorded until you resume') : 'Record (Ctrl+R)'} aria-label={recording ? (app.status?.paused ? 'Resume' : 'Pause') : 'Start recording'}><Icon name={recording ? (app.status?.paused ? 'record' : 'pause') : 'record'} size={recording ? 16 : 34} /></button>
    <div class="deck-info">
      {#if recording}
        <div class="clock" class:armed-clock={app.status?.waitingForSound || app.status?.paused}>{app.status?.waitingForSound ? 'Armed' : app.status?.paused ? 'Paused' : formatTime(app.levels.seconds * 1000, 1000, false)}</div>
        {#if app.status?.waitingForSound}<div class="quiet info">waiting for sound: press play in your player; the silence before it isn't kept</div>{/if}
        <div class="quiet info">{app.status?.sessionName} · {sizeText} WAV{#if firstEstimate}{' · '}<span class="estimate" title={estimates.map(estimate => `${estimate.name}: ${estimate.text}`).join(String.fromCharCode(10))}>{firstEstimate.text} as {firstEstimate.name}{estimates.length > 1 ? ` (+${estimates.length - 1} more)` : ''}</span>{/if}</div>
      {:else}
        <input class="session-name" bind:value={sessionName} placeholder="Session name (optional)" onkeydown={(event) => { if (event.key === 'Enter') toggleRecording() }} />
        <div class="start-mode" role="group" aria-label="When recording starts">
          <button class:on={!startOnSound} onclick={() => setStartOnSound(false)} title="Recording starts the moment you press the button">Start now</button><button class:on={startOnSound} onclick={() => setStartOnSound(true)} title="Armed: recording starts with the first sound, so you can press record, switch to your player and press play">Start on sound</button>
        </div>
        <div class="quiet">Ctrl+R starts and stops recording from anywhere in the app.</div>
      {/if}
    </div>
    {#if recording}<button class="ghost start-over" onclick={() => startingOver = true} title="Throw this recording away">Start over…</button>{/if}
    {#if recording}<button class="primary finish" onclick={toggleRecording} title="Stop recording for good and go to Albums & cuts (Ctrl+R)">Finish and start editing the cuts<kbd>Ctrl+R</kbd></button>{/if}
    {#if recording}<button class="ghost marker" onclick={() => sendToShell({ type: 'marker' })} title="Mark a new track here (M)"><Icon name="marker" size={14} />New track here<kbd>M</kbd></button>{/if}
  </div>

  {#if issues.length}
    <h3>Issues</h3>
    <div class="issues">
      {#each issues as issue (issue.kind + issue.frame)}
        <div class="issue" class:soft={issue.kind === 'noname' || issue.kind === 'retake' || issue.kind === 'silence'}>
          <span class="issue-text"><strong>{issue.text}</strong> {issue.action}</span>
          {#if issue.kind === 'silence' && app.levels.silenceStopIn >= 0}<button class="ghost" onclick={() => sendToShell({ type: 'keepThroughSilence' })} title="Don't stop by itself during this recording">Keep recording</button><button class="ghost" onclick={toggleRecording}>Stop now</button>{/if}
          {#if issue.kind !== 'noname' && issue.kind !== 'retake' && issue.kind !== 'silence' && issue.index >= 0 && !app.status?.changes[issue.index]?.problem}<button class="ghost" onclick={() => sendToShell({ type: 'setProblem', index: issue.index, problem: issue.kind })}>Replace with a retake</button>{/if}
          {#if issue.index >= 0 && app.status?.changes[issue.index]?.problem}<span class="quiet">Will be replaced by a later take of the same track</span><button class="ghost" onclick={() => sendToShell({ type: 'setProblem', index: issue.index, problem: '' })}>Keep this take</button>{/if}
        </div>
      {/each}
    </div>
  {/if}
  <h3>Input level</h3>
  <LevelMeter />
  {#if app.levels.clipped > 0}<p class="warn">The input reached full scale. Turn the source down a little to avoid clipping.</p>{/if}

  {#if recording}
    <!-- One line each while recording (SPEC.md › Recording › Compact view): only what the source gives, in the order artist, album, year, title -->
    <p class="line"><span class="line-label">Source</span>{#if app.status?.cutoffHz}<strong>spectrum stops at {(app.status.cutoffHz / 1000).toFixed(1)} kHz</strong><span class="quiet">{' · '}{app.status.cutoffText}</span>{:else}<span class="quiet">measuring…</span>{/if}</p>
    <p class="line now-line"><span class="line-label">Now playing</span>
      {#if currentSource}
        {#if currentSource.artist}<span class="field"><i>Artist</i>{currentSource.artist}</span>{/if}
        {#if currentSource.album}<span class="field"><i>Album</i>{currentSource.album}{current?.year ? ` (${current.year})` : ''}</span>{/if}
        {#if currentSource.albumArtist && currentSource.albumArtist !== currentSource.artist}<span class="field"><i>Album artist</i>{currentSource.albumArtist}</span>{/if}
        {#if currentSource.trackNumber}<span class="field"><i>No.</i>{currentSource.trackNumber}{currentSource.trackCount ? '/' + currentSource.trackCount : ''}</span>{/if}
        <span class="field"><i>Title</i>{currentSource.title}</span>
        <span class="quiet small">{currentSource.label.replace('Media info: ', 'via ')}{currentSource.playing ? '' : ' (paused)'}</span>
      {:else}<span class="quiet">nothing named; gaps split tracks</span>{/if}
    </p>
  {:else}
    <h3>Source quality</h3>
    <p class="source">{#if app.status?.cutoffHz}<strong>Spectrum stops at {(app.status.cutoffHz / 1000).toFixed(1)} kHz</strong><span class="quiet">{' · '}{app.status.cutoffText}</span>{:else}<span class="quiet">{app.status?.capturing ? 'Measuring: needs a couple of seconds of sound.' : 'Measured from the sound as it arrives.'}</span>{/if}</p>
    <h3>Now playing</h3>
    {#if currentSource}
      {@const missing = 'not provided'}
      <table class="now-table">
        <tbody>
          <tr><th>From</th><td>{currentSource.label}{currentSource.playing ? '' : ' (paused)'}</td></tr>
          <tr><th>Artist</th><td class:none={!currentSource.artist}>{currentSource.artist || missing}</td></tr>
          <tr><th>Album</th><td class:none={!currentSource.album}>{currentSource.album || missing}</td></tr>
          <tr><th>Year</th>{#if current?.year}<td>{current.year} <span class="quiet">· looked up on MusicBrainz (Windows media info has no year)</span></td>{:else}<td class="none">{currentSource.album ? 'looking it up…' : 'not provided (needs an album name to look up)'}</td>{/if}</tr>
          <tr><th>Title</th><td class:none={!currentSource.title}>{currentSource.title || missing}</td></tr>
          {#if currentSource.kind === 'media'}
            <tr><th>Album artist</th><td class:none={!currentSource.albumArtist}>{currentSource.albumArtist || missing}</td></tr>
            <tr><th>Track no.</th><td class:none={!currentSource.trackNumber}>{currentSource.trackNumber ? `${currentSource.trackNumber}${currentSource.trackCount ? ' of ' + currentSource.trackCount : ''}` : missing}</td></tr>
            {#if currentSource.genres}<tr><th>Genre</th><td>{currentSource.genres}</td></tr>{/if}
            <tr><th>Picture</th><td class:none={!currentSource.picture}>{currentSource.picture ? 'provided' : missing}</td></tr>
          {:else if currentSource.rule}<tr><th>Rule</th><td>{currentSource.rule}</td></tr>{/if}
        </tbody>
      </table>
    {:else}
      <p class="now"><span class="quiet">Nothing named yet. {source === 'none' ? 'Tracks will be split on gaps.' : 'Play something in a browser or app; gaps split tracks when there are no names.'}</span></p>
    {/if}
  {/if}

  {#if recording}
    {#if albums.length}
      <h3>Albums so far</h3>
      <div class="album-strip">
        {#each albums.slice().reverse() as group, position (group.key + group.indexes[0])}
          {@const latest = changes[group.indexes[group.indexes.length - 1]]}
          {@const open = openAlbum === group.key + group.indexes[0]}
          <button class="album-card" class:latest={position === 0} class:open onclick={() => showAlbum(group)} title={open ? 'Hide its tracks' : 'Show its tracks'}>
            <span class="disc" style:--hue={hue(group.key)}>{initials(group)}</span>
            <span class="album-text">
              <span class="album-name">{albumLine(group)}</span>
              {#if position === 0}<span class="album-track">{trackNumber(group, group.indexes[group.indexes.length - 1]) || '–'}. {latest?.title || 'untitled'}</span>
              {:else}<span class="album-track quiet">{group.indexes.length} track{group.indexes.length === 1 ? '' : 's'}</span>{/if}
              {#if open}<span class="album-tracks">{#each group.indexes as index}<span>{trackNumber(group, index) || '–'}. {changes[index].title || 'untitled'}</span>{/each}</span>{/if}
            </span>
          </button>
        {/each}
      </div>
    {/if}

    <div class="live-head">
      {#if selected >= 0}<span><strong>Marker {selected + 1} selected</strong></span>
      {:else if !following}<span><strong>Paused view</strong><span class="quiet">{' · '}recording carries on</span></span>
      {:else}<span><strong>Following</strong><span class="quiet">{' · '}the last minute</span></span>{/if}
      {#if !following}<button class="primary catch-up" onclick={catchUp} title="Back to following the last minute (Esc)">Catch up</button>{/if}
    </div>
    <Hint id="live-wave" text="Drag or zoom (wheel) the wave to stop following and look around; Ctrl+wheel sets its height. Click a marker to select it, then drag the audio under it and let go to place it; click it again or press Esc to let go. Catch up goes back to the last minute. The strip above is the whole recording: click it to look there." />
    <canvas class="live-strip" bind:this={stripCanvas} title="The whole recording so far: click or drag to look there" onpointerdown={onStrip} onpointermove={onStrip}></canvas>
    <canvas class="live-wave" bind:this={liveCanvas} bind:clientWidth={liveWidth} tabindex="-1" onpointerdown={onLiveDown} onpointermove={onLiveMove} onpointerup={onLiveUp} onpointercancel={onLiveUp} onpointerleave={() => { if (!dragging && hovered >= 0) { hovered = -1; drawLive() } }}></canvas>

    <div class="tracks-head">
      <h3>Tracks marked so far</h3>
      <Dropdown label="Track names" value={source} options={sourceChoices} align="right" onchange={(value) => source = value} />
    </div>
    <Hint id="live-edit-albums" text="Tracks are grouped by album; numbers count up by themselves. Edit an album's artist, name or year in its header to change all its tracks. A track that should belong to the album above or below: drag it by ⋮⋮ onto that album, or use ⤒ / ⤓." />
    <div class="changes">
      {#each albums as group, groupIndex (group.key + group.indexes[0])}
        {@const above = albums[groupIndex - 1]}
        {@const below = albums[groupIndex + 1]}
        <div class="album-head" id={'album-' + group.indexes[0]} role="group" aria-label={albumLine(group)}
          ondragover={(event) => { if (dragIndex >= 0 && !group.indexes.includes(dragIndex)) event.preventDefault() }}
          ondrop={(event) => { event.preventDefault(); if (dragIndex >= 0 && !group.indexes.includes(dragIndex)) joining = { index: dragIndex, target: group }; dragIndex = -1 }}>
          <span class="head-name"><span class="disc small" style:--hue={hue(group.key)}>{initials(group)}</span><span class="quiet small">{group.indexes.length}</span></span>
          <input value={group.artist} placeholder="Artist" title="This album's artist, for all its tracks" onchange={(event) => editAlbum(group, 'artist', (event.target as HTMLInputElement).value)} />
          <input value={group.album} placeholder="Album" title="This album's name, for all its tracks" onchange={(event) => editAlbum(group, 'album', (event.target as HTMLInputElement).value)} />
          <input class="year" value={group.year} placeholder="Year" title="This album's year, for all its tracks" onchange={(event) => editAlbum(group, 'year', (event.target as HTMLInputElement).value)} />
        </div>
        {#each group.indexes as index, position (index + ':' + changes[index].frame)}
          {@const change = changes[index]}
          {@const rate = app.status?.sampleRate ?? 48000}
          <div class="change" role="listitem">
            <span class="handle" draggable="true" ondragstart={(event) => { dragIndex = index; event.dataTransfer?.setData('text/plain', String(index)) }} ondragend={() => dragIndex = -1} title="Drag onto another album to move this track into it">⋮⋮</span>
            <button class="number" class:on={selected === index} onclick={() => selected === index ? deselect() : selectMarker(index, true)} title="Select this marker in the wave above, zoomed in around it">{index + 1}</button>
            <input class="time-box" value={formatTime(change.frame, rate, false)} onchange={(event) => setTime(index, (event.target as HTMLInputElement).value, rate)} title="Where this track starts (m:ss)" aria-label="Start time" />
            <input value={change.artist} placeholder="Artist" onchange={(event) => sendToShell({ type: 'editChange', index, artist: (event.target as HTMLInputElement).value.trim() })} />
            <input value={change.album} placeholder="Album" onchange={(event) => sendToShell({ type: 'editChange', index, album: (event.target as HTMLInputElement).value.trim() })} />
            <input class="year" value={change.year ?? ''} placeholder={group.year || 'Year'} title={group.year && !change.year ? `The album's year (${group.year}) goes on this track too` : 'Year'} onchange={(event) => sendToShell({ type: 'editChange', index, year: (event.target as HTMLInputElement).value.trim() })} />
            <span class="track-no" title="Track number: counts up within the album">{trackNumber(group, index) || '–'}</span>
            <input value={change.title} placeholder={change.manual ? 'Title (marked by hand)' : 'Title'} onchange={(event) => sendToShell({ type: 'editChange', index, title: (event.target as HTMLInputElement).value.trim() })} />
            <span class="joins">
              {#if position === 0 && above}<button onclick={() => joining = { index, target: above }} title={`Move into the album above: ${albumLine(above)}`}>⤒</button>{/if}
              {#if position === group.indexes.length - 1 && below}<button onclick={() => joining = { index, target: below }} title={`Move into the album below: ${albumLine(below)}`}>⤓</button>{/if}
            </span>
            <button class="retake" class:on={!!change.problem} onclick={() => { problemFor = index; problemNote = change.note ?? '' }} title={change.problem ? `${problemLabel(change.problem)}${change.note ? ': ' + change.note : ''}. Click to change it` : 'Problem with this track? Note it, play it again or leave it out'}>{change.problem ? problemLabel(change.problem) : 'Problem?'}</button>
            <button class="remove" onclick={() => sendToShell({ type: 'removeChange', index })} title="Remove this marker (the audio stays)" aria-label="Remove marker">✕</button>
          </div>
        {/each}
      {:else}<p class="quiet">None yet. Press M to mark where a track starts.</p>{/each}
    </div>
  {/if}
</div>
{#if joining}
  {@const join = joining}
  {@const change = changes[join.index]}
  <Dialog title="Move this track into that album?" wide text={`“${change?.title || 'This track'}” (${albumLine({ artist: change?.artist ?? '', album: change?.album ?? '', year: change?.year })}) joins ${albumLine(join.target)}. Which names should they share?`}
    confirmLabel="Use that album's names" altLabel="Give that album this track's names" onconfirm={() => joinAs('theirs')} onalt={() => joinAs('mine')} oncancel={() => joining = null} />
{/if}

<style>
  .record { padding-bottom: 20px; }
  .record.recording h3 { margin-top: 10px; }
  .settings-row { display: flex; gap: 8px; align-items: center; flex-wrap: wrap; }
  .device { margin: 8px 0 14px; font-size: 12.5px; }
  .deck { display: flex; flex-wrap: wrap; align-items: center; gap: 18px; padding: 16px 18px; border-radius: 14px; background: var(--panel); border: 1px solid var(--line); transition: padding 300ms ease, gap 300ms ease, border-radius 300ms ease; }
  /* While recording the deck shrinks to a small bar (SPEC.md › Recording › Compact view) */
  .deck.live { display: inline-flex; flex-wrap: nowrap; gap: 12px; padding: 6px 12px 6px 6px; border-radius: 999px; border-color: color-mix(in srgb, var(--danger) 55%, var(--line)); max-width: 100%; }
  .deck.live .record-button { width: 34px; height: 34px; }
  .deck.live .deck-info { flex-direction: row; align-items: baseline; gap: 10px; min-width: 0; }
  .deck.live .clock { font-size: 20px; font-weight: 700; }
  .deck.live .info { font-size: 11.5px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
  .backdrop { position: fixed; inset: 0; z-index: 120; display: grid; place-items: center; background: rgba(0, 0, 0, 0.25); }
  .problem-box { width: min(560px, calc(100vw - 40px)); display: flex; flex-direction: column; gap: 8px; padding: 16px 18px 14px; border-radius: 14px; background: var(--panel-solid); border: 1px solid var(--line); box-shadow: 0 24px 60px var(--shadow); }
  .problem-box h3 { margin: 0 0 4px; font-size: 16px; text-transform: none; letter-spacing: 0; color: var(--fg); }
  .note-field { display: flex; flex-direction: column; gap: 4px; font-size: 12px; color: var(--fg-quiet); }
  .choice { display: flex; flex-direction: column; align-items: flex-start; gap: 2px; text-align: left; padding: 8px 12px; border-radius: 10px; border: 1px solid var(--line); background: var(--panel); color: var(--fg); font: inherit; cursor: pointer; }
  .choice:hover { border-color: var(--accent); background: var(--accent-soft); }
  .choice span { font-size: 12px; color: var(--fg-quiet); }
  .problem-foot { display: flex; gap: 8px; margin-top: 4px; } .problem-foot .spacer { flex: 1; }
  .start-mode { display: flex; margin: 2px 0; }
  .start-mode button { padding: 3px 12px; font: inherit; font-size: 12px; background: var(--panel); color: var(--fg-quiet); border: 1px solid var(--line); cursor: pointer; }
  .start-mode button:first-child { border-radius: 7px 0 0 7px; } .start-mode button:last-child { border-radius: 0 7px 7px 0; border-left: 0; }
  .start-mode button.on { background: var(--accent-soft); color: var(--accent); font-weight: 600; }
  .armed-clock { color: var(--warn) !important; text-shadow: none !important; animation: blink 1.2s ease-in-out infinite; }
  @keyframes blink { 50% { opacity: 0.45; } }
  .deck.live .start-over { padding: 3px 10px; font-size: 12px; color: var(--danger); border-color: color-mix(in srgb, var(--danger) 45%, transparent); }
  .deck.live .start-over:hover { background: var(--danger); color: #fff; }
  .deck.live .finish { padding: 5px 14px; font-size: 13px; font-weight: 600; }
  .record-button.paused { background: var(--warn); color: #000; border-color: transparent; }
  .deck.live .marker { padding: 3px 10px; font-size: 12px; }
  .record-button { flex: none; width: 72px; height: 72px; border-radius: 50%; display: grid; place-items: center; border: 2px solid color-mix(in srgb, var(--danger) 60%, transparent); background: var(--panel-strong); color: var(--danger); cursor: pointer; transition: transform 120ms, background 120ms, width 300ms ease, height 300ms ease; }
  .record-button:hover { transform: scale(1.04); }
  .record-button.on { background: var(--danger); color: #fff; animation: pulse 1.6s ease-in-out infinite; }
  @keyframes pulse { 0%, 100% { box-shadow: 0 0 0 0 color-mix(in srgb, var(--danger) 60%, transparent); } 60% { box-shadow: 0 0 0 12px color-mix(in srgb, var(--danger) 0%, transparent); } }
  .deck-info { flex: 1; min-width: 0; display: flex; flex-direction: column; gap: 4px; }
  .clock { font-family: var(--font-display); font-size: 34px; color: var(--readout); text-shadow: 0 0 10px var(--readout-glow); letter-spacing: 0.04em; transition: font-size 300ms ease; }
  .session-name { font-size: 16px; padding: 6px 10px; max-width: 360px; }
  .marker { flex: none; }
  .warn { color: var(--danger); font-size: 12.5px; margin: 8px 0 0; }
  .now, .source { margin: 0; }
  .estimate { border-bottom: 1px dotted var(--fg-quiet); cursor: help; }
  .live-wave { display: block; width: 100%; height: 110px; border-radius: 8px; background: var(--panel); outline: none; cursor: grab; touch-action: none; }
  .catch-up { flex: none; }
  .live-strip { display: block; width: 100%; height: 26px; border-radius: 6px; background: var(--panel); margin-bottom: 4px; cursor: pointer; touch-action: none; }
  .now-table { border-collapse: collapse; font-size: 13px; margin: 2px 0 6px; }
  .now-table th { text-align: left; font-weight: 500; color: var(--fg-quiet); padding: 2px 16px 2px 0; white-space: nowrap; vertical-align: top; }
  .now-table td { padding: 2px 0; }
  .now-table td.none { color: var(--fg-quiet); font-style: italic; }
  .live-head { display: flex; align-items: center; justify-content: space-between; gap: 10px; margin: 14px 0 4px; font-size: 12px; }
  .changes { display: flex; flex-direction: column; gap: 3px; }
  /* Rows and album headers share these columns, so an album's artist, album and year sit right above its tracks' */
  .change, .album-head { display: grid; grid-template-columns: 14px 30px 64px minmax(80px, 1fr) minmax(80px, 1fr) 56px 24px minmax(110px, 1.5fr) 30px 64px 26px; gap: 5px; align-items: center; }
  .change { min-height: 26px; padding: 0 1px; }
  .head-name { grid-column: 1 / 4; display: flex; align-items: center; gap: 6px; }
  .album-head { align-items: center; margin-top: 8px; padding: 3px 0; border-radius: 8px; background: var(--panel); border: 1px solid var(--line); }
  .album-head input { font-weight: 600; }
  .year { text-align: center; }
  .track-no { font-family: var(--font-display); font-size: 12px; text-align: center; color: var(--fg-quiet); }
  .handle { cursor: grab; color: var(--fg-quiet); font-size: 12px; user-select: none; text-align: center; }
  .joins { display: flex; gap: 2px; min-width: 30px; }
  .joins button { width: 24px; height: 26px; padding: 0; background: var(--panel); border: 1px solid var(--line); border-radius: 5px; color: var(--fg); cursor: pointer; }
  .joins button:hover { background: var(--hover); }
  .tracks-head { display: flex; align-items: center; justify-content: space-between; gap: 10px; }
  .line { margin: 4px 0; font-size: 13px; display: flex; flex-wrap: wrap; align-items: baseline; gap: 4px 14px; }
  .line-label { font-size: 11px; text-transform: uppercase; letter-spacing: 0.06em; color: var(--fg-quiet); font-weight: 600; min-width: 92px; }
  .field i { font-style: normal; font-size: 11px; color: var(--fg-quiet); margin-right: 5px; }
  .small { font-size: 11.5px; }
  .album-strip { display: flex; gap: 8px; overflow-x: auto; padding: 2px 2px 8px; scrollbar-width: thin; }
  .album-card { flex: none; display: flex; gap: 8px; align-items: flex-start; max-width: 300px; min-width: 180px; padding: 6px 10px 6px 6px; border-radius: 10px; background: var(--panel); border: 1px solid var(--line); color: var(--fg); font: inherit; text-align: left; cursor: pointer; }
  .album-card:hover { background: var(--hover); }
  .album-card.latest { border-color: color-mix(in srgb, var(--accent) 60%, var(--line)); }
  .disc { flex: none; width: 40px; height: 40px; border-radius: 6px; display: grid; place-items: center; font-family: var(--font-display); font-size: 13px; font-weight: 700; color: #fff; background: linear-gradient(135deg, hsl(var(--hue) 55% 45%), hsl(calc(var(--hue) + 40) 60% 35%)); }
  .disc.small { width: 26px; height: 26px; font-size: 10px; border-radius: 5px; }
  .album-text { display: flex; flex-direction: column; gap: 2px; min-width: 0; font-size: 12.5px; }
  .album-name { font-weight: 600; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .album-track { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .album-tracks { display: flex; flex-direction: column; gap: 1px; margin-top: 4px; font-size: 12px; color: var(--fg-quiet); max-height: 160px; overflow-y: auto; }
  .retake { height: 26px; padding: 0 8px; background: var(--panel); border: 1px solid var(--line); border-radius: 5px; color: var(--fg-quiet); font: inherit; font-size: 12px; cursor: pointer; }
  .retake:hover { background: var(--hover); } .retake.on { background: color-mix(in srgb, var(--warn) 25%, transparent); color: var(--fg); border-color: transparent; }
  .issues { display: flex; flex-direction: column; gap: 6px; }
  .issue { display: flex; align-items: center; gap: 10px; flex-wrap: wrap; padding: 8px 10px; border-radius: 8px; background: color-mix(in srgb, var(--danger) 12%, transparent); border-left: 3px solid var(--danger); font-size: 13px; }
  .issue.soft { background: color-mix(in srgb, var(--warn) 14%, transparent); border-left-color: var(--warn); }
  .issue-text { flex: 1; min-width: 200px; }
  .number { font-family: var(--font-display); font-size: 12px; color: var(--fg-quiet); text-align: center; background: var(--panel); border: 1px solid var(--line); border-radius: 5px; height: 26px; padding: 0; cursor: pointer; }
  .number:hover { background: var(--hover); } .number.on { background: var(--accent-soft); color: var(--accent); border-color: transparent; }
  .nudge { display: flex; gap: 2px; }
  .nudge button { width: 24px; height: 26px; padding: 0; background: var(--panel); border: 1px solid var(--line); border-radius: 5px; color: var(--fg); font: inherit; cursor: pointer; }
  .nudge button:hover { background: var(--hover); }
  .time-box { font-family: var(--font-display); font-size: 12px; text-align: center; padding: 4px 4px; }
  .remove { width: 26px; height: 26px; padding: 0; background: none; border: 0; border-radius: 5px; color: var(--fg-quiet); cursor: pointer; }
  .remove:hover { background: var(--hover); color: var(--danger); }
</style>
