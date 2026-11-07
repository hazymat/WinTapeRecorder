<script lang="ts">
  import { request, sendToShell } from '../bridge'
  import { app, nav, toast, formatTime, formatLength, writeSetting, readSetting } from '../state.svelte'
  import LevelMeter from '../parts/LevelMeter.svelte'
  import CutoffInfo from '../parts/CutoffInfo.svelte'
  import Dropdown from '../parts/Dropdown.svelte'
  import Dialog from '../parts/Dialog.svelte'
  import Hint from '../parts/Hint.svelte'
  import Icon from '../parts/Icon.svelte'
  import { selection, selectKeys, clearSelection, type SelectionAction } from '../lib/selection.svelte'
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
  let liveScale = $state(1) // Ctrl+wheel: wave height, as in the session's waveform; kept while zooming
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
  export function catchUp() { followSeconds = 60; liveScale = 1; following = true; selected = -1; dragging = null; drawLive() }
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
  // The selected marker pulses (as a grabbed edge does in Albums & cuts), so it's plain it's still selected
  let lastLiveDraw = 0
  const pulse = () => 0.5 + 0.5 * Math.sin(performance.now() / 1200 * Math.PI * 2)
  let drawing = false
  async function drawLive() {
    const status = app.status
    if (!liveCanvas || !status?.recording || !status.sampleRate || drawing) return
    drawing = true
    try {
      const rate = status.sampleRate, recorded = app.levels.seconds * rate, { start, span } = liveRange()
      const data = await request<{ mode: string; start?: number; channels: number[][] }>({ type: 'peaks', id: status.sessionId, start: Math.max(0, start), end: Math.max(start + span, 1), columns: Math.round(liveWidth) })
      if (!liveCanvas || (data.mode !== 'peaks' && data.mode !== 'samples')) return
      const now = liveRange() // the view may have moved while the peaks were on their way
      const ratio = devicePixelRatio, height = liveHeight, context = liveCanvas.getContext('2d')!
      if (liveCanvas.width !== Math.round(liveWidth * ratio)) { liveCanvas.width = Math.round(liveWidth * ratio); liveCanvas.height = height * ratio }
      context.setTransform(ratio, 0, 0, ratio, 0, 0); context.clearRect(0, 0, liveWidth, height)
      const style = getComputedStyle(document.documentElement), colour = (name: string, fallback: string) => style.getPropertyValue(name).trim() || fallback
      const toX = (frame: number) => (frame - now.start) / now.span * liveWidth
      // The audio before the start (a view starting before 0) is simply blank
      const dataStart = Math.max(0, start), lanes = data.channels.length, laneHeight = height / lanes, columns = data.channels[0]?.length / 2 || 0, framesPerColumn = (start + span - dataStart) / columns
      context.fillStyle = colour('--wave', '#5ccfff')
      // Zoomed right in: the samples themselves as a line, as in Albums & cuts
      if (data.mode === 'samples') data.channels.forEach((samples, lane) => {
        const middle = laneHeight * (lane + 0.5), half = laneHeight / 2 - 1, first = data.start ?? dataStart
        context.strokeStyle = colour('--line', 'rgba(255,255,255,0.15)'); context.lineWidth = 1; context.beginPath(); context.moveTo(0, middle); context.lineTo(liveWidth, middle); context.stroke()
        context.strokeStyle = colour('--wave', '#5ccfff'); context.lineWidth = 1.4; context.beginPath()
        samples.forEach((sample, index) => { const x = toX(first + index), y = middle - Math.max(-1, Math.min(1, sample * liveScale)) * half; if (index === 0) context.moveTo(x, y); else context.lineTo(x, y) })
        context.stroke()
        if (now.span / liveWidth < 0.34) { context.fillStyle = colour('--wave', '#5ccfff'); samples.forEach((sample, index) => { const x = toX(first + index), y = middle - Math.max(-1, Math.min(1, sample * liveScale)) * half; context.fillRect(x - 1.5, y - 1.5, 3, 3) }) }
      })
      else data.channels.forEach((values, lane) => { const middle = laneHeight * (lane + 0.5); for (let column = 0; column < columns; column++) { const at = dataStart + column * framesPerColumn; if (at > recorded) break; const x = toX(at), low = Math.max(-1, values[column * 2] * liveScale), high = Math.min(1, values[column * 2 + 1] * liveScale); context.fillRect(x, middle - high * (laneHeight / 2 - 1), Math.max(1, liveWidth * framesPerColumn / now.span), Math.max(1, (high - low) * (laneHeight / 2 - 1))) } })
      // What's still to come, and where "now" is
      context.fillStyle = colour('--panel-strong', 'rgba(255,255,255,0.1)'); const nowX = toX(recorded); if (nowX < liveWidth) context.fillRect(Math.max(0, nowX), 0, liveWidth - Math.max(0, nowX), height)
      drawStrip(now.start, now.span, recorded, rate, colour)
      // Markers: the selected one in the accent colour with a band; the hovered one brighter
      context.font = '10.5px "Segoe UI Variable Text", "Segoe UI", sans-serif'; context.textBaseline = 'top'
      status.changes.forEach((change, index) => {
        const x = markerX(index); if (x < -2 || x > liveWidth + 2) return
        const isSelected = index === selected, isHovered = index === hovered && !isSelected
        context.fillStyle = colour('--warn', '#ffb74d')
        if (isSelected || isHovered) { context.globalAlpha = isSelected ? 0.1 + 0.3 * pulse() : 0.12; context.fillRect(x - 6, 0, 12, height); context.globalAlpha = 1 }
        context.fillRect(x - 1, 0, isSelected || isHovered ? 3 : 2, height)
        context.fillStyle = change.confirmed ? '#3ecf6e' : context.fillStyle; context.fillRect(x, 0, 11, 13) // the flag: drag it to move the marker, double-click it to confirm the cut
        if (change.confirmed) { context.fillStyle = '#fff'; context.fillText('✓', x + 1, 1) }
        context.fillStyle = colour('--warn', '#ffb74d')
        context.fillText(`${index + 1} ${change.title || ''}`.trim(), x + 15, 3)
      })
      if (liveScale > 1.05) { context.fillStyle = colour('--fg-quiet', '#888'); context.textBaseline = 'top'; context.fillText(`Height ×${liveScale < 10 ? liveScale.toFixed(1) : Math.round(liveScale)} (Ctrl+wheel)`, liveWidth - 120, 3) }
      // The time scale
      context.fillStyle = colour('--fg-quiet', '#888'); context.textBaseline = 'bottom'; context.font = '10px "Segoe UI Variable Text", "Segoe UI", sans-serif'
      const stepSeconds = [0.1, 0.2, 0.5, 1, 2, 5, 10, 30, 60, 120, 300, 600, 900, 1800].find(step => step * rate / now.span * liveWidth >= 70) ?? 1800
      for (let second = Math.ceil(Math.max(0, now.start) / rate / stepSeconds) * stepSeconds; second * rate < now.start + now.span; second += stepSeconds) { const x = toX(second * rate); context.fillRect(x, height - 10, 1, 4); context.fillText(stepSeconds < 1 ? `${formatTime(second * rate, rate, false)}.${Math.round((second % 1) * 10)}` : formatTime(second * rate, rate, false), x + 3, height - 1) }
    } catch { } finally { drawing = false }
  }
  $effect(() => { if (recording) { liveTimer = window.setInterval(() => { if (selected >= 0 || performance.now() - lastLiveDraw > 240) { lastLiveDraw = performance.now(); drawLive() } }, 80); return () => clearInterval(liveTimer) } else { following = true; selected = -1; overviewSpan = 0; dragging = null } })
  // A marker's line or flag drags the marker itself; anywhere else drags the view, or the audio under the selected marker (SPEC.md › Recording › Live editing)
  const flagZone = 16
  let flagDrag: null | { index: number; frame: number; startX: number; moved: boolean; onFlag: boolean } = null
  let lastFlagClick: null | { index: number; at: number } = null
  function onLiveDown(event: PointerEvent) {
    if (event.button !== 0 || !app.status) return
    const onMarker = markerAt(event.offsetX)
    if (onMarker >= 0) { pause(); flagDrag = { index: onMarker, frame: app.status.changes[onMarker].frame, startX: event.offsetX, moved: false, onFlag: event.offsetY < flagZone }; liveCanvas?.setPointerCapture(event.pointerId); return }
    pause()
    dragging = { startX: event.offsetX, startViewStart: viewStart, pinX: selected >= 0 ? markerX(selected) : 0, moved: false, onMarker }
    liveCanvas?.setPointerCapture(event.pointerId)
  }
  function onLiveMove(event: PointerEvent) {
    if (flagDrag && app.status) { if (!flagDrag.moved && Math.abs(event.offsetX - flagDrag.startX) < 3) return; flagDrag.moved = true; flagDrag.frame = Math.max(0, Math.min(app.levels.seconds * app.status.sampleRate, viewStart + event.offsetX * viewSpan / liveWidth)); if (liveCanvas) liveCanvas.style.cursor = 'ew-resize'; drawLive(); return }
    if (!dragging) {
      const index = markerAt(event.offsetX)
      if (index !== hovered) { hovered = index; drawLive() }
      if (liveCanvas) liveCanvas.style.cursor = index >= 0 ? 'ew-resize' : 'grab'
      if (index >= 0) { liveCanvas?.setAttribute('title', `Drag to move marker ${index + 1}; click to zoom in on it${event.offsetY < flagZone ? `; double-click the flag to ${app.status?.changes[index]?.confirmed ? 'undo the confirmation' : 'confirm this cut'}` : ''}`); return }
      liveCanvas?.setAttribute('title', selected >= 0 ? `Marker ${selected + 1} is selected: drag the audio under it, or drag the marker itself; Esc lets go` : following ? 'Drag to stop following and look around; the wheel zooms, Ctrl+wheel sets the wave height' : 'Drag to pan, wheel to zoom, Ctrl+wheel for height')
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
    if (flagDrag) {
      const done = flagDrag; flagDrag = null
      const moved = done.moved && Math.abs(done.frame - (app.status?.changes[done.index]?.frame ?? done.frame)) > 1
      if (moved) { sendToShell({ type: 'editChange', index: done.index, frame: Math.round(done.frame) }); lastFlagClick = null; selected = done.index }
      else if (done.moved) { }
      // A click on the line (below the flag) zooms in on that marker and selects it; again lets go
      else if (!done.onFlag) { if (selected === done.index) deselect(); else selectMarker(done.index, true); lastFlagClick = null }
      // Two clicks on the same flag within 400 ms (without moving it) confirm the cut, or undo that
      else if (lastFlagClick && lastFlagClick.index === done.index && performance.now() - lastFlagClick.at < 400) { sendToShell({ type: 'editChanges', indexes: [done.index], confirmed: !app.status?.changes[done.index]?.confirmed }); lastFlagClick = null }
      else lastFlagClick = { index: done.index, at: performance.now() }
      drawLive(); return
    }
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
    viewSpan = Math.max(liveWidth / 24, Math.min(Math.max(overviewSpan, 60 * rate) * 1.5, viewSpan * factor))
    viewStart = anchorFrame - anchorX * viewSpan / liveWidth
    drawLive()
  }
  $effect(() => { const canvas = liveCanvas; if (!canvas) return; canvas.addEventListener('wheel', onLiveWheel, { passive: false }); return () => canvas.removeEventListener('wheel', onLiveWheel) })
  function onLiveKey(event: KeyboardEvent) {
    if (event.key !== 'Escape' || !recording || (event.target as HTMLElement)?.matches?.('input, textarea')) return
    if (selected >= 0) { event.preventDefault(); deselect() } else if (!following || liveScale > 1.05 || followSeconds !== 60) { event.preventDefault(); catchUp() }
  }

  function nudge(index: number, frame: number, seconds: number) { sendToShell({ type: 'editChange', index, frame: Math.max(0, frame + seconds * (app.status?.sampleRate ?? 48000)) }) }
  function setTime(index: number, text: string, rate: number) {
    const parts = text.trim().split(':').map(Number)
    if (parts.some(part => Number.isNaN(part))) return
    const seconds = parts.reduce((total, part) => total * 60 + part, 0)
    sendToShell({ type: 'editChange', index, frame: Math.round(seconds * rate) })
  }
  // Issues while recording (SPEC.md › Recording › Issues): each one names the track it hit and what to do about it
  type Issue = { kind: 'dropout' | 'gap' | 'clipping' | 'noname' | 'retake' | 'silence' | 'other'; frame: number; index: number; text: string; action: string }
  const issues = $derived.by<Issue[]>(() => {
    const status = app.status; if (!status?.recording) return []
    const rate = status.sampleRate || 48000, recorded = app.levels.seconds * rate
    const trackAt = (frame: number) => { let found = -1; status.changes.forEach((change, index) => { if (change.frame <= frame) found = index }); return found }
    // The take an issue hit, by its id: -2 when that take has since been removed (the issue goes with it)
    const ownerOf = (item: { frame: number; changeId?: string }) => { if (!item.changeId) return trackAt(item.frame); return status.changes.findIndex(change => change.id === item.changeId) }
    const name = (index: number) => { const change = status.changes[index]; return change?.title ? `“${change.title}”` : index >= 0 ? `track ${index + 1}` : 'the recording' }
    const list: Issue[] = []
    for (const dropout of status.dropoutList ?? []) { const index = ownerOf(dropout); if (index < 0 && dropout.changeId) continue; list.push({ kind: 'dropout', frame: dropout.frame, index, text: `Dropout at ${formatTime(dropout.frame, rate, false)} in ${name(index)}: about ${dropout.ms} ms lost.`, action: 'Play that track again; the new take replaces this one.' }) }
    for (const gap of status.gapList ?? []) { const index = ownerOf(gap); if (index < 0 && gap.changeId) continue; list.push({ kind: 'gap', frame: gap.frame, index, text: `The sound cut out at ${formatTime(gap.frame, rate, false)} in ${name(index)} for ${gap.ms < 1000 ? gap.ms + ' ms' : (gap.ms / 1000).toFixed(1) + ' s'}: the player stalled (a busy PC?).`, action: 'The gap is in the recording: play that track again; the new take replaces this one.' }) }
    for (const other of status.otherSounds ?? []) { const index = ownerOf(other); if (index < 0 && other.changeId) continue; list.push({ kind: 'other', frame: other.frame, index, text: `${other.app} made a sound at ${formatTime(other.frame, rate, false)} in ${name(index)} (${(other.ms / 1000).toFixed(1)} s, peak ${other.peakDb} dB).`, action: 'It is in the recording: play that track again, or turn on Do not disturb so it does not happen again.' }) }
    for (const clip of status.clips ?? []) { const index = ownerOf(clip); if (index < 0 && clip.changeId) continue; list.push({ kind: 'clipping', frame: clip.frame, index, text: `Clipping at ${formatTime(clip.frame, rate, false)} in ${name(index)}: ${clip.samples} samples at full scale.`, action: 'Turn the source down a little and play that track again.' }) }
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
  let summaryOpen = $state(readSetting('recordSummaryOpen', '1') === '1')
  // Album panels in the track list fold away one by one; all open to start with
  let foldedAlbums = $state<string[]>([])
  const foldKey = (group: { key: string; indexes: number[] }) => group.key + ':' + group.indexes[0]
  function toggleFold(group: { key: string; indexes: number[] }) { const key = foldKey(group); foldedAlbums = foldedAlbums.includes(key) ? foldedAlbums.filter(other => other !== key) : [...foldedAlbums, key] }
  // Removing a marker between two others: the track before runs on, the next one starts earlier, or they meet in the middle
  let removing = $state(-1)
  // A take marked as faulty is replaced by a later take of the same track (same title and artist): left out on export by itself
  const faulty = (problem: string) => problem !== '' && problem !== 'note' && problem !== 'skip'
  function replacedBy(index: number) {
    const changes = app.status?.changes ?? [], change = changes[index]
    if (!change || !faulty(change.problem) || !change.title) return -1
    const same = (text: string | undefined, other: string | undefined) => (text ?? '').trim().toLowerCase() === (other ?? '').trim().toLowerCase()
    return changes.findIndex((later, at) => at > index && same(later.title, change.title) && same(later.artist, change.artist))
  }
  function removeMarker(index: number) { const count = app.status?.changes.length ?? 0; if ((index > 0 && index < count - 1) || faulty(app.status?.changes[index]?.problem ?? '')) removing = index; else sendToShell({ type: 'removeChange', index }) }
  function closeGap(how: 'previous' | 'next' | 'middle') {
    const index = removing; removing = -1
    const changes = app.status?.changes ?? [], here = changes[index]?.frame, next = changes[index + 1]?.frame
    if (how !== 'previous' && here != null && next != null) sendToShell({ type: 'editChange', index: index + 1, frame: Math.round(how === 'next' ? here : (here + next) / 2) })
    sendToShell({ type: 'removeChange', index })
  }
  // Quiet PC check: read when the page opens and whenever the window comes back to the front (after changing Settings)
  let quiet = $state<null | { soundsOff: boolean; notificationsOff: boolean }>(null)
  function checkQuiet() { request<{ soundsOff: boolean; notificationsOff: boolean }>({ type: 'quietCheck' }).then(found => quiet = found).catch(() => { }) }
  $effect(() => { checkQuiet(); const again = () => checkQuiet(); window.addEventListener('focus', again); return () => window.removeEventListener('focus', again) })
  // The track list: the same columns as Albums & cuts, minus artwork, source and fades
  const recColumns = '48px 150px minmax(80px, 1fr) minmax(80px, 1fr) 60px 36px minmax(110px, 1.4fr) minmax(70px, 0.7fr) minmax(70px, 0.7fr) 72px 26px'
  // Ticked tracks (by marker index) act together through the bar at the bottom
  const ticked = $derived(selection.scope === 'record' ? selection.keys : [])
  const recActions: SelectionAction[] = [
    { label: 'Problem…', single: true, run: keys => { problemFor = Number(keys[0]); problemNote = changes[problemFor]?.note ?? '' } },
    { label: 'Remove markers', key: 'Delete', danger: true, run: keys => { for (const key of keys.map(Number).sort((first, second) => second - first)) sendToShell({ type: 'removeChange', index: key }); clearSelection() } },
  ]
  function tickIndexes(indexes: number[], on: boolean) { const keys = new Set(ticked); for (const index of indexes) { if (on) keys.add(String(index)); else keys.delete(String(index)) } if (keys.size) selectKeys('record', Array.from(keys).sort((first, second) => Number(first) - Number(second)), recActions, 'track'); else clearSelection() }
  function tickAll(on: boolean) { tickIndexes(changes.map((_, index) => index), on) }
  // Press and hold a track to pick it up; drop it on another album's header to join that album, or in the gap to give it its own
  let liftingIndex = $state(-1), liftedIndex = $state(-1), dropOn = $state(''), liftTimer = 0, liftStart = { x: 0, y: 0 }
  function liftDown(index: number, event: PointerEvent) {
    if (event.button !== 0 || (event.target as HTMLElement)?.closest?.('button, select, input[type=checkbox]')) return
    liftingIndex = index; liftStart = { x: event.clientX, y: event.clientY }; clearTimeout(liftTimer)
    const cancelOnMove = (move: PointerEvent) => { if (Math.abs(move.clientX - liftStart.x) + Math.abs(move.clientY - liftStart.y) > 5) { clearTimeout(liftTimer); liftingIndex = -1; window.removeEventListener('pointermove', cancelOnMove) } }
    window.addEventListener('pointermove', cancelOnMove)
    liftTimer = window.setTimeout(() => { window.removeEventListener('pointermove', cancelOnMove); (document.activeElement as HTMLElement | null)?.blur?.(); liftedIndex = index; liftingIndex = -1; window.addEventListener('pointermove', liftMove); window.addEventListener('pointerup', liftUp, { once: true }) }, 350)
    window.addEventListener('pointerup', () => { clearTimeout(liftTimer); liftingIndex = -1; window.removeEventListener('pointermove', cancelOnMove) }, { once: true })
  }
  function liftMove(event: PointerEvent) { const target = (document.elementFromPoint(event.clientX, event.clientY) as HTMLElement | null)?.closest?.('[data-drop]') as HTMLElement | null; dropOn = target?.dataset.drop ?? '' }
  function liftUp() {
    window.removeEventListener('pointermove', liftMove)
    const index = liftedIndex, target = dropOn; liftedIndex = -1; dropOn = ''
    if (index < 0 || !target) return
    const change = changes[index]; if (!change) return
    if (target === 'split') { sendToShell({ type: 'editChanges', indexes: [index], album: change.album ? `${change.album} (${change.title || 'single'})` : change.title }); return }
    const group = albums.find(candidate => 'album:' + candidate.indexes[0] === target); if (!group || group.indexes.includes(index)) return
    // Straight in, with that album's names; the track keeps its own artist unless the album has no name
    sendToShell({ type: 'editChanges', indexes: [index], album: group.album, year: group.year, genre: group.genre, composer: group.composer, ...(group.album ? {} : { artist: group.artist }) })
  }
  // Problem with a track (SPEC.md › Recording › Problems)
  let problemFor = $state(-1), problemNote = $state('')
  const problemLabel = (problem: string) => problem === 'note' ? 'Noted' : problem === 'skip' ? 'Left out' : problem === 'dropout' ? 'Dropout' : problem === 'gap' ? 'Cut out' : problem === 'clipping' ? 'Clipping' : 'Retake'
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
  type Album = { key: string; artist: string; album: string; year: string; genre: string; composer: string; indexes: number[] }
  const loose = (text: string) => (text ?? '').toLowerCase().replace(/[^\p{L}\p{N}]/gu, '')
  const changes = $derived<Change[]>(app.status?.changes ?? [])
  const albums = $derived.by(() => {
    const list: Album[] = []
    changes.forEach((change, index) => {
      const key = change.album ? 'album:' + loose(change.album) : 'artist:' + loose(change.artist), last = list[list.length - 1] // by album, so a featured artist doesn't split it
      if (last && last.key === key) { last.indexes.push(index); if (!last.year && change.year) last.year = change.year; if (!last.genre && change.genre) last.genre = change.genre; if (!last.composer && change.composer) last.composer = change.composer }
      else list.push({ key, artist: change.artist, album: change.album, year: change.year ?? '', genre: change.genre ?? '', composer: change.composer ?? '', indexes: [index] })
    })
    return list
  })
  function trackNumber(group: Album, index: number) { if (changes[index]?.problem) return 0; return group.indexes.filter(other => !changes[other]?.problem).indexOf(index) + 1 }
  let openAlbum = $state('') // the album card showing its tracks
  function hue(text: string) { let hash = 0; for (const char of text) hash = (hash * 31 + char.charCodeAt(0)) | 0; return Math.abs(hash) % 360 }
  function initials(group: Album) { return ((group.album || group.artist || '?').match(/\p{L}|\p{N}/gu) ?? ['?']).slice(0, 2).join('').toUpperCase() }
  function showAlbum(group: Album) { openAlbum = openAlbum === group.key + group.indexes[0] ? '' : group.key + group.indexes[0]; document.getElementById('album-' + group.indexes[0])?.scrollIntoView({ block: 'nearest', behavior: 'smooth' }) }
  // An album's artist, name or year, for all its tracks at once
  function editAlbum(group: Album, field: 'artist' | 'album' | 'year' | 'genre' | 'composer', value: string) { sendToShell({ type: 'editChanges', indexes: group.indexes, [field]: value.trim() }) }
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
  {#if !recording && quiet}
    <!-- Is the PC quiet? Recording what it plays records every ding too (SPEC.md › Recording › Other sounds) -->
    <div class="quiet-check">
      <span class="quiet-item" class:ok={quiet.soundsOff}>{quiet.soundsOff ? '✓ Windows sounds are off' : '⚠ Windows sounds are on'}{#if !quiet.soundsOff}<button class="ghost small-button" onclick={() => sendToShell({ type: 'openQuietSettings', which: 'sounds' })} title="Opens Sound settings: set Sound Scheme to No Sounds">Turn off…</button>{/if}</span>
      <span class="quiet-item" class:ok={quiet.notificationsOff}>{quiet.notificationsOff ? '✓ Do not disturb is on' : '⚠ Notifications can pop up and ding'}{#if !quiet.notificationsOff}<button class="ghost small-button" onclick={() => sendToShell({ type: 'openQuietSettings', which: 'notifications' })} title="Opens Notifications settings: switch on Do not disturb at the top">Do not disturb…</button>{/if}</span>
      <span class="quiet small">Other apps' sounds are still flagged in Issues while recording.</span>
    </div>
  {/if}
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

  <!-- Summary (SPEC.md › Recording › Summary): source, what's playing and the albums so far, folded away with one click -->
  <section class="summary-panel" class:folded={!summaryOpen}>
    <button class="summary-toggle" onclick={() => { summaryOpen = !summaryOpen; writeSetting('recordSummaryOpen', summaryOpen ? '1' : '0') }} aria-expanded={summaryOpen}><span class="chev">{summaryOpen ? '▾' : '▸'}</span>Summary{#if !summaryOpen && currentSource}<span class="quiet summary-peek">{currentSource.artist ? currentSource.artist + ' - ' : ''}{currentSource.title}</span>{/if}</button>
    {#if summaryOpen}
  {#if recording}
    <!-- One line each while recording (SPEC.md › Recording › Compact view): only what the source gives, in the order artist, album, year, title -->
    <p class="line"><span class="line-label">Source</span>{#if app.status?.cutoffHz}<CutoffInfo hz={app.status.cutoffHz} text={app.status.cutoffText} /><span class="quiet">{' · '}{app.status.cutoffText}</span>{:else}<span class="quiet">measuring…</span>{/if}</p>
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
    {#if albums.length}
      <div class="summary-sub">Albums so far</div>
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
  {:else}
    <h3>Source quality</h3>
    <p class="source">{#if app.status?.cutoffHz}<CutoffInfo hz={app.status.cutoffHz} text={app.status.cutoffText} capital /><span class="quiet">{' · '}{app.status.cutoffText}</span>{:else}<span class="quiet">{app.status?.capturing ? 'Measuring: needs a couple of seconds of sound.' : 'Measured from the sound as it arrives.'}</span>{/if}</p>
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
    {/if}
  </section>

  {#if recording}

    <div class="live-head">
      {#if selected >= 0}<span><strong>Marker {selected + 1} selected</strong></span>
      {:else if !following}<span><strong>Paused view</strong><span class="quiet">{' · '}recording carries on</span></span>
      {:else}<span><strong>Following</strong><span class="quiet">{' · '}the last minute</span></span>{/if}
      {#if !following || liveScale > 1.05 || followSeconds !== 60}<button class="primary catch-up" onclick={catchUp} title="Back to following the last minute at normal height (Esc)">Catch up</button>{/if}
    </div>
    <Hint id="live-wave" text="Drag or zoom (wheel) the wave to stop following and look around; Ctrl+wheel sets its height. Click a marker to select it, then drag the audio under it and let go to place it; click it again or press Esc to let go. Catch up goes back to the last minute at normal height. The strip above is the whole recording: click it to look there." />
    <canvas class="live-strip" bind:this={stripCanvas} title="The whole recording so far: click or drag to look there" onpointerdown={onStrip} onpointermove={onStrip}></canvas>
    <canvas class="live-wave" onclick={(event) => { if (event.detail === 2 && event.offsetY < flagZone) { const index = markerAt(event.offsetX); if (index >= 0) { sendToShell({ type: 'editChanges', indexes: [index], confirmed: !app.status?.changes[index]?.confirmed }); lastFlagClick = null } } }} bind:this={liveCanvas} bind:clientWidth={liveWidth} tabindex="-1" onpointerdown={onLiveDown} onpointermove={onLiveMove} onpointerup={onLiveUp} onpointercancel={onLiveUp} onpointerleave={() => { if (!dragging && hovered >= 0) { hovered = -1; drawLive() } }}></canvas>

    <div class="tracks-head">
      <h3>Tracks marked so far</h3>
      <Dropdown label="Track names" value={source} options={sourceChoices} align="right" onchange={(value) => source = value} />
    </div>
    <Hint id="live-edit-albums" text="Tracks are grouped by album; numbers count up by themselves. Edit an album's artist, name or year in its header to change all its tracks. A track that should belong to the album above or below: drag it by ⋮⋮ onto that album, or use ⤒ / ⤓." />
    <!-- The same list as Albums & cuts (SPEC.md › Recording › Live editing), without auditioning: Start | Length find the line in the wave -->
    <div class="list rec-list" style:--columns={recColumns}>
      <div class="row head"><span><input type="checkbox" class="tick" checked={changes.length > 0 && ticked.length === changes.length} indeterminate={ticked.length > 0 && ticked.length < changes.length} onchange={() => tickAll(ticked.length !== changes.length)} aria-label="Select all tracks" /></span><span class="head-cell">Start · Length</span><span class="head-cell">Artist</span><span class="head-cell">Album</span><span class="head-cell">Year</span><span class="head-cell">No.</span><span class="head-cell">Title</span><span class="head-cell">Genre</span><span class="head-cell">Composer</span><span></span><span></span></div>
      {#each albums as group, groupIndex (group.key + group.indexes[0])}
        {@const allTicked = group.indexes.every(index => ticked.includes(String(index)))}
        <div class="album-row open row-grid" class:over={dropOn === 'album:' + group.indexes[0]} data-drop={'album:' + group.indexes[0]} id={'album-' + group.indexes[0]} role="group" aria-label={albumLine(group)}>
          <span class="album-label"><button class="fold" onclick={() => toggleFold(group)} title={foldedAlbums.includes(foldKey(group)) ? 'Show its tracks' : 'Hide its tracks'} aria-expanded={!foldedAlbums.includes(foldKey(group))}>{foldedAlbums.includes(foldKey(group)) ? '▸' : '▾'}</button><input type="checkbox" class="tick" checked={allTicked} indeterminate={!allTicked && group.indexes.some(index => ticked.includes(String(index)))} onchange={() => tickIndexes(group.indexes, !allTicked)} aria-label="Select this album's tracks" /><span class="album-name"><strong>{group.album || 'No album'}</strong> - {group.artist || 'unknown artist'}{group.year ? ` (${group.year})` : ''}</span><span class="quiet album-count">{group.indexes.length}</span></span>
          <span class="head-field cell-artist"><input value={group.artist} placeholder="Artist" title="This album's artist, for all its tracks (Enter or ↓ fills them)" onchange={(event) => editAlbum(group, 'artist', (event.target as HTMLInputElement).value)} onkeydown={(event) => { if (event.key === 'Enter') { const input = event.target as HTMLInputElement; input.blur(); editAlbum(group, 'artist', input.value) } }} /><button class="fill-down" onclick={(event) => editAlbum(group, 'artist', ((event.currentTarget as HTMLElement).previousElementSibling as HTMLInputElement).value)} title="Put this artist on every track in the album">↓</button></span>
          <span class="head-field cell-album"><input value={group.album} placeholder="Album" title="This album's album, for all its tracks (Enter or ↓ fills them)" onchange={(event) => editAlbum(group, 'album', (event.target as HTMLInputElement).value)} onkeydown={(event) => { if (event.key === 'Enter') { const input = event.target as HTMLInputElement; input.blur(); editAlbum(group, 'album', input.value) } }} /><button class="fill-down" onclick={(event) => editAlbum(group, 'album', ((event.currentTarget as HTMLElement).previousElementSibling as HTMLInputElement).value)} title="Put this album on every track in the album">↓</button></span>
          <span class="head-field cell-year"><input value={group.year} placeholder="Year" title="This album's year, for all its tracks (Enter or ↓ fills them)" onchange={(event) => editAlbum(group, 'year', (event.target as HTMLInputElement).value)} onkeydown={(event) => { if (event.key === 'Enter') { const input = event.target as HTMLInputElement; input.blur(); editAlbum(group, 'year', input.value) } }} /><button class="fill-down" onclick={(event) => editAlbum(group, 'year', ((event.currentTarget as HTMLElement).previousElementSibling as HTMLInputElement).value)} title="Put this year on every track in the album">↓</button></span>
          <span class="head-field cell-genre"><input value={group.genre} placeholder="Genre" title="This album's genre, for all its tracks (Enter or ↓ fills them)" onchange={(event) => editAlbum(group, 'genre', (event.target as HTMLInputElement).value)} onkeydown={(event) => { if (event.key === 'Enter') { const input = event.target as HTMLInputElement; input.blur(); editAlbum(group, 'genre', input.value) } }} /><button class="fill-down" onclick={(event) => editAlbum(group, 'genre', ((event.currentTarget as HTMLElement).previousElementSibling as HTMLInputElement).value)} title="Put this genre on every track in the album">↓</button></span>
          <span class="head-field cell-composer"><input value={group.composer} placeholder="Composer" title="This album's composer, for all its tracks (Enter or ↓ fills them)" onchange={(event) => editAlbum(group, 'composer', (event.target as HTMLInputElement).value)} onkeydown={(event) => { if (event.key === 'Enter') { const input = event.target as HTMLInputElement; input.blur(); editAlbum(group, 'composer', input.value) } }} /><button class="fill-down" onclick={(event) => editAlbum(group, 'composer', ((event.currentTarget as HTMLElement).previousElementSibling as HTMLInputElement).value)} title="Put this composer on every track in the album">↓</button></span>
        </div>
        {#if !foldedAlbums.includes(foldKey(group))}
        {#each group.indexes as index (index + ':' + changes[index].frame)}
          {@const change = changes[index]}
          {@const rate = app.status?.sampleRate ?? 48000}
          {@const nextFrame = changes[index + 1]?.frame ?? app.levels.seconds * rate}
          <div class="row" class:replaced={replacedBy(index) >= 0} class:lifting={liftingIndex === index} class:lifted={liftedIndex === index} class:current={selected === index} onpointerdown={(event) => liftDown(index, event)} role="listitem">
            <span class="tick-cell"><input type="checkbox" class="tick" checked={ticked.includes(String(index))} onchange={() => tickIndexes([index], !ticked.includes(String(index)))} aria-label="Select track {index + 1}" /> {index + 1}</span>
            <span class="hear"><button class="hear-start locate" class:grabbed={selected === index} class:checked={change.confirmed} onclick={() => selected === index ? deselect() : selectMarker(index, true)} title="Find this start in the wave above, zoomed in (click again to let go)"><span class="pill-time">{formatTime(change.frame, rate, false)}</span>{#if change.detectedFrame != null && change.detectedFrame >= 0 && change.detectedFrame !== change.frame}<span class="pill-revert" role="button" tabindex="-1" onclick={(event) => { event.stopPropagation(); sendToShell({ type: 'editChange', index, frame: change.detectedFrame }) }} onkeydown={() => { }} title={`Moved. Click to put it back where it was placed (${formatTime(change.detectedFrame, rate, false)})`}>↺</span>{/if}<span class="locate-icon">⌖</span></button><button class="hear-end locate" onclick={() => selectMarker(index, true)} title="Length so far"><span class="pill-time">{formatLength(Math.max(0, nextFrame - change.frame), rate)}</span></button></span>
            <input value={change.artist} placeholder="Artist" onchange={(event) => sendToShell({ type: 'editChange', index, artist: (event.target as HTMLInputElement).value.trim() })} />
            <input value={change.album} placeholder="Album" onchange={(event) => sendToShell({ type: 'editChange', index, album: (event.target as HTMLInputElement).value.trim() })} />
            <input value={change.year ?? ''} placeholder={group.year || 'Year'} onchange={(event) => sendToShell({ type: 'editChange', index, year: (event.target as HTMLInputElement).value.trim() })} />
            <span class="track-no" title="Track number: counts up within the album">{trackNumber(group, index) || '-'}</span>
            <input value={change.title} placeholder={change.manual ? 'Title (marked by hand)' : 'Title'} onchange={(event) => sendToShell({ type: 'editChange', index, title: (event.target as HTMLInputElement).value.trim() })} />
            <input value={change.genre ?? ''} placeholder={group.genre || 'Genre'} onchange={(event) => sendToShell({ type: 'editChanges', indexes: [index], genre: (event.target as HTMLInputElement).value.trim() })} />
            <input value={change.composer ?? ''} placeholder={group.composer || 'Composer'} onchange={(event) => sendToShell({ type: 'editChanges', indexes: [index], composer: (event.target as HTMLInputElement).value.trim() })} />
            <button class="retake" class:on={!!change.problem} onclick={() => { problemFor = index; problemNote = change.note ?? '' }} title={change.problem ? `${problemLabel(change.problem)}${change.note ? ': ' + change.note : ''}. Click to change it` : 'Problem with this track? Note it, play it again or leave it out'}>{change.problem ? (replacedBy(index) >= 0 ? `Replaced by ${replacedBy(index) + 1}` : problemLabel(change.problem)) : 'Problem?'}</button>
            <button class="remove" onclick={() => removeMarker(index)} title="Remove this marker (the audio stays)" aria-label="Remove marker">✕</button>
          </div>
          {#if liftedIndex >= 0 && group.indexes.includes(liftedIndex) && group.indexes[group.indexes.length - 1] === index}<div class="split-zone" data-drop="split" class:over={dropOn === 'split'}>Drop here to give it an album of its own</div>{/if}
        {/each}
        {/if}
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

{#if removing >= 0}
  <div class="backdrop" role="presentation" onpointerdown={(event) => { if (event.target === event.currentTarget) removing = -1 }}>
    <div class="gap-box" role="dialog" aria-label="Remove this track">
      {#if faulty(app.status?.changes[removing]?.problem ?? '')}
        <h3>Track {removing + 1} is a faulty take</h3>
        <p class="quiet gap-note">{replacedBy(removing) >= 0 ? `Track ${replacedBy(removing) + 1} is the new take, so this one is already left out of the export.` : 'Once you play it again, the new take replaces it and this one is left out by itself.'} There's no need to remove it, and removing it would hand its audio to a neighbour.</p>
        <button class="primary gap-choice" onclick={() => { const index = removing; removing = -1; sendToShell({ type: 'setProblem', index, problem: 'skip' }) }}>Leave this take out now</button>
        <button class="ghost gap-choice" onclick={() => removing = -1}>Keep it as it is</button>
        {#if removing > 0 && removing < (app.status?.changes.length ?? 0) - 1}<div class="quiet gap-note">Or remove the marker anyway:</div>{/if}
      {:else}
      <h3>Remove track {removing + 1}: how should its neighbours meet?</h3>
      {/if}
      {#if removing > 0 && removing < (app.status?.changes.length ?? 0) - 1}
      <button class="ghost gap-choice" onclick={() => closeGap('previous')}>Track {removing} runs on until track {removing + 2} starts</button>
      <button class="ghost gap-choice" onclick={() => closeGap('next')}>Track {removing + 2} starts where track {removing} ends now</button>
      <button class="ghost gap-choice" onclick={() => closeGap('middle')}>They meet in the middle</button>
      {:else}<button class="ghost gap-choice" onclick={() => { const index = removing; removing = -1; sendToShell({ type: 'removeChange', index }) }}>Remove the marker anyway</button>{/if}
      <div class="gap-foot"><button class="ghost" onclick={() => removing = -1}>Cancel</button></div>
    </div>
  </div>
{/if}

<style>
  .gap-box { width: min(460px, calc(100vw - 40px)); display: flex; flex-direction: column; gap: 6px; padding: 16px 18px 14px; border-radius: 14px; background: var(--panel-solid); border: 1px solid var(--line); box-shadow: 0 24px 60px var(--shadow); }
  .gap-box h3 { margin: 0 0 6px; font-size: 15px; text-transform: none; letter-spacing: 0; color: var(--fg); }
  .gap-choice { text-align: left; padding: 7px 12px; }
  .gap-note { margin: 0 0 4px; font-size: 12.5px; }
  .row.replaced { opacity: 0.55; }
  .replaced-tag { font-size: 11px; color: var(--fg-quiet); white-space: nowrap; }
  .gap-foot { display: flex; justify-content: flex-end; margin-top: 4px; }

  .record { padding-bottom: 20px; }
  .record.recording { height: 100%; display: flex; flex-direction: column; padding-bottom: 0; }
  .record.recording > * { flex: none; }
  .record.recording > .rec-list { flex: 1 1 auto; min-height: 140px; overflow-y: auto; }
  .summary-panel { border: 1px solid var(--line); border-radius: 10px; background: var(--panel); padding: 4px 12px 8px; margin: 6px 0 8px; }
  .summary-panel.folded { padding-bottom: 4px; }
  .summary-toggle { display: flex; align-items: center; gap: 8px; width: 100%; min-width: 0; padding: 4px 0; border: 0; background: transparent; color: var(--fg); font: inherit; font-weight: 600; font-size: 13px; cursor: pointer; text-align: left; }
  .summary-toggle .chev, .fold { color: var(--fg-quiet); width: 14px; text-align: center; }
  .summary-peek { font-weight: 400; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; min-width: 0; }
  .summary-sub { font-size: 11px; text-transform: uppercase; letter-spacing: 0.05em; color: var(--fg-quiet); margin: 8px 0 4px; }
  .fold { flex: none; border: 0; background: transparent; padding: 0; margin-right: 4px; cursor: pointer; font-size: 12px; }
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
  .row { display: grid; grid-template-columns: var(--columns); gap: 8px; align-items: center; padding: 0 8px; border-radius: 4px; cursor: default; font-size: 12.5px; min-height: 26px; }
  .row:not(.head):hover { background: var(--hover); }
  .row.current { box-shadow: inset 3px 0 0 var(--accent); }
  .number, .time, .fades { font-family: var(--font-display); font-size: 12px; color: var(--fg-quiet); }
  .row input { background: transparent; border-color: transparent; padding: 1px 6px; min-width: 0; font-size: 12.5px; }
  .row input:hover { border-color: var(--line); }
  .row input:focus { background: var(--panel); }
  .hear { display: flex; gap: 0; white-space: nowrap; }
  .hear .hear-start, .hear .hear-end { width: 82px; padding: 0 6px; justify-content: space-between; font-family: var(--font-display); font-weight: 500; }
  .hear .hear-start.doubt, .hear .hear-end.doubt { color: var(--warn); }
  .pill-time { font-size: 12px; }
  .head-cell { position: relative; display: block; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .col-resize { position: absolute; top: -4px; right: -6px; width: 9px; bottom: -4px; cursor: col-resize; z-index: 2; }
  .col-resize::after { content: ''; position: absolute; left: 4px; top: 6px; bottom: 6px; width: 1px; background: var(--line); }
  .col-resize:hover::after { background: var(--accent); width: 2px; }
  .hear button { display: inline-flex; align-items: center; justify-content: center; gap: 4px; height: 20px; padding: 0 9px; font: inherit; font-size: 11.5px; font-weight: 600; cursor: pointer; white-space: nowrap; background: var(--accent-soft); color: var(--accent); border: 1px solid color-mix(in srgb, var(--accent) 40%, transparent); }
  .hear .hear-start { border-radius: 7px 0 0 7px; } .hear .hear-end { border-radius: 0 7px 7px 0; border-left: 0; }
  .hear button:hover { background: var(--accent); color: var(--on-accent); }
  .hear button.checked { background: rgba(62, 207, 110, 0.18); color: #2e9e57; border-color: rgba(62, 207, 110, 0.5); }
  .number-cell { font-family: var(--font-display); font-size: 12px; color: var(--fg-quiet); width: 34px; text-align: center; }
  .album-row.open { border-color: color-mix(in srgb, var(--accent) 50%, var(--line)); cursor: grab; }
  .album-row.row-grid { margin-top: 10px; display: grid; grid-template-columns: var(--columns); gap: 8px; padding: 3px 8px; }
  .row-grid .album-label .album-count { text-transform: none; letter-spacing: 0; font-weight: 400; }
  .row-grid .album-label > .tick { margin: 0 4px 0 0; }
  .row-grid .album-name { text-transform: none; letter-spacing: 0; font-size: 13px; color: var(--fg); overflow: hidden; text-overflow: ellipsis; min-width: 0; }
  .row-grid .album-label { grid-column: 1 / 4; display: flex; gap: 10px; align-items: baseline; white-space: nowrap; overflow: hidden; }
  .row-grid .cell-artist { grid-column: 4; } .row-grid .cell-album { grid-column: 5; } .row-grid .cell-year { grid-column: 6; } .row-grid .cell-genre { grid-column: 9; } .row-grid .cell-composer { grid-column: 10; }
  .head-field { position: relative; display: flex; align-items: center; min-width: 0; }
  .head-field input { flex: 1; min-width: 0; padding-right: 24px !important; }
  .fill-down { position: absolute; right: 2px; top: 50%; transform: translateY(-50%); width: 20px; height: 20px; border-color: transparent !important; background: transparent !important; opacity: 0.6; padding: 0; border-radius: 5px; border: 1px solid var(--line); background: var(--panel); color: var(--fg-quiet); cursor: pointer; font-size: 12px; }
  .fill-down:hover { opacity: 1; background: var(--accent) !important; color: var(--on-accent); }
  .track-number { text-align: center; padding-left: 2px !important; padding-right: 2px !important; }
  .album-row.lifted { opacity: 0.85; box-shadow: 0 8px 22px var(--shadow); cursor: grabbing; }
  .row.lifting { animation: wiggle 350ms ease-in-out; }
  @keyframes wiggle { 0%, 100% { transform: none; } 25% { transform: rotate(-0.4deg); } 75% { transform: rotate(0.4deg); } }
  .row.lifted :global(*) { user-select: none; }
  .row.lifted { opacity: 0.85; box-shadow: 0 8px 22px var(--shadow); transform: scale(1.01); background: var(--panel-solid); cursor: grabbing; position: relative; z-index: 2; }
  .edge-cell { display: flex; align-items: center; gap: 2px; min-width: 0; white-space: nowrap; } /* never wraps: rows keep their shape whatever their state */
  .hear button.heard { box-shadow: inset 0 -3px 0 0 var(--accent); }
  .hear .hear-start.grabbed { outline: 2px solid #3ecf6e; outline-offset: 1px; z-index: 1; }
  .hear .hear-end.grabbed { outline: 2px solid #e5484d; outline-offset: 1px; z-index: 1; }
  .row:has(.skipped) { opacity: 0.55; }
  .tick { width: 15px; height: 15px; margin: 0; accent-color: var(--accent); cursor: pointer; }
  .row:not(.head):hover { background: var(--hover); }
  .rec-list .head { position: sticky; top: 0; z-index: 1; padding: 6px 8px; font-size: 11px; color: var(--fg-quiet); text-transform: uppercase; letter-spacing: 0.05em; background: var(--panel-solid); border-radius: 0; }
  .rec-list .head { top: 0; }
  .rec-list { border-top: 1px solid var(--line); }
  .rec-list .row-grid .album-label { grid-column: 1 / 3; }
  .rec-list .row-grid .cell-artist { grid-column: 3; } .rec-list .row-grid .cell-album { grid-column: 4; } .rec-list .row-grid .cell-year { grid-column: 5; } .rec-list .row-grid .cell-genre { grid-column: 8; } .rec-list .row-grid .cell-composer { grid-column: 9; }
  .rec-list .hear .hear-start, .rec-list .hear .hear-end { width: 70px; }
  .locate-icon { font-size: 11px; opacity: 0.7; }
  .rec-list .album-row.over { background: var(--accent-soft); border-color: var(--accent); }
  .split-zone { margin: 6px 0; padding: 10px; border: 2px dashed var(--line); border-radius: 8px; text-align: center; font-size: 12.5px; color: var(--fg-quiet); }
  .split-zone.over { border-color: var(--accent); color: var(--accent); background: var(--accent-soft); }
  .album-row.row-grid { border-radius: 8px; background: var(--panel); border: 1px solid var(--line); align-items: center; }
  .album-row input { font-weight: 600; padding: 1px 6px; font-size: 12.5px; }
  .track-no { font-family: var(--font-display); font-size: 12px; text-align: center; color: var(--fg-quiet); }
  .quiet-check { display: flex; flex-wrap: wrap; align-items: center; gap: 6px 18px; padding: 6px 12px; margin-bottom: 8px; border-radius: 8px; background: var(--panel); border: 1px solid var(--line); font-size: 12.5px; }
  .quiet-item { display: inline-flex; align-items: center; gap: 8px; color: var(--warn); font-weight: 600; }
  .quiet-item.ok { color: #2e9e57; }
  .small-button { padding: 1px 8px; font-size: 11.5px; height: 22px; }
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
  .catch-up { flex: none; padding-top: 2px; padding-bottom: 2px; } /* the head keeps its height, so the wave never jumps when Catch up appears */
  .live-strip { display: block; width: 100%; height: 26px; border-radius: 6px; background: var(--panel); margin-bottom: 4px; cursor: pointer; touch-action: none; }
  .now-table { border-collapse: collapse; font-size: 13px; margin: 2px 0 6px; }
  .now-table th { text-align: left; font-weight: 500; color: var(--fg-quiet); padding: 2px 16px 2px 0; white-space: nowrap; vertical-align: top; }
  .now-table td { padding: 2px 0; }
  .now-table td.none { color: var(--fg-quiet); font-style: italic; }
  .live-head { height: 32px; box-sizing: border-box; display: flex; align-items: center; justify-content: space-between; gap: 10px; margin: 14px 0 4px; font-size: 12px; }
  .changes { display: flex; flex-direction: column; gap: 3px; }
  /* Rows and album headers share these columns, so an album's artist, album and year sit right above its tracks' */
  .change, .album-head { display: grid; grid-template-columns: 14px 30px 64px minmax(80px, 1fr) minmax(80px, 1fr) 60px 24px minmax(110px, 1.4fr) minmax(70px, 0.7fr) minmax(70px, 0.7fr) 30px 70px 26px; gap: 5px; align-items: center; }
  .album-head .c-artist { grid-column: 4; } .album-head .c-album { grid-column: 5; } .album-head .c-year { grid-column: 6; } .album-head .c-genre { grid-column: 9; } .album-head .c-composer { grid-column: 10; }
  .head-field { position: relative; display: flex; align-items: center; min-width: 0; }
  .head-field input { flex: 1; min-width: 0; padding-right: 22px !important; font-weight: 600; }
  .fill-down { position: absolute; right: 2px; top: 50%; transform: translateY(-50%); width: 18px; height: 18px; padding: 0; border: 0; border-radius: 4px; background: transparent; color: var(--fg-quiet); cursor: pointer; font-size: 11px; opacity: 0.6; }
  .fill-down:hover { opacity: 1; background: var(--accent); color: var(--on-accent); }
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
  .pill-revert { color: var(--warn); font-family: var(--font-ui); font-size: 12px; padding: 0 2px; border-radius: 3px; cursor: pointer; }
  .pill-revert:hover { background: color-mix(in srgb, var(--warn) 25%, transparent); }
  .tick-cell { display: inline-flex; align-items: center; gap: 6px; font-family: var(--font-display); font-size: 12px; color: var(--fg-quiet); }
  .retake { height: 22px; margin: 2px 0; white-space: nowrap; padding: 0 8px; background: var(--panel); border: 1px solid var(--line); border-radius: 5px; color: var(--fg-quiet); font: inherit; font-size: 12px; cursor: pointer; }
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
