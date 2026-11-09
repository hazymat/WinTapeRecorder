<script lang="ts">
  import { untrack } from 'svelte'
  import { request, sendToShell, onShellMessage } from '../bridge'
  import { app, nav, toast, formatTime, formatLength, formatDuration, writeSetting, readSetting, describeCutoff, shortCutoff, medianCutoff, type SessionDetail, type Track, type AlbumMatch } from '../state.svelte'
  import { selection, selectKeys, clearSelection, type SelectionAction, tickKeys } from '../lib/selection.svelte'
  import { selectable } from '../lib/selectable.svelte'
  import Waveform, { type WaveView, type Grab } from '../parts/Waveform.svelte'
  import CoverCompare, { type CompareItem } from '../parts/CoverCompare.svelte'
  import CutoffInfo from '../parts/CutoffInfo.svelte'
  import Icon from '../parts/Icon.svelte'
  import Hint from '../parts/Hint.svelte'
  import Dropdown from '../parts/Dropdown.svelte'
  import Dialog from '../parts/Dialog.svelte'
  import TagEditor, { type TagFields } from '../parts/TagEditor.svelte'
  import LookupDialog, { type TagMatch } from '../parts/LookupDialog.svelte'
  // One session (SPEC.md › Editing, Auditioning, Export): waveform, track list, auditions and export
  let { sessionId }: { sessionId: string } = $props()

  let session = $state<SessionDetail | null>(null)
  let loadError = $state(''), loadProgress = $state(0)
  let tracks = $state<Track[]>([])
  let view = $state<WaveView>({ start: 0, framesPerPixel: 1000 })
  let cursor = $state(0), currentId = $state(''), grab = $state<Grab>(null)
  let autoHeight = $state(readSetting('autoHeight', '1') !== '0')
  // The session's name and details sit in the title-bar row (SPEC.md › Window and look): moved there, removed with the view
  function intoTitleBar(node: HTMLElement) { const target = document.querySelector('.topbar .title'); if (target) target.appendChild(node); return { destroy() { node.remove() } } }
  // Track list columns (SPEC.md › Editing): drag a header's right edge to resize; remembered. Title (index 8) takes the space left
  const columnNames = ['select', 'cut', 'art', 'artist', 'album', 'year', 'number', 'title', 'genre', 'composer', 'source', 'fades', 'status']
  const titleColumn = 12 // the last column (status) takes the space left, so every other boundary can be dragged
  const defaultWidths = [48, 196, 30, 140, 150, 64, 36, 200, 100, 110, 44, 34, 0]
  let columnWidths = $state<number[]>((() => { try { const saved = JSON.parse(readSetting('columnWidths', '[]')); return Array.isArray(saved) && saved.length === defaultWidths.length ? saved : defaultWidths.slice() } catch { return defaultWidths.slice() } })())
  const gridColumns = $derived(columnWidths.map((width, index) => index === titleColumn ? 'minmax(72px, 1fr)' : `${width}px`).join(' '))
  let resizing: null | { index: number; x: number; width: number } = null
  function resizeDown(index: number, event: PointerEvent) { event.preventDefault(); event.stopPropagation(); resizing = { index, x: event.clientX, width: columnWidths[index] }; (event.currentTarget as HTMLElement).setPointerCapture(event.pointerId) }
  function resizeMove(event: PointerEvent) { if (!resizing) return; const next = columnWidths.slice(); next[resizing.index] = Math.round(Math.max(24, Math.min(600, resizing.width + event.clientX - resizing.x))); columnWidths = next }
  function resizeUp() { if (!resizing) return; resizing = null; writeSetting('columnWidths', JSON.stringify(columnWidths)) }
  function resetColumns() { columnWidths = defaultWidths.slice(); writeSetting('columnWidths', JSON.stringify(columnWidths)) }
  // The splitter between the wave and the track list (SPEC.md › Editing): drag it; the height is remembered
  let waveHeight = $state(Math.max(200, Number(readSetting('waveHeight', '240')) || 240))
  let splitting: null | { y: number; height: number } = null
  function splitDown(event: PointerEvent) { splitting = { y: event.clientY, height: waveHeight }; (event.currentTarget as HTMLElement).setPointerCapture(event.pointerId) }
  function splitMove(event: PointerEvent) { if (!splitting) return; waveHeight = Math.round(Math.max(200, Math.min(window.innerHeight - 260, splitting.height + event.clientY - splitting.y))) }
  function splitUp() { if (!splitting) return; splitting = null; writeSetting('waveHeight', String(waveHeight)) }
  // ---- The steps (SPEC.md › Workflow): what's left to do, and the next thing to do about it ----
  type Edge = { trackId: string; edge: 'start' | 'end' }
  const edges = $derived<Edge[]>(tracks.flatMap(track => [{ trackId: track.id, edge: 'start' as const }, { trackId: track.id, edge: 'end' as const }]))
  const unchecked = $derived(edges.filter(({ trackId, edge }) => { const track = tracks.find(candidate => candidate.id === trackId)!; return !(edge === 'start' ? track.startChecked : track.endChecked) && !track.skip }))
  const doubtful = $derived(unchecked.filter(({ trackId, edge }) => { const track = tracks.find(candidate => candidate.id === trackId)!; return !(edge === 'start' ? track.startSure : track.endSure) }))
  const untagged = $derived(tracks.filter(track => !track.skip && (!track.artist || !track.title || !track.album || !track.albumArtist || !track.genre)))
  const unexported = $derived(tracks.filter(track => !track.skip && !track.exported))
  const unverified = $derived(tracks.filter(track => !track.skip && track.exported && !track.verified))
  const exportable = $derived(tracks.filter(track => !track.skip))
  let step = $state<'cuts' | 'tags' | 'export'>('cuts')
  let focused = $state<Edge | null>(null)
  // Check the cuts: each start and end in turn, zoomed in both axes with the edge grabbed, and played
  function focusEdge(target: Edge, play = true) {
    const track = tracks.find(candidate => candidate.id === target.trackId); if (!track || !waveform) return
    // Hearing an edge doesn't select its line on the wave: that's done by hand there (SPEC.md › Editing). The row's button shows it was the last heard
    focused = target; currentId = track.id
    const frame = track[target.edge]; cursor = frame
    // Zoomed to what you'll hear (SPEC.md › Editing): a start shows half the Audition length before it and the full length after;
    // an end the full length before and half after. Zoom freely from there
    const listen = app.auditionSeconds * rate, span = listen * 1.5
    view = waveform.clampView({ start: target.edge === 'start' ? frame - listen / 2 : frame - listen, framesPerPixel: span / waveWidth() })
    scrollRowIntoView(track.id)
    if (play) hearEdge(target.edge, track.id)
  }
  // Find next to confirm (C): the next start or end without a ✓, counting from where you are: just after the edge last jumped to if
  // it's on the current track, else from the current track's own start (SPEC.md › Editing)
  function checkNext() {
    if (!unchecked.length) { grab = null; focused = null; toast('Every start and end is confirmed', { ms: 2500 }); return }
    const position = (edge: Edge) => edges.findIndex(candidate => candidate.trackId === edge.trackId && candidate.edge === edge.edge)
    const from = focused && focused.trackId === current?.id ? position(focused) + 1 : current ? position({ trackId: current.id, edge: 'start' }) : 0
    const next = unchecked.find(edge => position(edge) >= from) ?? unchecked[0]
    focusEdge(next)
  }
  function markChecked(target: Edge) {
    const next = copy(); const track = next.find(candidate => candidate.id === target.trackId); if (!track) return
    if (target.edge === 'start') track.startChecked = true; else track.endChecked = true
    tracks = next; save()
  }
  // Y: confirm (or undo) the start or end you're on, without moving anywhere
  function confirmFocused() { const track = current; if (!track) { toast('Click a track first'); return } confirmTrack(track) }
  // Between tracks (SPEC.md › Export): trimmed to a short roll (the default), or kept as recorded
  const trimSilence = $derived(app.recordingProfiles.find(profile => profile.id === session?.recordingProfileId)?.trimSilence ?? true)
  const endsMeet = $derived(app.recordingProfiles.find(profile => profile.id === session?.recordingProfileId)?.endsMeet ?? true)
  const betweenTracks = $derived(!trimSilence ? 'keep' : endsMeet ? 'meet' : 'trim')
  function setBetween(choice: string) {
    if (!session) return
    const trim = choice !== 'keep', meet = choice === 'meet'
    sendToShell({ type: 'setTrimSilence', profileId: session.recordingProfileId, trim, meet })
    const profile = app.recordingProfiles.find(candidate => candidate.id === session!.recordingProfileId); if (profile) { profile.trimSilence = trim; profile.endsMeet = meet }
    dialog = { kind: 'redetect' }
  }
  // Revert an edge to where detection put it
  function revertEdge(track: Track, edge: 'start' | 'end') {
    const detected = edge === 'start' ? track.detectedStart : track.detectedEnd; if (detected == null || detected < 0) return
    const next = copy(), edited = next.find(candidate => candidate.id === track.id)!
    edited[edge] = detected; if (edge === 'start') edited.startChecked = false; else edited.endChecked = false
    commit(next, `Track ${tracks.indexOf(track) + 1} ${edge} back where it was detected`)
  }
  let snapToZero = $state(readFlag('snapZero'))
  let waveMode = $state<'wave' | 'spectrogram'>(readSetting('waveMode', 'wave') === 'spectrogram' ? 'spectrogram' : 'wave')
  function setWaveMode(mode: 'wave' | 'spectrogram') { waveMode = mode; writeSetting('waveMode', mode) }
  // How much of each start or end you hear; L steps through the lengths
  function setAudition(seconds: number) { app.auditionSeconds = seconds; sendToShell({ type: 'setAuditionSeconds', seconds }) }
  function nextAudition() { const choices = auditionChoices.map(([value]) => Number(value)); const at = choices.indexOf(app.auditionSeconds); setAudition(choices[(at + 1) % choices.length]); toast(`Auditioning ${app.auditionSeconds} s of each start or end`, { ms: 1500 }) }
  // A long session has a reading every two seconds (tens of thousands): each row reads them once from a plain copy, not on every edit
  const qualitySamples = $derived(session ? $state.snapshot(session.quality ?? []) : [])
  const cutoffMemo = $derived.by(() => { qualitySamples; return new Map<string, number | null>() })
  function cutoffFor(start: number, end: number) { const key = start + ':' + end; if (!cutoffMemo.has(key)) cutoffMemo.set(key, medianCutoff(qualitySamples, start, end)); return cutoffMemo.get(key) ?? null }
  const sessionCutoff = $derived(session ? cutoffFor(0, session.totalFrames) : null)
  let exportStatus = $state('')
  let waveform = $state<ReturnType<typeof Waveform>>()
  const scope = $derived('tracks:' + sessionId)
  const rate = $derived(session?.sampleRate ?? 48000)
  const selectedIds = $derived(selection.scope === scope ? selection.keys : [])
  const playing = $derived(!!app.status?.playing && app.status.playingSession === sessionId)
  function readFlag(name: string) { try { return localStorage.getItem('wtr.' + name) === '1' } catch { return false } }

  onShellMessage('openProgress', message => { if (message.id === sessionId) loadProgress = message.fraction })
  onShellMessage('suggestion', message => { if (message.id !== sessionId) return; const track = tracks.find(candidate => candidate.id === message.trackId); if (track) { track.suggestion = message.suggestion; track.suggestionLooked = message.looked } })
  onShellMessage('albumCheck', message => { if (message.id === sessionId && session) session.albumCheck = message.check })
  onShellMessage('albumProgress', message => { if (message.id === sessionId) albumProgress = { done: message.done, total: message.total } })
  onShellMessage('albumMatch', message => {
    if (message.id !== sessionId || !session) return
    session.albums = message.albums; session.albumLooked = true; albumProgress = null
    for (const update of message.tracks as { trackId: string; suggestion: any; offAlbum: boolean }[]) { const track = tracks.find(candidate => candidate.id === update.trackId); if (track) { track.suggestion = update.suggestion; track.offAlbum = update.offAlbum; track.suggestionDismissed = false; track.suggestionLooked = true } }
  })
  onShellMessage('exportProgress', message => exportStatus = message.total ? `Exporting ${Math.min(message.done + 1, message.total)} of ${message.total}${message.current ? ': ' + message.current : ''}` : '')

  async function load() {
    session = null; loadError = ''; loadProgress = 0; past = []; future = []; clearSelection()
    try {
      const detail = await request<SessionDetail>({ type: 'openSession', id: sessionId })
      session = detail; tracks = detail.tracks; currentId = detail.tracks[0]?.id ?? ''; cursor = detail.tracks[0]?.start ?? 0
      sendToShell({ type: 'snapshot', id: sessionId, label: 'Opened', tracks: $state.snapshot(tracks) }) // the first snapshot is the state as detected
      view = { start: 0, framesPerPixel: Math.max(1, detail.totalFrames / 1000) } // the waveform fits it once it knows its width
      requestAnimationFrame(() => fit())
    } catch (error) { loadError = (error as Error).message }
  }
  $effect(() => { sessionId; untrack(load) })

  // ---- Edits: every change is undoable and saved straight away ----
  let past = $state<string[]>([]), future = $state<string[]>([])
  let saveTimer = 0
  function save() { clearTimeout(saveTimer); saveTimer = window.setTimeout(() => sendToShell({ type: 'saveSession', id: sessionId, tracks: $state.snapshot(tracks) }), 250) }
  function commit(next: Track[], label: string) {
    past = past.concat([JSON.stringify($state.snapshot(tracks))]).slice(-200); future = []
    tracks = next.slice().sort((first, second) => first.start - second.start); save()
    sendToShell({ type: 'snapshot', id: sessionId, label, tracks: $state.snapshot(tracks) }) // the history survives closing the app
    toast(label, { undo })
  }
  const canUndo = $derived(past.length > 0), canRedo = $derived(future.length > 0)
  let historyOpen = $state(false), snapshots = $state<{ index: number; at: string; label: string; tracks: number }[]>([])
  async function openHistory() { snapshots = await request({ type: 'history', id: sessionId }); historyOpen = true }
  async function restoreSnapshot(index: number, label: string) {
    try { const restored = await request<Track[]>({ type: 'restoreSnapshot', id: sessionId, index }); historyOpen = false; commit(restored, `Restored: ${label}`); clearSelection() }
    catch (error) { toast('Could not restore: ' + (error as Error).message) }
  }
  function undo() { const previous = past.pop(); if (!previous) return; future.push(JSON.stringify($state.snapshot(tracks))); tracks = JSON.parse(previous); save(); toast('Undone', { ms: 2000 }) }
  function redo() { const next = future.pop(); if (!next) return; past.push(JSON.stringify($state.snapshot(tracks))); tracks = JSON.parse(next); save(); toast('Redone', { undo, ms: 4000 }) }
  const copy = () => tracks.map(track => ({ ...$state.snapshot(track) }))
  const trackAt = (frame: number) => tracks.find(track => frame >= track.start && frame < track.end)
  const current = $derived(tracks.find(track => track.id === currentId) ?? trackAt(cursor))
  const targets = (keys?: string[]) => { const ids = keys?.length ? keys : selectedIds; return ids.length ? tracks.filter(track => ids.includes(track.id)) : tracks }

  // ---- Dragging edges (live), committed on release; snapped to a zero crossing when that's on ----
  let dragBefore = ''
  function onEdge(grabbed: NonNullable<Grab>, frame: number) {
    grab = grabbed
    if (!dragBefore) dragBefore = JSON.stringify($state.snapshot(tracks))
    moveEdge(grabbed, frame)
  }
  function moveEdge(grabbed: NonNullable<Grab>, frame: number) {
    const index = tracks.findIndex(track => track.id === grabbed.trackId); if (index < 0) return
    const track = tracks[index], previous = tracks[index - 1], next = tracks[index + 1]
    // Moving an edge doesn't confirm it: that's the user's own ✓ (SPEC.md › Editing)
    if (grabbed.edge === 'start') track.start = Math.max(previous?.end ?? 0, Math.min(track.end - 1, frame))
    else track.end = Math.min(next?.start ?? session!.totalFrames, Math.max(track.start + 1, frame))
  }
  async function onEdgeDone(grabbed: NonNullable<Grab>) {
    const before = dragBefore; dragBefore = ''
    const track = tracks.find(candidate => candidate.id === grabbed.trackId)
    if (track && snapToZero) moveEdge(grabbed, await request<number>({ type: 'nearestZero', id: sessionId, frame: track[grabbed.edge], radius: Math.max(200, Math.round(view.framesPerPixel * 12)) }))
    if (before && before !== JSON.stringify($state.snapshot(tracks))) { past = past.concat([before]).slice(-200); future = []; save() }
  }
  async function snapped(frame: number) { return snapToZero ? await request<number>({ type: 'nearestZero', id: sessionId, frame, radius: Math.max(200, Math.round(view.framesPerPixel * 12)) }) : frame }

  async function setEdgeAtCursor(edge: 'start' | 'end', chosen?: Track) {
    const track = chosen ?? current; if (!track) { toast('Put the playhead inside a track first'); return false }
    const frame = await snapped(cursor)
    const next = copy(), edited = next.find(candidate => candidate.id === track.id)!
    if (edge === 'start' && frame >= edited.end || edge === 'end' && frame <= edited.start) { toast(edge === 'start' ? 'The start must be before the end' : 'The end must be after the start'); return false }
    edited[edge] = frame; grab = { trackId: track.id, edge }
    commit(next, `Track ${tracks.indexOf(track) + 1} ${edge === 'start' ? 'starts' : 'ends'} at ${formatTime(frame, rate)}`)
    return true
  }
  async function splitAtCursor() {
    const frame = await snapped(cursor)
    const track = trackAt(frame)
    if (!track || frame - track.start < rate / 10 || track.end - frame < rate / 10) { toast('Put the playhead inside a track, away from its ends, to split it'); return }
    const next = copy(), first = next.find(candidate => candidate.id === track.id)!
    const second: Track = { ...first, id: Math.random().toString(36).slice(2, 12), start: frame, title: '', artist: first.artist, exported: false, exportedPath: '', fadeInMs: 0 }
    first.end = frame; first.fadeOutMs = 0; first.exported = false
    next.push(second); commit(next, `Split track ${tracks.indexOf(track) + 1} at ${formatTime(frame, rate)}`)
    currentId = second.id
  }
  // A new track over the cursor's gap (between tracks), or from the cursor to the next track
  function addTrackAtCursor() {
    if (trackAt(cursor)) { splitAtCursor(); return }
    const previous = tracks.filter(track => track.end <= cursor).at(-1), next = tracks.find(track => track.start > cursor)
    const start = cursor, end = next?.start ?? Math.min(session!.totalFrames, cursor + 60 * rate)
    if (end - start < rate / 10) return
    const added: Track = { id: Math.random().toString(36).slice(2, 12), start, end, artist: previous?.artist ?? '', title: '', album: previous?.album ?? '', fadeInMs: 0, fadeOutMs: 0, exported: false, exportedPath: '' }
    commit(copy().concat([added]), `Added a track at ${formatTime(start, rate)}`); currentId = added.id
  }
  // Removing one track between two others asks how the gap closes (SPEC.md › Tracks › Remove)
  let gapFor = $state<Track | null>(null)
  function closeGap(how: 'gap' | 'previous' | 'next' | 'middle') {
    const removed = gapFor; gapFor = null
    if (!removed) return
    const next = copy(), at = next.findIndex(track => track.id === removed.id), before = next[at - 1], after = next[at + 1]
    if (how === 'previous' || how === 'middle') { before.end = how === 'previous' ? after.start : Math.round((before.end + after.start) / 2); before.exported = false; before.endChecked = false }
    if (how === 'next' || how === 'middle') { after.start = before.end; after.exported = false; after.startChecked = false }
    commit(next.filter(track => track.id !== removed.id), how === 'gap' ? 'Removed 1 track (its audio is skipped on export)' : 'Removed 1 track and closed the gap')
    clearSelection()
  }
  async function addToRecording() {
    const result = await request<{ ok: boolean; reason: string }>({ type: 'appendRecording', id: sessionId })
    if (result.ok) nav.view = 'record'; else toast(result.reason)
  }
  function deleteTracks(keys: string[]) {
    const ids = keys.length ? keys : current ? [current.id] : []
    if (!ids.length) return
    if (ids.length === 1) { const at = tracks.findIndex(track => track.id === ids[0]); if (at > 0 && at < tracks.length - 1) { gapFor = tracks[at]; return } }
    commit(copy().filter(track => !ids.includes(track.id)), `Removed ${ids.length} track${ids.length === 1 ? '' : 's'} (their audio is skipped on export)`)
    clearSelection()
  }
  function mergeTracks(keys: string[]) {
    const chosen = tracks.filter(track => keys.includes(track.id))
    if (chosen.length < 2) { toast('Choose two or more tracks to merge'); return }
    const first = chosen[0], last = chosen[chosen.length - 1]
    const next = copy().filter(track => !keys.includes(track.id) || track.id === first.id)
    const merged = next.find(track => track.id === first.id)!; merged.end = last.end; merged.fadeOutMs = last.fadeOutMs; merged.exported = false
    // Tracks in between that weren't chosen go too, so the merged one is continuous
    commit(next.filter(track => track.id === first.id || track.end <= first.start || track.start >= last.end), `Merged ${chosen.length} tracks`)
    clearSelection(); currentId = first.id
  }
  function setSkip(track: Track, skip: boolean) { const next = copy(); const edited = next.find(candidate => candidate.id === track.id)!; edited.skip = skip; commit(next, skip ? `Track ${tracks.indexOf(track) + 1} left out of the export` : `Track ${tracks.indexOf(track) + 1} back in`) }
  // Captured names (SPEC.md › Tags › Revert): what the player said when the track started, kept with the session
  function capturedFor(track: Track) {
    const changes = session?.changes ?? [], from = (track.detectedStart != null && track.detectedStart >= 0 ? track.detectedStart : track.start)
    let best: (typeof changes)[number] | null = null, bestDistance = Infinity
    for (const change of changes) { if (change.frame > track.end) continue; const distance = Math.abs(change.frame - from); if (distance < bestDistance) { bestDistance = distance; best = change } }
    return best && bestDistance < rate * 30 ? best : null
  }
  let reverting = $state<null | { keys: string[]; artist: boolean; title: boolean; album: boolean }>(null)
  function revertCaptured() {
    const job = reverting; reverting = null; if (!job) return
    const next = copy(); let count = 0
    for (const track of next) if (job.keys.includes(track.id)) {
      const captured = capturedFor(track); if (!captured) continue
      if (job.artist) track.artist = captured.artist; if (job.title) track.title = captured.title; if (job.album) track.album = captured.album ?? ''
      track.exported = false; count++
    }
    commit(next, `${count} track${count === 1 ? '' : 's'} back to the captured names`)
  }
  function editNumberField(track: Track, field: 'discNumber', text: string) { const next = copy(), edited = next.find(candidate => candidate.id === track.id)! as any; edited[field] = Math.max(0, Math.round(Number(text.trim()) || 0)); edited.exported = false; commit(next, `Track ${tracks.indexOf(track) + 1}: disc ${edited[field] || 'cleared'}`) }
  // While exporting, the file being written is highlighted in the report and kept in view
  let exportingFile = $state('')
  onShellMessage('exportProgress', message => { if (message.id !== sessionId) return; exportingFile = message.current ?? ''; if (step === 'export') { const id = Object.keys(exportPaths).find(key => fileOf(exportPaths[key]?.path) === exportingFile); if (id) document.querySelector(`[data-report-row="${id}"]`)?.scrollIntoView({ block: 'center', behavior: 'smooth' }) } })
  onShellMessage('exportDone', message => { if (message.id === sessionId) exportingFile = '' })
  // Each track turns "Exported" the moment it's written and checked, not only at the end
  onShellMessage('exportTracks', message => { if (message.id !== sessionId) return; for (const update of message.tracks as { id: string; exported: boolean; exportedPath: string; verified: boolean; verifyError: string }[]) { const track = tracks.find(candidate => candidate.id === update.id); if (track) { track.exported = update.exported; track.exportedPath = update.exportedPath; track.verified = update.verified; track.verifyError = update.verifyError } } })
  const queued = $derived(!!app.status?.exportQueue?.includes(sessionId))
  const exportingHere = $derived(!!app.status?.exporting && app.status?.exportingSession === sessionId)
  function editComment(track: Track, value: string) { const next = copy(), edited = next.find(candidate => candidate.id === track.id)!; edited.comment = value; edited.exported = false; commit(next, `Track ${tracks.indexOf(track) + 1}: comment changed`) }
  function setTrackNumber(track: Track, text: string) {
    const number = Math.max(0, Math.round(Number(text.trim()) || 0)), next = copy(), edited = next.find(candidate => candidate.id === track.id)!
    edited.trackNumber = number; edited.exported = false
    commit(next, `Track ${tracks.indexOf(track) + 1} is number ${number || 'by position'}`)
  }
  function editField(track: Track, field: 'artist' | 'title' | 'album' | 'year' | 'genre' | 'albumArtist' | 'composer', value: string) {
    if ((track[field] ?? '') === value) return
    const next = copy(); const edited = next.find(candidate => candidate.id === track.id)!; edited[field] = value; edited.exported = edited.exported && false
    commit(next, `Track ${tracks.indexOf(track) + 1}: ${field} changed`)
  }

  // ---- Albums in the track list (SPEC.md › Tags › Albums): runs of tracks sharing artist (or album artist) and album, each with a
  // header that sets artist, album, year and genre for all its tracks; a track can be moved into the album above or below ----
  // Grouped by album name and album artist, so featured artists on single tracks don't split an album; tracks with no album group by artist
  const squash = (text: string | undefined) => (text ?? '').toLowerCase().replace(/[^\p{L}\p{N}]/gu, '')
  const albumKey = (track: Track) => track.album ? 'album:' + squash(track.album) + '|' + squash(track.albumArtist) : 'artist:' + squash(track.artist)
  type ListAlbum = { joined?: boolean; key: string; first: string; ids: string[]; artist: string; album: string; year: string; genre: string; composer: string }
  const listAlbums = $derived.by(() => {
    const list: ListAlbum[] = []
    for (const track of tracks) {
      const key = albumKey(track), last = list[list.length - 1]
      if (last && last.key === key) { last.ids.push(track.id); if (!last.year && track.year) last.year = track.year; if (!last.genre && track.genre) last.genre = track.genre; if (!last.composer && track.composer) last.composer = track.composer }
      else list.push({ key, first: track.id, ids: [track.id], artist: track.albumArtist || track.artist || '', album: track.album || '', year: track.year ?? '', genre: track.genre ?? '', composer: track.composer ?? '' })
      if (list[list.length - 1].ids.length > 1 && !track.albumArtist) { const group = list[list.length - 1]; const artists = new Set(group.ids.map(id => tracks.find(candidate => candidate.id === id)?.artist ?? '')); if (artists.size > 1) group.artist = Array.from(artists).sort((first, second) => second.length - first.length).pop() ?? group.artist }
    }
    // After the cuts, a track recorded later goes back to its album (a retake at the end of the session, say): same album name, and an artist in common
    if (step !== 'cuts') for (let index = list.length - 1; index > 0; index--) {
      const later = list[index]; if (!later.album) continue
      const members = (group: ListAlbum) => tracks.filter(track => group.ids.includes(track.id)), laterArtists = new Set(members(later).flatMap(track => [squash(track.artist), squash(track.albumArtist)]).filter(Boolean))
      const home = list.slice(0, index).find(group => squash(group.album) === squash(later.album) && members(group).some(track => laterArtists.has(squash(track.artist)) || laterArtists.has(squash(track.albumArtist))))
      if (home) { home.ids.push(...later.ids); home.joined = true; if (!home.year) home.year = later.year; if (!home.genre) home.genre = later.genre; if (!home.composer) home.composer = later.composer; list.splice(index, 1) }
    }
    return list
  })
  const albumOf = (trackId: string) => listAlbums.find(group => group.ids.includes(trackId))
  // Fill: every track in the album gets the header's artist (and album artist), album, year and genre, as typed now
  function fillFromHeader(group: ListAlbum, header: HTMLElement | null) {
    if (!header) return
    const value = (selector: string) => ((header.querySelector(selector) as HTMLInputElement | null)?.value ?? '').trim()
    const artist = value('.cell-artist'), album = value('.cell-album'), year = value('.cell-year'), genre = value('.cell-genre')
    const next = copy()
    for (const track of next) if (group.ids.includes(track.id)) { if (artist) { track.artist = artist; track.albumArtist = artist } track.album = album; track.year = year; track.genre = genre; track.exported = false }
    commit(next, `Album details filled into ${group.ids.length} track${group.ids.length === 1 ? '' : 's'}`)
  }
  function setAlbumField(group: ListAlbum, field: 'artist' | 'album' | 'year' | 'genre' | 'composer', value: string) {
    const next = copy()
    for (const track of next) if (group.ids.includes(track.id)) {
      if (field === 'artist') { track.artist = value; track.albumArtist = value } // the album's artist on every track, featured credits included
      else (track as any)[field] = value
      track.exported = false
    }
    commit(next, `${field === 'artist' ? 'Artist' : field[0].toUpperCase() + field.slice(1)} set on ${group.ids.length} track${group.ids.length === 1 ? '' : 's'}`)
  }
  let joining = $state<null | { trackId: string; target: ListAlbum; mergeIds?: string[] }>(null)
  // Album tiles (SPEC.md › Albums & cuts): each album is a compact tile; one is open at a time showing its tracks.
  // The current track's album opens by default; clicking a tile opens it
  let openAlbumKey = $state('')
  const openGroup = $derived(listAlbums.find(group => group.first === openAlbumKey) ?? (current ? albumOf(current.id) : null) ?? listAlbums[0] ?? null)
  function openTile(group: ListAlbum) { openAlbumKey = group.first; if (!group.ids.includes(currentId)) { currentId = group.first; const first = tracks.find(track => track.id === group.first); if (first) { cursor = first.start; showFrame(first.start) } } }
  // Press and hold a track to pick it up, then drop it on the album above or below to join it, or on the gap to give it an album of its own
  let lifting = $state(''), lifted = $state(''), dropOn = $state(''), liftTimer = 0, liftStart = { x: 0, y: 0 }
  function liftDown(track: Track, event: PointerEvent) {
    // Anywhere on the row but its buttons, text boxes included: holding still picks it up, moving first (selecting text) doesn't
    if (event.button !== 0 || (event.target as HTMLElement)?.closest?.('button, select, input[type=checkbox]')) return
    lifting = track.id; liftStart = { x: event.clientX, y: event.clientY }; clearTimeout(liftTimer)
    const cancelOnMove = (move: PointerEvent) => { if (Math.abs(move.clientX - liftStart.x) + Math.abs(move.clientY - liftStart.y) > 5) { clearTimeout(liftTimer); lifting = ''; window.removeEventListener('pointermove', cancelOnMove) } }
    window.addEventListener('pointermove', cancelOnMove)
    liftTimer = window.setTimeout(() => { window.removeEventListener('pointermove', cancelOnMove); (document.activeElement as HTMLElement | null)?.blur?.(); window.getSelection()?.removeAllRanges(); lifted = track.id; lifting = ''; window.addEventListener('pointermove', liftMove); window.addEventListener('pointerup', liftUp, { once: true }) }, 350)
    window.addEventListener('pointerup', () => { clearTimeout(liftTimer); lifting = ''; window.removeEventListener('pointermove', cancelOnMove) }, { once: true })
  }
  function liftMove(event: PointerEvent) {
    const target = (document.elementFromPoint(event.clientX, event.clientY) as HTMLElement | null)?.closest?.('[data-drop]') as HTMLElement | null
    dropOn = target?.dataset.drop ?? ''
  }
  // A whole album picked up by its header (press and hold on the header's label or its empty space)
  let liftedAlbum = $state('')
  function liftAlbumDown(group: ListAlbum, event: PointerEvent) {
    if (event.button !== 0 || (event.target as HTMLElement)?.closest?.('input, button, select')) return
    liftStart = { x: event.clientX, y: event.clientY }; clearTimeout(liftTimer)
    const cancelOnMove = (move: PointerEvent) => { if (Math.abs(move.clientX - liftStart.x) + Math.abs(move.clientY - liftStart.y) > 5) { clearTimeout(liftTimer); window.removeEventListener('pointermove', cancelOnMove) } }
    window.addEventListener('pointermove', cancelOnMove)
    liftTimer = window.setTimeout(() => { window.removeEventListener('pointermove', cancelOnMove); liftedAlbum = group.first; window.addEventListener('pointermove', liftMove); window.addEventListener('pointerup', liftAlbumUp, { once: true }) }, 350)
    window.addEventListener('pointerup', () => { clearTimeout(liftTimer); window.removeEventListener('pointermove', cancelOnMove) }, { once: true })
  }
  function liftAlbumUp() {
    window.removeEventListener('pointermove', liftMove)
    const from = listAlbums.find(group => group.first === liftedAlbum), target = dropOn; liftedAlbum = ''; dropOn = ''
    const into = listAlbums.find(candidate => 'album:' + candidate.first === target)
    if (!from || !into || into === from) return
    // Merged straight in, taking that album's names; Undo puts it back
    const sample = tracks.find(track => track.id === into.first)!, next = copy()
    for (const edited of next) if (from.ids.includes(edited.id)) { if (!sample.album) edited.artist = sample.artist; edited.albumArtist = sample.albumArtist; edited.album = sample.album; edited.year = sample.year; edited.genre = sample.genre || edited.genre; edited.exported = false }
    commit(next, `${from.ids.length} track${from.ids.length === 1 ? '' : 's'} merged into ${albumName(into)}`)
    openAlbumKey = into.first
  }
  function liftUp() {
    window.removeEventListener('pointermove', liftMove)
    const trackId = lifted, target = dropOn; lifted = ''; dropOn = ''
    if (!trackId || !target) return
    const mover = tracks.find(track => track.id === trackId); if (!mover) return
    if (target === 'split') { splitting2 = { trackId, name: mover.album ? `${mover.album} (${mover.title || 'single'})` : mover.title }; return }
    const group = listAlbums.find(candidate => 'album:' + candidate.first === target); if (!group || group.ids.includes(trackId)) return
    // Straight in, taking that album's names (each track keeps its own artist); Undo puts it back
    const sample = tracks.find(track => track.id === group.first)!, next = copy(), edited = next.find(track => track.id === trackId)!
    edited.albumArtist = sample.albumArtist; edited.album = sample.album; edited.year = sample.year; edited.genre = sample.genre || edited.genre; edited.exported = false
    if (!sample.album) edited.artist = sample.artist // an album without a name is held together by its artist
    commit(next, `“${mover.title || 'Track'}” moved into ${albumName(group)}`)
    openAlbumKey = group.first
  }
  let splitting2 = $state<null | { trackId: string; name: string }>(null)
  function splitOut() {
    const split = splitting2; splitting2 = null; if (!split) return
    const next = copy(), edited = next.find(track => track.id === split.trackId)!; edited.album = split.name.trim(); edited.exported = false
    commit(next, 'Track given an album of its own')
  }
  let dragTrack = ''
  function joinAlbum(mode: 'theirs' | 'mine') {
    const join = joining; joining = null; if (!join) return
    const mover = tracks.find(track => track.id === join.trackId); if (!mover) return
    const next = copy()
    // Album-level fields only: each track keeps its own artist (a featured artist stays on its track)
    const movers = join.mergeIds ?? [mover.id]
    if (mode === 'theirs') { const sample = tracks.find(track => track.id === join.target.first)!; for (const edited of next) if (movers.includes(edited.id)) { if (!sample.album) edited.artist = sample.artist; edited.albumArtist = sample.albumArtist; edited.album = sample.album; edited.year = sample.year; edited.genre = sample.genre || edited.genre; edited.exported = false } }
    else for (const track of next) if (join.target.ids.includes(track.id)) { track.albumArtist = mover.albumArtist; track.album = mover.album; track.year = mover.year || track.year; track.genre = mover.genre || track.genre; track.exported = false }
    commit(next, movers.length > 1 ? 'Albums merged' : 'Track moved into the album')
  }
  const albumName = (group: { artist: string; album: string; year?: string }) => `${group.artist || 'Unknown artist'} - ${group.album || 'no album'}${group.year ? ` (${group.year})` : ''}`

  // ---- Tags cards ----
  const plainTitle = (title: string) => squash((title ?? '').replace(/\s*[\(\[][^\)\]]*[\)\]]/g, ''))
  function sameTitle(mine: string, theirs: string) { const a = plainTitle(mine), b = plainTitle(theirs); return a !== '' && (a === b || (a.length >= 4 && b.length >= 4 && (a.includes(b) || b.includes(a)))) }
  // The online match for an album: the one sharing most of its tracks
  function matchFor(group: ListAlbum): AlbumMatch | null { let best: AlbumMatch | null = null, most = 0; for (const match of session?.albums ?? []) { const shared = match.trackIds.filter(id => group.ids.includes(id)).length; if (shared > most) { most = shared; best = match } } return best }
  function theirTitle(track: Track, match: AlbumMatch) { if (track.suggestion?.releaseId === match.releaseId && track.suggestion.title) return track.suggestion.title; return match.trackTitles.find(title => sameTitle(track.title, title)) ?? '' }
  function commonOf(list: Track[], field: keyof Track): string | null { const values = Array.from(new Set(list.map(track => ((track as any)[field] ?? '') as string))); return values.length === 1 ? values[0] : values.length === 0 ? '' : null }
  type Field = 'artist' | 'albumArtist' | 'album' | 'year' | 'genre'
  // What an album still lacks (SPEC.md › Fill gaps & covers): a blank or mixed album field, no cover, or a track with no artist or title
  function albumNeeds(group: ListAlbum) {
    const list = tracks.filter(track => group.ids.includes(track.id)), wanting = fieldRows(list, matchFor(group)).filter(row => row.field === 'artist' ? row.mine === '' : !row.mine).map(row => row.label) // tracks by different artists are fine
    if (!commonOf(list, 'artPath')) wanting.push('Cover')
    const blanks = list.filter(track => !track.skip && (!track.artist || !track.title)).length
    if (blanks) wanting.push(`${blanks} track${blanks === 1 ? '' : 's'}`)
    return wanting
  }
  let onlyNeedy = $state(false)
  const albumsReady = $derived(listAlbums.filter(group => !albumNeeds(group).length).length)
  // Every picture on offer for an album
  type CoverChoice = CompareItem & { kind: 'database' | 'player' | 'own'; src: string }
  function coverChoices(match: AlbumMatch | null, playerCover: string, ownCover: string | null, inUse: string): CoverChoice[] {
    const choices: CoverChoice[] = []
    if (match?.artUrl && !failedCovers[match.artUrl]) choices.push({ key: match.artUrl, kind: 'database', label: match.source, src: match.artUrl, url: match.artUrl, inUse: inUse === 'database' })
    if (playerCover) choices.push({ key: playerCover, kind: 'player', label: 'Player', src: thumbs[playerCover], path: playerCover, inUse: inUse === 'player' })
    if (inUse === 'own' && ownCover) choices.push({ key: ownCover, kind: 'own', label: 'Your own', src: thumbs[ownCover] ?? '', path: ownCover, inUse: true })
    return choices
  }
  function useChoice(group: ListAlbum, choice: CompareItem & { kind?: string }) {
    if (choice.url) useCoverUrl(group, choice.url)
    else if (choice.path) { coverSources = Object.assign({}, coverSources, { [group.first]: choice.kind === 'player' ? 'player' : 'own' }); applyTags(group.ids, { artPath: choice.path }, false) }
  }
  // Resolution and file size under each picture, fetched one at a time
  let artInfos = $state<Record<string, { width: number; height: number; bytes: number } | null>>({})
  const artAsked = new Set<string>()
  let artRunning = false
  async function loadArtInfos() {
    if (artRunning) return
    artRunning = true
    try {
      for (let again = true; again;) {
        again = false
        for (const group of listAlbums) {
          const list = tracks.filter(track => group.ids.includes(track.id)), match = matchFor(group), own = commonOf(list, 'artPath'), player = commonest(list.map(track => track.playerArtPath ?? '').filter(Boolean))
          for (const [key, path, url] of [[own ?? '', own ?? '', ''], [player, player, ''], [match?.artUrl ?? '', '', match?.artUrl ?? '']]) {
            if (!key || artAsked.has(key)) continue
            artAsked.add(key); again = true
            try { artInfos = Object.assign({}, artInfos, { [key]: await request<{ width: number; height: number; bytes: number }>({ type: 'artInfo', id: sessionId, path, url }) }) }
            catch { artInfos = Object.assign({}, artInfos, { [key]: null }) }
          }
        }
      }
    } finally { artRunning = false }
  }
  $effect(() => { if (step === 'tags' && listAlbums.length) untrack(loadArtInfos) })
  const artSize = (bytes: number) => bytes >= 1048576 ? `${(bytes / 1048576).toFixed(1)} MB` : `${Math.round(bytes / 1024)} KB`
  let comparing = $state<null | { group: ListAlbum; items: (CompareItem & { kind?: string })[] }>(null)
  function openCompare(group: ListAlbum, choices: CoverChoice[], inUsePath: string | null) {
    const items: (CompareItem & { kind?: string })[] = choices.flatMap(choice => choice.kind === 'database'
      ? [{ ...choice, label: `${choice.label} (standard)` }, { key: choice.key + '#full', kind: 'database', label: `${choice.label} (largest)`, url: choice.url!.replace(/-500$/, ''), inUse: false }]
      : [choice])
    comparing = { group, items: items.map(item => item.kind === 'database' && item.inUse && inUsePath ? { ...item, inUse: true } : item) }
  }
  function fieldRows(list: Track[], match: AlbumMatch | null) {
    const rows: { field: Field; label: string; mine: string | null; theirs: string }[] = [
      { field: 'artist', label: 'Artist', mine: commonOf(list, 'artist'), theirs: match?.albumArtist ?? '' },
      { field: 'albumArtist', label: 'Album artist', mine: commonOf(list, 'albumArtist'), theirs: match?.albumArtist ?? '' },
      { field: 'album', label: 'Album', mine: commonOf(list, 'album'), theirs: match?.album ?? '' },
      { field: 'year', label: 'Year', mine: commonOf(list, 'year'), theirs: match?.year ?? '' },
      { field: 'genre', label: 'Genre', mine: commonOf(list, 'genre'), theirs: match?.genre ?? '' },
    ]
    return rows.map(row => ({ ...row, same: !!row.theirs && (row.mine ?? '').toLowerCase() === row.theirs.toLowerCase() }))
  }
  function setGroupField(group: ListAlbum, field: Field, value: string) {
    const next = copy()
    for (const track of next) if (group.ids.includes(track.id)) { (track as any)[field] = value; track.exported = false }
    commit(next, `${field === 'albumArtist' ? 'Album artist' : field[0].toUpperCase() + field.slice(1)} set on ${group.ids.length} track${group.ids.length === 1 ? '' : 's'}`)
  }
  // The album's main artist: the one most of its tracks have (featured credits on single tracks don't count against it)
  function mainArtist(list: Track[]) { const counts = new Map<string, number>(); for (const track of list) if (track.artist) counts.set(track.artist, (counts.get(track.artist) ?? 0) + 1); return Array.from(counts.entries()).sort((first, second) => second[1] - first[1] || first[0].length - second[0].length)[0]?.[0] ?? '' }
  // One album artist for the whole album, so it stays one album however the track artists vary
  function sameAlbumArtist(group: ListAlbum) { const list = tracks.filter(track => group.ids.includes(track.id)), main = mainArtist(list); if (!main) return; const next = copy(); for (const track of next) if (group.ids.includes(track.id) || (track.album && squash(track.album) === squash(group.album) && !track.albumArtist && list.some(member => member.artist === track.artist))) { track.albumArtist = main; track.exported = false } commit(next, `Album artist set to ${main}`) }
  function useAllFrom(group: ListAlbum, list: Track[], match: AlbumMatch) {
    const next = copy()
    for (const track of next) if (group.ids.includes(track.id)) {
      if (match.albumArtist) { track.albumArtist = match.albumArtist; if (!track.artist) track.artist = match.albumArtist }
      if (match.album) track.album = match.album; if (match.year) track.year = match.year; if (match.genre) track.genre = match.genre
      track.exported = false
    }
    commit(next, `Album details from ${match.source}`)
    if (match.artUrl && !failedCovers[match.artUrl] && !commonOf(list, 'artPath')) useCoverUrl(group, match.artUrl)
  }
  // Fill gaps: the database only fills what's empty (your values are never replaced without a click on theirs), plus the cover if none
  function fillBlanksFrom(group: ListAlbum, match: AlbumMatch) {
    const next = copy()
    for (const track of next) if (group.ids.includes(track.id)) {
      if (!track.albumArtist && match.albumArtist) track.albumArtist = match.albumArtist
      if (!track.artist && match.albumArtist) track.artist = match.albumArtist
      if (!track.album && match.album) track.album = match.album
      if (!track.year && match.year) track.year = match.year
      if (!track.genre && match.genre) track.genre = match.genre
      track.exported = false
    }
    commit(next, `Blanks filled from ${match.source}`)
    const list = tracks.filter(track => group.ids.includes(track.id))
    if (match.artUrl && !failedCovers[match.artUrl] && !commonOf(list, 'artPath')) useCoverUrl(group, match.artUrl)
  }
  function useAllTitles(list: Track[], match: AlbumMatch) { const next = copy(); for (const track of next) { const original = list.find(candidate => candidate.id === track.id); if (!original) continue; const theirs = theirTitle(original, match); if (theirs) { track.title = theirs; track.exported = false } } commit(next, `Titles from ${match.source}`) }
  // Where each album's cover came from, so exactly one source shows as in use
  // Which albums' track lists are open in Fill gaps (kept open while you edit them)
  let openTrackLists = $state<Record<string, boolean>>({})
  // The picture most of an album's tracks came with: one track may carry the album before's picture, sent late by the player
  function commonest(paths: string[]) { const counts = new Map<string, number>(); for (const path of paths) counts.set(path, (counts.get(path) ?? 0) + 1); let best = '', most = 0; for (const [path, count] of counts) if (count > most) { best = path; most = count } return best }
  let coverSources = $state<Record<string, 'database' | 'player' | 'own'>>({})
  function coverInUse(group: ListAlbum, ownCover: string | null, playerCover: string): '' | 'database' | 'player' | 'own' {
    if (!ownCover) return ''
    const known = coverSources[group.first]; if (known) return known
    return playerCover && ownCover === playerCover ? 'player' : /[\\/]art-web-[^\\/]*$/.test(ownCover) ? 'database' : 'own'
  }
  async function useCoverUrl(group: ListAlbum, url: string) {
    try { const saved = await request<{ path: string; thumb: string }>({ type: 'fetchArt', id: sessionId, url }); thumbs = Object.assign({}, thumbs, { [saved.path]: saved.thumb }); coverSources = Object.assign({}, coverSources, { [group.first]: 'database' }); applyTags(group.ids, { artPath: saved.path }, false) }
    catch (error) { toast('No picture for that album: ' + (error as Error).message) }
  }
  // A picture dropped on an album's panel (SPEC.md › Fill gaps & covers)
  let dropAlbum = $state('')
  function dropCover(group: ListAlbum, file: File) {
    const reader = new FileReader()
    reader.onload = async () => {
      try {
        const data = String(reader.result).split(',')[1] ?? ''
        const saved = await request<{ path: string; thumb: string; error?: string }>({ type: 'artData', id: sessionId, data })
        if (!saved.path) { toast(saved.error || 'Could not use that picture'); return }
        thumbs = Object.assign({}, thumbs, { [saved.path]: saved.thumb }); coverSources = Object.assign({}, coverSources, { [group.first]: 'own' }); applyTags(group.ids, { artPath: saved.path }, false)
      } catch (error) { toast('Could not use that picture: ' + (error as Error).message) }
    }
    reader.readAsDataURL(file)
  }
  async function chooseCoverFor(group: ListAlbum, paste: boolean) {
    try {
      const result = await request<{ path: string; thumb: string; error?: string }>({ type: paste ? 'pasteArt' : 'chooseArt', id: sessionId })
      if (!result.path) { if (result.error) toast(result.error); return }
      thumbs = Object.assign({}, thumbs, { [result.path]: result.thumb }); coverSources = Object.assign({}, coverSources, { [group.first]: 'own' }); applyTags(group.ids, { artPath: result.path }, false)
    } catch (error) { toast('Could not use that picture: ' + (error as Error).message) }
  }

  // ---- Bulk dialogs: tags and fades for the selected tracks ----
  let dialog = $state<null | { kind: 'fades'; keys: string[]; fadeIn: string; fadeOut: string } | { kind: 'close' } | { kind: 'redetect' }>(null)
  // Tags (SPEC.md › Tags): the editor for the chosen tracks, and the online lookup it can open
  let tagging = $state<string[] | null>(null)
  let lookingUp = $state<null | { keys: string[]; artist: string; title: string; album: string }>(null)
  let thumbs = $state<Record<string, string>>({})
  $effect(() => {
    const missing = Array.from(new Set(tracks.flatMap(track => [track.artPath ?? '', track.playerArtPath ?? '']).filter(path => path && !(path in thumbs))))
    if (missing.length) untrack(() => request<Record<string, string>>({ type: 'thumbs', paths: missing }).then(found => { thumbs = Object.assign({}, thumbs, found) }).catch(() => { }))
  })
  function applyTags(keys: string[], fields: Partial<TagFields>, single: boolean) {
    const next = copy()
    for (const track of next) {
      if (!keys.includes(track.id)) continue
      for (const [name, value] of Object.entries(fields) as [keyof TagFields, string][]) {
        if (name === 'trackNumber' || name === 'trackCount' || name === 'discNumber') (track as any)[name] = Number(value) || 0
        else (track as any)[name] = value
      }
      track.exported = false
    }
    commit(next, `Tags set on ${keys.length} track${keys.length === 1 ? '' : 's'}${single ? '' : ''}`)
  }
  function applyMatch(keys: string[], match: TagMatch, take: Record<string, boolean>, artPath: string, artThumb: string) {
    const fields: Partial<TagFields> = {}
    if (take.title && match.title) fields.title = match.title
    if (take.artist && match.artist) fields.artist = match.artist
    if (take.album && match.album) fields.album = match.album
    if (take.albumArtist && match.albumArtist) fields.albumArtist = match.albumArtist
    if (take.year && match.year) fields.year = match.year
    if (take.genre && match.genre) fields.genre = match.genre
    if (take.trackNumber && match.trackNumber) { fields.trackNumber = String(match.trackNumber); if (match.trackCount) fields.trackCount = String(match.trackCount) }
    if (artPath) { fields.artPath = artPath; thumbs = Object.assign({}, thumbs, { [artPath]: artThumb }) }
    applyTags(keys, fields, keys.length === 1)
    lookingUp = null
  }
  // The album match (SPEC.md › Tags › Album match): the session's albums, in order (one, or several recorded back to back).
  // Album-wide fields are offered once per album, on its card; each track only gets its title spelling and number from its release
  type AlbumField = 'album' | 'albumArtist' | 'year' | 'genre'
  const albumFields: [AlbumField, string][] = [['album', 'Album'], ['albumArtist', 'Album artist'], ['year', 'Year'], ['genre', 'Genre']]
  const looseTitle = (title: string) => title.toLowerCase().replace(/\s*[\(\[].*?[\)\]]/g, '').replace(/[^\p{L}\p{N}]/gu, '')
  const albums = $derived(session?.albums ?? [])
  let albumProgress = $state<{ done: number; total: number } | null>(null)
  const albumTracks = (match: AlbumMatch) => exportable.filter(track => match.trackIds.includes(track.id))
  // What an album's tracks have now for a field: one shared value, '' when none have it, or null when they differ
  function commonValue(list: Track[], field: AlbumField | 'artPath'): string | null { const values = Array.from(new Set(list.map(track => ((track as any)[field] ?? '') as string))); return values.length === 1 ? values[0] : values.length === 0 ? '' : null }
  function albumOffers(match: AlbumMatch) {
    const list = albumTracks(match)
    return albumFields.map(([field, label]) => { const theirs = (match as any)[field] as string, yours = commonValue(list, field); return { field, label, theirs, yours, same: !theirs || (yours ?? '').toLowerCase() === theirs.toLowerCase(), kept: match.kept.includes(field) } })
  }
  let failedCovers = $state<Record<string, boolean>>({})
  function useAlbumField(match: AlbumMatch, fields: AlbumField[]) {
    const patch: Partial<TagFields> = {}
    for (const field of fields) { const value = (match as any)[field]; if (value) (patch as any)[field] = value }
    if (Object.keys(patch).length) applyTags(albumTracks(match).map(track => track.id), patch, false)
  }
  function keepAlbumField(index: number, field: string, keep: boolean) {
    const match = albums[index]; if (!match) return
    match.kept = keep ? Array.from(new Set(match.kept.concat([field]))) : match.kept.filter(name => name !== field)
    sendToShell({ type: 'albumKeep', id: sessionId, album: index, field, keep })
  }
  async function useMatchCover(match: AlbumMatch) {
    if (!match.artUrl) return
    try { const saved = await request<{ path: string; thumb: string }>({ type: 'fetchArt', id: sessionId, url: match.artUrl }); thumbs = Object.assign({}, thumbs, { [saved.path]: saved.thumb }); applyTags(albumTracks(match).map(track => track.id), { artPath: saved.path }, false) }
    catch (error) { toast('No picture for that release: ' + (error as Error).message) }
  }
  async function useAllAlbum(match: AlbumMatch) {
    useAlbumField(match, albumOffers(match).filter(offer => !offer.same && !offer.kept).map(offer => offer.field))
    if (match.artUrl && !failedCovers[match.artUrl] && !match.kept.includes('art') && !commonValue(albumTracks(match), 'artPath')) await useMatchCover(match)
  }
  // A picture of your own (a file, or pasted): on the selected tracks, else the album's tracks (else every track)
  async function chooseCover(paste: boolean, match?: AlbumMatch) {
    try {
      const result = await request<{ path: string; thumb: string; error?: string }>({ type: paste ? 'pasteArt' : 'chooseArt', id: sessionId })
      if (!result.path) { if (result.error) toast(result.error); return }
      thumbs = Object.assign({}, thumbs, { [result.path]: result.thumb })
      const keys = selectedIds.length ? selectedIds : match ? albumTracks(match).map(track => track.id) : albums.length === 1 ? albumTracks(albums[0]).map(track => track.id) : exportable.map(track => track.id)
      applyTags(keys, { artPath: result.path }, false)
      toast(`Cover set on ${keys.length} track${keys.length === 1 ? '' : 's'}${selectedIds.length ? ' (the selected ones)' : ''}`)
    } catch (error) { toast('Could not use that picture: ' + (error as Error).message) }
  }
  // Which of a release's tracks are here, which are missing
  function missingFrom(match: AlbumMatch) { const numbers = new Set(albumTracks(match).map(track => track.suggestion?.releaseId === match.releaseId ? track.suggestion.trackNumber : 0)); return match.trackTitles.filter((_, index) => !numbers.has(index + 1)) }
  // Per track: only the title as the release spells it and its number
  const trackOffers = $derived(tracks.filter(track => !track.skip && !track.suggestionDismissed && track.suggestion && ((track.suggestion.title && track.suggestion.title !== track.title) || (track.suggestion.trackNumber && (track.suggestion.trackNumber !== track.trackNumber || (track.suggestion.trackCount || 0) !== (track.trackCount || 0))))))
  const offAlbum = $derived(tracks.filter(track => !track.skip && track.offAlbum && !track.suggestionDismissed))
  function useTrackOffer(list: Track[]) {
    const next = copy()
    for (const track of next) {
      const suggestion = list.find(candidate => candidate.id === track.id)?.suggestion; if (!suggestion) continue
      if (suggestion.title) track.title = suggestion.title
      if (suggestion.trackNumber) { track.trackNumber = suggestion.trackNumber; if (suggestion.trackCount) track.trackCount = suggestion.trackCount }
      track.exported = false
    }
    commit(next, `Titles and numbers from the album on ${list.length} track${list.length === 1 ? '' : 's'}`)
  }
  function dismissSuggestion(track: Track) { const next = copy(); next.find(candidate => candidate.id === track.id)!.suggestionDismissed = true; tracks = next; save() }
  function openLookup(keys: string[], fields?: TagFields) {
    const first = tracks.find(track => keys.includes(track.id))
    lookingUp = { keys, artist: fields?.artist || first?.artist || '', title: fields?.title || first?.title || '', album: fields?.album || first?.album || '' }
    tagging = null
  }
  function applyDialog() {
    const open = dialog; dialog = null
    if (!open) return
    if (open.kind === 'fades') { const fadeIn = Math.max(0, Number(open.fadeIn) || 0), fadeOut = Math.max(0, Number(open.fadeOut) || 0); const next = copy(); for (const track of next) if (open.keys.includes(track.id)) { track.fadeInMs = Math.round(fadeIn); track.fadeOutMs = Math.round(fadeOut) } commit(next, `Fades set on ${open.keys.length} track${open.keys.length === 1 ? '' : 's'}`) }
    else if (open.kind === 'close') closeSession()
    else if (open.kind === 'redetect') redetect()
  }
  async function redetect() {
    try { const rebuilt = await request<Track[]>({ type: 'redetect', id: sessionId }); commit(rebuilt, `Found ${rebuilt.length} tracks again with the recording profile's gap settings`); clearSelection(); currentId = rebuilt[0]?.id ?? '' }
    catch (error) { toast('Redetect failed: ' + (error as Error).message) }
  }
  async function closeSession() {
    try { await request({ type: 'deleteSession', id: sessionId }); clearSelection(); nav.view = 'record'; nav.sessionId = ''; writeSetting('session', ''); toast('Session closed and its recording deleted') }
    catch (error) { toast('Could not close the session: ' + (error as Error).message) }
  }

  // ---- Listening (SPEC.md › Auditioning) ----
  function playFrom(frame: number) { auditionRun = null; playingPart = ''; sendToShell({ type: 'play', id: sessionId, segments: [[Math.max(0, Math.round(frame)), session!.totalFrames]] }) }
  function stop() { sendToShell({ type: 'stopPlayback' }) }
  function audition(which: 'starts' | 'ends', keys?: string[]) {
    const length = app.auditionSeconds * rate, segments: number[][] = []
    for (const track of targets(keys)) {
      segments.push(which === 'starts' ? [track.start, Math.min(track.end, track.start + length)] : [Math.max(track.start, track.end - length), track.end])
    }
    if (!segments.length) return
    const edge = which === 'starts' ? 'start' as const : 'end' as const, list = targets(keys)
    auditionRun = { edge, started: false, parts: segments.map((segment, index) => ({ id: list[index].id, from: segment[0], to: segment[1] })) }; playingPart = ''
    sendToShell({ type: 'play', id: sessionId, segments })
    toast(`Playing the ${which === 'starts' ? 'start' : 'end'} of ${segments.length} track${segments.length === 1 ? '' : 's'}, ${app.auditionSeconds} s each`, { ms: 3000 })
  }
  // Just one edge of the current track (or of the grabbed edge's track): for working on a start or an end without waiting through the other
  function hearEdge(edge: 'start' | 'end', trackId = '') {
    const track = (trackId ? tracks.find(candidate => candidate.id === trackId) : grab ? tracks.find(candidate => candidate.id === grab!.trackId) : null) ?? current
    if (!track) { toast('Click a track first'); return }
    const length = app.auditionSeconds * rate
    auditionRun = null; playingPart = ''
    sendToShell({ type: 'play', id: sessionId, segments: [edge === 'start' ? [track.start, Math.min(track.end, track.start + length)] : [Math.max(track.start, track.end - length), track.end]] })
    if (!trackId) showFrame(edge === 'start' ? track.start : track.end)
  }
  // Playing a run of starts or ends: the row being heard lights up and stays in view; ticking it doesn't interrupt the run
  let auditionRun = $state<null | { edge: 'start' | 'end'; started: boolean; parts: { id: string; from: number; to: number }[] }>(null)
  let playingPart = $state('')
  $effect(() => {
    const frame = app.playhead
    untrack(() => {
      if (!auditionRun) return
      if (frame < 0) { if (auditionRun.started) { auditionRun = null; playingPart = '' } return }
      const part = auditionRun.parts.find(candidate => frame >= candidate.from && frame < candidate.to)
      if (!part) return
      auditionRun.started = true
      if (part.id !== playingPart) { playingPart = part.id; focused = { trackId: part.id, edge: auditionRun.edge }; scrollRowIntoView(part.id) }
    })
  })
  // The waveform follows what's playing
  $effect(() => {
    const frame = app.playhead
    if (!playing || frame < 0 || !session) return
    untrack(() => { const span = waveWidth() * view.framesPerPixel; if (frame < view.start || frame > view.start + span * 1.01) view = waveform?.clampView({ start: frame - span * 0.1, framesPerPixel: view.framesPerPixel }) ?? view })
  })

  // ---- Export (SPEC.md › Export) ----
  const outputProfileId = $derived(session?.outputProfileId && app.outputProfiles.some(profile => profile.id === session!.outputProfileId) ? session.outputProfileId : app.activeOutput)
  // Tags worth having on every file before it goes out (SPEC.md › Export): the dialog offers to fill them in first
  const wanted: [keyof Track, string][] = [['artist', 'artist'], ['album', 'album'], ['albumArtist', 'album artist'], ['genre', 'genre']]
  let exportCheck = $state<null | { keys: string[]; missing: string; unconfirmed: number }>(null)
  // One ✓ per track confirms its start and end together (SPEC.md › Editing); click again to undo
  // The select bar: ticks all the chosen tracks, or unticks them if they're all ticked already
  function confirmTracks(keys: string[]) {
    const chosen = tracks.filter(track => keys.includes(track.id)); if (!chosen.length) return
    const all = chosen.every(track => track.startChecked && track.endChecked)
    const next = copy(); for (const track of next) if (keys.includes(track.id)) { track.startChecked = !all; track.endChecked = !all }
    commit(next, `${chosen.length} track${chosen.length === 1 ? '' : 's'} ${all ? 'not confirmed' : 'confirmed'}`)
  }
  function confirmTrack(track: Track) {
    const both = !!track.startChecked && !!track.endChecked
    const next = copy(), edited = next.find(candidate => candidate.id === track.id)!
    edited.startChecked = !both; edited.endChecked = !both
    commit(next, `Track ${tracks.indexOf(track) + 1} ${both ? 'not confirmed' : 'confirmed'}`)
  }
  // A start or end confirmed by hand: green once the user is happy with it
  function toggleChecked(track: Track, edge: 'start' | 'end') {
    const next = copy(), edited = next.find(candidate => candidate.id === track.id)!
    const on = !(edge === 'start' ? edited.startChecked : edited.endChecked)
    if (edge === 'start') edited.startChecked = on; else edited.endChecked = on
    commit(next, `Track ${tracks.indexOf(track) + 1} ${edge} ${on ? 'confirmed' : 'not confirmed'}`)
  }
  // Files already in place from an earlier export: keep them (checked first) or write them again
  let existing = $state<null | { keys: string[]; files: { trackId: string; path: string }[] }>(null)
  async function useExisting() {
    const job = existing; existing = null; if (!job) return
    const result = await request<{ adopted: number; failed: string[]; tracks: Track[] }>({ type: 'adoptExports', id: sessionId, files: job.files })
    tracks = result.tracks
    toast(`${result.adopted} existing file${result.adopted === 1 ? '' : 's'} kept${result.failed.length ? `; ${result.failed.length} failed the check and will be written again` : ''}`, { ms: 8000 })
    const rest = job.keys.filter(key => !tracks.find(track => track.id === key)?.exported)
    if (rest.length) exportTracks(rest, true, true)
  }
  async function exportTracks(keys?: string[], checked = false, existingChecked = false) {
    const chosen = targets(keys)
    if (!chosen.length) return
    if (!existingChecked) {
      clearTimeout(saveTimer); sendToShell({ type: 'saveSession', id: sessionId, tracks: $state.snapshot(tracks) })
      const files = await request<{ trackId: string; path: string }[]>({ type: 'existingExports', id: sessionId, profileId: outputProfileId, trackIds: chosen.filter(track => !track.exported).map(track => track.id) }).catch(() => [])
      if (files.length) { existing = { keys: chosen.map(track => track.id), files }; return }
    }
    if (!checked) {
      // Empty tags and unconfirmed cuts are pointed out once; exporting anyway is always possible
      const missing = wanted.filter(([field]) => chosen.some(track => !(track[field] as string | undefined))).map(([, label]) => label)
      const unconfirmed = chosen.reduce((count, track) => count + (track.startChecked ? 0 : 1) + (track.endChecked ? 0 : 1), 0)
      if (missing.length || unconfirmed) { exportCheck = { keys: chosen.map(track => track.id), missing: missing.join(', '), unconfirmed }; return }
    }
    // Tracks without a number get their place in their album (SPEC.md › Export)
    // A blank album artist becomes the album's main artist: the one most of its tracks have (SPEC.md › Export)
    if (tracks.some(track => !track.skip && !track.albumArtist)) {
      const next = copy()
      for (const group of listAlbums) {
        const members = next.filter(track => group.ids.includes(track.id) && !track.skip); if (!members.some(track => !track.albumArtist)) continue
        const counts = new Map<string, number>(); for (const track of members) if (track.artist) counts.set(track.artist, (counts.get(track.artist) ?? 0) + 1)
        const main = Array.from(counts.entries()).sort((first, second) => second[1] - first[1] || first[0].length - second[0].length)[0]?.[0] ?? ''
        if (main) for (const track of members) if (!track.albumArtist) track.albumArtist = main
      }
      commit(next, 'Album artists filled in from each album\u2019s main artist')
    }
    numberAlbums()
    clearTimeout(saveTimer); sendToShell({ type: 'saveSession', id: sessionId, tracks: $state.snapshot(tracks) })
    exportStatus = `Exporting ${chosen.length} track${chosen.length === 1 ? '' : 's'}…`
    // The export carries on in the background: the next session can be recorded meanwhile (SPEC.md › Workflow)
    toast(`Exporting ${chosen.length} track${chosen.length === 1 ? '' : 's'} in the background. You can start the next session now.`, { ms: 8000, action: { label: 'New session', key: 'N', run: () => { clearSelection(); nav.view = 'record' } } })
    try {
      const result = await request<{ done: number; verified: number; error: string; tracks: Track[]; folder: string }>({ type: 'export', id: sessionId, trackIds: chosen.map(track => track.id), profileId: outputProfileId })
      exportStatus = ''
      if (result.tracks) tracks = result.tracks
      const exportedPath = result.tracks?.find(track => chosen.some(chosenTrack => chosenTrack.id === track.id) && track.exportedPath)?.exportedPath
      if (result.error) toast(`${result.done} of ${chosen.length} written, ${result.verified} verified. ${result.error.split('\n')[0]}`, { ms: 15000 })
      else toast(`${result.done} track${result.done === 1 ? '' : 's'} written and verified`, { action: { label: 'Show in Explorer', key: 'O', run: () => sendToShell({ type: 'openFolder', path: exportedPath ?? result.folder }) } })
    } catch (error) { exportStatus = ''; toast('Export failed: ' + (error as Error).message) }
  }

  // ---- View ----
  let waveHost = $state<HTMLElement>()
  const waveWidth = () => waveHost?.clientWidth ?? 800
  function fit() { if (!session) return; view = waveform?.clampView({ start: 0, framesPerPixel: session.totalFrames / Math.max(1, waveWidth()) }) ?? view }
  function zoomToTrack(trackId: string) {
    const track = tracks.find(candidate => candidate.id === trackId); if (!track || !waveform) return
    const margin = (track.end - track.start) * 0.04
    view = waveform.clampView({ start: track.start - margin, framesPerPixel: (track.end - track.start + margin * 2) / waveWidth() })
    currentId = track.id
  }
  function showFrame(frame: number) { const span = waveWidth() * view.framesPerPixel; if (frame < view.start || frame > view.start + span) view = waveform?.clampView({ start: frame - span / 2, framesPerPixel: view.framesPerPixel }) ?? view }
  function zoomAtCursor(factor: number) { const x = (cursor - view.start) / view.framesPerPixel; waveform?.zoomAt(factor, x >= 0 && x <= waveWidth() ? x : waveWidth() / 2) }
  function goToTrack(step: number) {
    const index = tracks.findIndex(track => track.id === current?.id), target = tracks[Math.max(0, Math.min(tracks.length - 1, (index < 0 ? (step > 0 ? -1 : tracks.length) : index) + step))]
    if (!target) return
    currentId = target.id; cursor = target.start; showFrame(target.start); scrollRowIntoView(target.id)
  }
  function scrollRowIntoView(trackId: string) { requestAnimationFrame(() => document.querySelector(`[data-select-key="${trackId}"]`)?.scrollIntoView({ block: 'nearest' })) }

  // ---- Keyboard (Settings › Shortcuts lists these) ----
  const inTagsStep = () => step === 'tags'
  function onKey(event: KeyboardEvent) {
    if (!session || dialog || (event.target as HTMLElement)?.matches?.('input, textarea, select')) return
    const key = event.key, lower = key.toLowerCase()
    const step = event.ctrlKey ? 1 : Math.max(1, Math.round(view.framesPerPixel)) * (event.shiftKey ? 10 : 1)
    let handled = true
    if (event.ctrlKey && lower === 'v' && inTagsStep() && !tagging && !lookingUp) { const group = (current && albumOf(current.id)) || listAlbums[0]; if (group) chooseCoverFor(group, true) }
    else if (event.ctrlKey && lower === 'z' && !event.shiftKey) undo()
    else if (event.ctrlKey && (lower === 'y' || (lower === 'z' && event.shiftKey))) redo()
    else if (event.ctrlKey && lower === 'e') exportTracks()
    else if (event.ctrlKey && key === '0') fit()
    else if (key === ' ') { (document.activeElement as HTMLElement | null)?.blur?.(); if (playing) stop(); else playFrom(cursor) }
    else if (key === 'ArrowLeft' || key === 'ArrowRight') {
      const delta = key === 'ArrowLeft' ? -step : step
      if (grab && tracks.some(track => track.id === grab!.trackId)) { const track = tracks.find(candidate => candidate.id === grab!.trackId)!; const before = JSON.stringify($state.snapshot(tracks)); moveEdge(grab, track[grab.edge] + delta); past = past.concat([before]).slice(-200); future = []; save(); cursor = track[grab.edge]; showFrame(cursor) }
      else { cursor = Math.max(0, Math.min(session.totalFrames, cursor + delta)); showFrame(cursor) }
    }
    else if (event.ctrlKey || event.altKey) handled = false
    else if (lower === 's') setEdgeAtCursor('start')
    else if (lower === 'e') setEdgeAtCursor('end')
    else if (lower === 'x') splitAtCursor()
    else if (lower === 'n') addTrackAtCursor()
    else if (lower === 'y') confirmFocused()
    else if (lower === 'c') checkNext()
    else if (lower === 'w') setWaveMode(waveMode === 'wave' ? 'spectrogram' : 'wave')
    else if (lower === 'a') nextAudition() // A, not L: with tracks ticked, L is Look up
    else if (key === ',') { if (current) focusEdge({ trackId: current.id, edge: 'start' }) }
    else if (key === '.') { if (current) focusEdge({ trackId: current.id, edge: 'end' }) }
    else if (key === 'Enter' && grab) hearEdge(grab.edge)
    else if (lower === 'z') { if (current) zoomToTrack(current.id) }
    else if (key === '+' || key === '=') zoomAtCursor(0.5)
    else if (key === '-' || key === '_') zoomAtCursor(2)
    else if (key === '[') goToTrack(-1)
    else if (key === ']') goToTrack(1)
    else if (key === 'Home') { cursor = 0; showFrame(0) }
    else if (key === 'End') { cursor = session.totalFrames; showFrame(cursor) }
    else if (key === 'Delete') deleteTracks(selectedIds)
    else if (key === 'Escape' && grab) grab = null
    else handled = false
    if (handled) event.preventDefault()
  }

  // ---- Track list selection and its actions (the multi-select bar) ----
  // Tick a track and it takes the blue cursor as its start or end; ticking doesn't move the cursor
  async function edgeToCursor(edge: 'start' | 'end', keys: string[]) { const track = tracks.find(candidate => candidate.id === keys[0]); if (track && await setEdgeAtCursor(edge, track)) clearSelection() }
  const actions: SelectionAction[] = [
    { label: 'Start at cursor', key: 'S', single: true, run: keys => edgeToCursor('start', keys) },
    { label: 'End at cursor', key: 'E', single: true, run: keys => edgeToCursor('end', keys) },
    { label: 'Confirm', key: 'Y', run: keys => confirmTracks(keys) },
    { label: 'Tags…', key: 'T', run: keys => tagging = keys },
    { label: 'Look up…', key: 'L', run: keys => openLookup(keys) },
    { label: 'Fades…', key: 'F', run: keys => { const first = tracks.find(track => keys.includes(track.id)); dialog = { kind: 'fades', keys, fadeIn: String(first?.fadeInMs ?? 0), fadeOut: String(first?.fadeOutMs ?? 0) } } },
    { label: 'Merge', run: keys => mergeTracks(keys) },
    { label: 'Revert to captured tags…', run: keys => reverting = { keys, artist: true, title: true, album: true } },
    { label: 'Remove', key: 'Delete', danger: true, run: keys => deleteTracks(keys) },
  ]
  const exportedCount = $derived(tracks.filter(track => track.exported).length)
  // Tick boxes: the same selection as Ctrl+click, so the bar at the bottom acts on the ticked tracks
  const tick = (event: MouseEvent, ids: string[], on: boolean) => tickKeys(event, scope, ids, tracks.map(track => track.id), on, actions, 'track')
  function toggleAll() { if (selectedIds.length === tracks.length) clearSelection(); else selectKeys(scope, tracks.map(track => track.id), actions, 'track') }
  const auditionChoices: [string, string][] = [['2', '2 s'], ['5', '5 s'], ['10', '10 s'], ['20', '20 s']]
  const outputChoices = $derived(app.outputProfiles.map(profile => [profile.id, profile.name] as [string, string]).concat([['__manage', 'Manage output profiles…']]))
  // Export sizes (SPEC.md › Export): an estimate per album and for the whole session, for the profile each album will use
  const profileFor = (track: Track) => app.outputProfiles.find(profile => profile.id === track.outputProfileId) ?? app.outputProfiles.find(profile => profile.id === outputProfileId) ?? app.outputProfiles[0]
  function estimateBytes(list: Track[]) {
    const channels = session?.channels || 2, bits = session?.bitDepth === 32 ? 24 : session?.bitDepth || 24
    return list.filter(track => !track.skip).reduce((total, track) => {
      const seconds = (track.end - track.start) / rate, profile = profileFor(track), pcm = seconds * rate * channels * bits / 8
      return total + (profile?.format === 'wav' ? pcm : profile?.format === 'flac' || !profile?.format ? pcm * 0.6 : seconds * (profile?.bitrateKbps || 320) * 125)
    }, 0)
  }
  const sizeText = (bytes: number) => bytes >= 1e9 ? `${(bytes / 1e9).toFixed(2)} GB` : `${Math.max(1, Math.round(bytes / 1e6))} MB`
  // Where each track will be written and the number and comment it will carry, from the shell (same rules as the export)
  let exportPaths = $state<Record<string, { path: string; number: number; count: number; comment: string }>>({})
  let pathsTimer = 0
  $effect(() => {
    if (step !== 'export') return
    tracks; outputProfileId; app.outputProfiles
    clearTimeout(pathsTimer)
    pathsTimer = window.setTimeout(() => { untrack(() => { clearTimeout(saveTimer); sendToShell({ type: 'saveSession', id: sessionId, tracks: $state.snapshot(tracks) }); request<typeof exportPaths>({ type: 'exportPaths', id: sessionId, profileId: outputProfileId }).then(found => exportPaths = found).catch(() => { }) }) }, 300)
  })
  const folderOf = (path: string | undefined) => path ? path.split(String.fromCharCode(92)).slice(0, -1).join(String.fromCharCode(92)) : ''
  const fileOf = (path: string | undefined) => path ? path.split(String.fromCharCode(92)).pop() ?? '' : ''
  // An album whose numbers are missing or repeat is numbered 1…n by position (and n tracks); one with a full, unique set keeps it.
  // Done on export, and when the Export step opens so the report shows exactly what will be written
  function numberAlbums() {
    const renumber = listAlbums.filter(group => { const numbers = tracks.filter(track => group.ids.includes(track.id) && !track.skip).map(track => track.trackNumber || 0); return numbers.some(number => !number) || new Set(numbers).size !== numbers.length })
    if (!renumber.length) return
    const next = copy()
    for (const group of renumber) { const members = next.filter(track => group.ids.includes(track.id) && !track.skip); members.forEach((track, index) => { track.trackNumber = index + 1; track.trackCount = members.length }) }
    commit(next, 'Track numbers set by position in each album')
  }
  $effect(() => { if (step === 'export') untrack(numberAlbums) })
  // A track that has gone back to its album takes what the album already says, where it has nothing of its own
  function adoptAlbumTags() {
    const fields = ['album', 'albumArtist', 'year', 'genre', 'composer', 'artPath'] as const, next = copy(); let changed = 0
    for (const group of listAlbums.filter(candidate => candidate.joined)) {
      const members = next.filter(track => group.ids.includes(track.id))
      for (const field of fields) { const values = new Set(members.map(track => track[field] ?? '').filter(Boolean)); if (values.size === 1) { const value = Array.from(values)[0]; for (const track of members) if (!track[field]) { track[field] = value; changed++ } } }
    }
    if (changed) commit(next, 'Late takes joined their albums')
  }
  $effect(() => { if (step !== 'cuts' && listAlbums.some(group => group.joined)) untrack(adoptAlbumTags) })
  // In album order: a retake sits at its number, not at the end of the session
  const inAlbumOrder = (list: Track[]) => list.slice().sort((first, second) => (first.trackNumber || 9999) - (second.trackNumber || 9999))
  function setAlbumProfile(group: ListAlbum, profileId: string) {
    if (profileId === '__manage') { sendToShell({ type: 'openSettings', tab: 'Output profiles' }); return }
    const next = copy(); for (const track of next) if (group.ids.includes(track.id)) { track.outputProfileId = profileId === '__session' ? '' : profileId; track.exported = false }
    commit(next, profileId === '__session' ? 'Album uses the session\u2019s output profile' : `Album exports as ${app.outputProfiles.find(profile => profile.id === profileId)?.name}`)
  }
</script>

<!-- Space is the app's play/stop: it must never also press the button last clicked (that replayed an old audition) -->
<svelte:window onkeydown={onKey} onkeyup={(event) => { if (event.key === ' ' && !(event.target as HTMLElement)?.matches?.('input, textarea, select')) event.preventDefault() }} />

{#if loadError}
  <div class="empty"><h2>Can't open this session</h2><p class="quiet">{loadError}</p></div>
{:else if !session}
  <div class="empty"><p class="quiet">Opening the session{loadProgress ? `, reading the waveform ${Math.round(loadProgress * 100)}%` : ''}…</p></div>
{:else}
<div class="session">
  <header class="session-title" use:intoTitleBar>
    <input class="name" value={session.name} onchange={(event) => { const name = (event.target as HTMLInputElement).value.trim(); if (name && session) { session.name = name; sendToShell({ type: 'saveSession', id: sessionId, name }) } }} aria-label="Session name" />
    <span class="facts">{new Date(session.created).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' })} · {formatDuration(session.totalFrames / rate)} · {session.device || 'Unknown device'} · {rate / 1000} kHz {session.bitDepth === 32 ? 'float' : session.bitDepth + '-bit'} · {tracks.length} tracks{exportedCount ? `, ${exportedCount} exported` : ''}</span>
    {#if session.cutOff}<span class="clip-warning" title="The app or PC stopped while this was recording. Everything up to the last couple of seconds was saved.">Cut off by a crash or power cut at {formatTime(session.totalFrames, rate, false)}</span>{/if}
    {#if session.otherSounds?.length}<span class="clip-warning" title={session.otherSounds.map(other => `${formatTime(other.frame, rate, false)}: ${other.app}, ${(other.ms / 1000).toFixed(1)} s`).join(String.fromCharCode(10))}>{session.otherSounds.length} sound{session.otherSounds.length === 1 ? '' : 's'} from other apps</span>{/if}
    {#if session.clippedSamples > 0}<span class="clip-warning" title="Samples recorded at full scale">{session.clippedSamples} clipped samples</span>{/if}
    {#if sessionCutoff}<span class="source-chip">Source: <CutoffInfo hz={sessionCutoff} />, {describeCutoff(sessionCutoff)}</span>{/if}
    {#if session.dropouts?.length}<span class="clip-warning" title={session.dropouts.map(dropout => `${formatTime(dropout.frame, rate, false)}: ${dropout.ms} ms lost`).join('\n')}>{session.dropouts.length} dropout{session.dropouts.length === 1 ? '' : 's'} (marked in red on the ruler)</span>{/if}
  </header>

  <nav class="steps" aria-label="Steps">
    <button class="step" class:active={step === 'cuts'} class:done={unchecked.length === 0} onclick={() => step = 'cuts'}><span class="step-number">1</span><span class="step-text"><strong>Albums &amp; cuts</strong><span>{unchecked.length === 0 ? 'all confirmed' : `${unchecked.length} to confirm${doubtful.length ? `, ${doubtful.length} doubtful` : ''}`}</span></span></button>
    <button class="step" class:active={step === 'tags'} class:done={untagged.length === 0} onclick={() => step = 'tags'}><span class="step-number">2</span><span class="step-text"><strong>Fill gaps &amp; covers</strong><span>{untagged.length === 0 ? 'nothing missing' : `${untagged.length} track${untagged.length === 1 ? '' : 's'} with blanks`}</span></span></button>
    <button class="step" class:active={step === 'export'} class:done={unexported.length === 0 && exportable.length > 0} onclick={() => step = 'export'}><span class="step-number">3</span><span class="step-text"><strong>Export</strong><span>{exportable.length === 0 ? 'nothing to export' : unexported.length === 0 ? `${exportable.length} exported${unverified.length ? `, ${unverified.length} failed the check` : ', all verified'}` : `${unexported.length} of ${exportable.length} to go`}</span></span></button>
    <div class="step-actions">
      {#if step === 'cuts'}
        <button class="primary" onclick={checkNext} title="Jump to the next start or end without a ✓, from the track you're on, zoomed in, and play it (C)">Find next to confirm<kbd>C</kbd></button>
        
        <Dropdown label="Track ends" value={betweenTracks} options={[['meet', 'Meet the next start'], ['trim', 'Trim silence'], ['keep', 'Keep as recorded']]} onchange={setBetween} />
      {:else if step === 'tags'}
        {#if selectedIds.length}<button class="ghost" onclick={() => tagging = selectedIds.slice()}>Edit {selectedIds.length} selected…</button>{/if}
      {:else}
        {#if outputChoices.length}<Dropdown label="Output profile" value={outputProfileId} options={outputChoices} onchange={(value) => { if (value === '__manage') { sendToShell({ type: 'openSettings', tab: 'Output profiles' }); return } if (session) session.outputProfileId = value; sendToShell({ type: 'saveSession', id: sessionId, outputProfileId: value }) }} />{/if}
        <span class="export-size" title="Estimated size of everything this session exports, with each album's profile (FLAC from the usual ratio, MP3 and AAC from the bitrate)">≈ {sizeText(estimateBytes(exportable))}</span>
        {#if exportingHere}<button class="primary danger-button" onclick={() => sendToShell({ type: 'cancelExport' })} title="Stop after the track being written (it's finished or removed, never left half-written)">Cancel export ({app.status?.exportDone}/{app.status?.exportTotal})</button>{:else if queued}<button class="primary" onclick={() => sendToShell({ type: 'unqueueExport', id: sessionId })} title="Another session is exporting; this one starts after it. Click to take it out of the queue">Export queued: click to unqueue</button>{:else}        <button class="primary" disabled={!exportable.length} onclick={() => exportTracks(selectedIds.length ? selectedIds : unexported.length ? unexported.map(track => track.id) : undefined)} title="Ctrl+E"><Icon name="export" size={16} />{selectedIds.length ? `Export ${selectedIds.length} selected` : unexported.length && unexported.length < exportable.length ? `Export the ${unexported.length} left` : 'Export all'}</button>{#if selectedIds.length}<button class="ghost" onclick={() => exportTracks((unexported.length ? unexported : exportable).map(track => track.id))} title="Ignore the ticks and export everything">{unexported.length && unexported.length < exportable.length ? `Export the ${unexported.length} left` : 'Export all'}</button>{/if}{/if}
      {/if}
    </div>
  </nav>
  <div class="toolbar">
    <button class="ghost" class:on={playing} onclick={() => playing ? stop() : playFrom(cursor)} title="Play from the playhead: click in the wave or on a track to move it (Space)"><Icon name={playing ? 'stop' : 'play'} size={16} />{playing ? 'Stop' : 'Play'}<kbd>Space</kbd></button>
    {#if step === 'cuts'}
    <div class="group">
      <Dropdown label="Audition" shortcut="A" value={String(app.auditionSeconds)} options={auditionChoices} align="left" onchange={(value) => setAudition(Number(value))} />
      <span class="group-label">{selectedIds.length ? 'Selected' : 'All tracks'}</span>
      <button class="ghost" onclick={() => audition('starts')} title="Run through the start of every track (selected, or all)">Starts</button>
      <button class="ghost" onclick={() => audition('ends')} title="Run through the end of every track">Ends</button>
    </div>
    <div class="group">
      <button class="ghost icon" onclick={() => zoomAtCursor(0.5)} title="Zoom in (+, or the wheel)"><Icon name="zoom-in" size={16} /><kbd>+</kbd></button>
      <button class="ghost icon" onclick={() => zoomAtCursor(2)} title="Zoom out (-)"><Icon name="zoom-out" size={16} /><kbd>-</kbd></button>
      <button class="ghost icon" onclick={fit} title="Whole session (Ctrl+0)"><Icon name="fit" size={16} /><kbd>Ctrl+0</kbd></button>
      <button class="ghost" class:on={snapToZero} onclick={() => { snapToZero = !snapToZero; writeSetting('snapZero', snapToZero ? '1' : '') }} title="Edges land on the nearest zero crossing">Snap to zero</button>
      <button class="ghost" class:on={autoHeight} onclick={() => { autoHeight = !autoHeight; writeSetting('autoHeight', autoHeight ? '1' : '0') }} title="Zoomed in, the wave grows to fill the height, so quiet starts and fades show clearly. Ctrl+wheel sets the height by hand; clicking this goes back to automatic">Auto height</button>
    </div>
    {:else}
    <div class="group">
      <button class="ghost icon" onclick={() => zoomAtCursor(0.5)} title="Zoom in (+, or the wheel)"><Icon name="zoom-in" size={16} /></button>
      <button class="ghost icon" onclick={() => zoomAtCursor(2)} title="Zoom out (-)"><Icon name="zoom-out" size={16} /></button>
      <button class="ghost icon" onclick={fit} title="Whole session (Ctrl+0)"><Icon name="fit" size={16} /></button>
    </div>
    {/if}
    <div class="group">
      <button class="ghost icon" onclick={undo} disabled={!canUndo} title="Undo (Ctrl+Z)"><Icon name="undo" size={16} /><kbd>Ctrl+Z</kbd></button>
      <button class="ghost icon" onclick={redo} disabled={!canRedo} title="Redo (Ctrl+Y)"><Icon name="redo" size={16} /><kbd>Ctrl+Y</kbd></button>
      <button class="ghost" onclick={openHistory} title="Every earlier state of this track list, to go back to">History…</button>
      <button class="ghost" disabled={!!app.status?.recording} onclick={addToRecording} title="Go back to recording and add more to the end of this session, for example a track that went wrong. Your cuts and tags stay as they are">Add to this recording</button>
    </div>
    {#if step === 'cuts'}
    <div class="group">
      <button class="ghost icon" onclick={splitAtCursor} title="Split at the playhead (X)"><Icon name="split" size={16} /><kbd>X</kbd></button>
      <button class="ghost icon" onclick={() => dialog = { kind: 'redetect' }} title="Find the tracks again with the recording profile's gap settings"><Icon name="redetect" size={16} /></button>
    </div>
    {/if}
    <div class="spacer"></div>
    {#if exportStatus}<span class="export-status" title={exportStatus}>{exportStatus}</span>{/if}
  </div>

  {#if step === 'cuts'}
  <Hint id="waveform-cuts" text="Find next to confirm (C) jumps to the next start or end without a ✓, from the track you're on, and plays it. Drag the line, nudge it with ← →, or press S / E to set it at the playhead; then tick ✓ beside it in the list. Wheel zooms, Shift+wheel scrolls, Ctrl+wheel sets the height, W switches wave and spectrogram." />
  <div class="wave-host" bind:this={waveHost} style:height="{waveHeight}px">

    <Waveform bind:this={waveform} bind:view {sessionId} sampleRate={rate} channels={session.channels} totalFrames={session.totalFrames} {tracks} changes={session.changes} dropouts={session.dropouts ?? []} mode={waveMode} {autoHeight} {selectedIds} currentId={current?.id ?? ''} {cursor} playhead={playing ? app.playhead : -1} {grab}
      onCursor={(frame) => { cursor = frame; grab = null; if (playing) playFrom(frame) }} {onEdge} {onEdgeDone} onRelease={() => grab = null} onPickTrack={(trackId, additive) => { currentId = trackId; if (additive) selectKeys(scope, Array.from(new Set(selectedIds.concat([trackId]))), actions, 'track'); scrollRowIntoView(trackId) }} onZoomTrack={zoomToTrack} />
  </div>
  <div class="readout"><div class="wave-mode" role="group" aria-label="Wave or spectrogram">
      <button class:on={waveMode === 'wave'} onclick={() => setWaveMode('wave')} title="Waveform: levels, starts and ends (W switches)">Wave</button><button class:on={waveMode === 'spectrogram'} onclick={() => setWaveMode('spectrogram')} title="Spectrogram: frequency over time. A flat top edge means a lossy source; a vertical gap is a dropout (W switches)">Spectrogram</button><kbd>W</kbd>
    </div><span>Playhead {formatTime(cursor, rate)}</span>{#if view.framesPerPixel < 2}<span>sample {cursor.toLocaleString()}</span>{/if}{#if current}<span>Track {tracks.indexOf(current) + 1}: {formatTime(current.start, rate)} – {formatTime(current.end, rate)}</span>{/if}{#if grab}<span class="grabbed">{grab.edge === 'start' ? 'Start' : 'End'} grabbed: ← → nudge, Enter hear it, Esc let go</span>{/if}<span class="key"><i class="swatch start"></i>track start <i class="swatch end"></i>track end <i class="swatch change"></i>name changed while recording</span></div>
  <div class="splitter" role="separator" aria-orientation="horizontal" aria-label="Drag to share the height between the wave and the track list" title="Drag to make the wave or the track list bigger" onpointerdown={splitDown} onpointermove={splitMove} onpointerup={splitUp} onpointercancel={splitUp}><span></span></div>
  {/if}

  {#if step === 'tags'}
    <!-- Tags (SPEC.md › Tags): one card per album, as a table of your values beside the database's; click theirs to use it -->
    <div class="tags-page">
      <div class="tags-summary">
        <strong class:done={albumsReady === listAlbums.length}>{albumsReady === listAlbums.length ? '✓ ' : ''}{albumsReady} of {listAlbums.length} albums complete</strong>
        {#if !session.albumLooked}<span class="quiet">Looking your albums up on MusicBrainz{albumProgress ? ` (${albumProgress.done} of ${albumProgress.total} tracks)` : ''}…</span>{/if}
        <span class="spacer"></span>
        <label class="only-needy"><input type="checkbox" bind:checked={onlyNeedy} /> Only show what still needs something</label>
      </div>
      {#each listAlbums as group, groupIndex (group.key + group.first)}
        {@const list = inAlbumOrder(tracks.filter(track => group.ids.includes(track.id)))}
        {@const match = matchFor(group)}
        {@const rows = fieldRows(list, match)}
        {@const ownCover = commonOf(list, 'artPath')}
        {@const playerCover = commonest(list.map(track => track.playerArtPath ?? '').filter(path => path && thumbs[path]))}
        {@const inUse = coverInUse(group, ownCover, playerCover)}
        {@const found = match ? match.trackTitles.filter(title => list.some(track => sameTitle(track.title, title))).length : 0}
        {@const needs = albumNeeds(group)}
        {@const choices = coverChoices(match, playerCover, ownCover, inUse)}
        {@const inUseChoice = choices.find(choice => choice.inUse)}
        {@const inUseKey = inUse === 'database' ? ownCover ?? '' : inUseChoice?.key ?? ''}
        {@const inUseInfo = artInfos[inUseKey]}
        {@const main = mainArtist(list)}
        {#if !onlyNeedy || needs.length}
        <section class="tag-card" class:dropping={dropAlbum === group.first} role="group" aria-label="Album"
          ondragover={(event) => { if (event.dataTransfer?.types.includes('Files')) { event.preventDefault(); dropAlbum = group.first } }}
          ondragleave={(event) => { if (!(event.currentTarget as HTMLElement).contains(event.relatedTarget as Node)) dropAlbum = '' }}
          ondrop={(event) => { event.preventDefault(); dropAlbum = ''; const file = Array.from(event.dataTransfer?.files ?? []).find(candidate => /image\/(jpeg|png)/.test(candidate.type)); if (file) dropCover(group, file); else toast('Drop a JPEG or PNG picture') }}>
          <header class="tag-head">
            <div class="tag-title">
              <h4>{group.album || 'No album'}<span class="quiet">{' - '}{group.artist || 'unknown artist'}</span></h4>
              <p class="quiet">{list.filter(track => !track.skip).length} tracks{#if match}{' · '}{match.source}: {found} of its {match.trackTitles.length} tracks here{:else if session.albumLooked}{' · '}not found online{/if}</p>
            </div>
            {#if needs.length}<span class="album-state needs" title="Still to fill in">Missing: {needs.join(', ')}</span>{:else}<span class="album-state complete">✓ Complete</span>{/if}
          </header>
          <div class="tag-layout">
            <div class="tag-panel cover-panel">
              <span class="panel-title">Cover</span>
              <button class="cover-main" class:empty={!inUseChoice && !ownCover} onclick={() => choices.length ? openCompare(group, choices, ownCover) : chooseCoverFor(group, false)} title={choices.length ? 'Compare the covers at full size' : 'Choose a picture'}>
                {#if ownCover && thumbs[ownCover]}<img src={thumbs[ownCover]} alt="Cover in use" />{:else if inUseChoice?.src}<img src={inUseChoice.src} alt="Cover in use" />{:else}<span>No cover yet</span>{/if}
              </button>
              <span class="cover-facts">{#if inUseInfo}<span class:low={inUseInfo.width < 500}>{inUseInfo.width} × {inUseInfo.height}</span> · {artSize(inUseInfo.bytes)}{:else if ownCover}&nbsp;{:else}Pick one below{/if}</span>
              <div class="cover-others">
                {#each choices as choice (choice.key)}
                  {@const info = artInfos[choice.key]}
                  <button class="cover-other" class:chosen={choice.inUse} title={choice.inUse ? `${choice.label}: in use` : `Use ${choice.label}'s cover`} onclick={() => useChoice(group, choice)}>
                    {#if choice.src}<img src={choice.src} alt={choice.label} onerror={() => { if (choice.url) failedCovers = Object.assign({}, failedCovers, { [choice.url]: true }) }} />{/if}
                    <span class="other-label">{choice.label}</span>
                    <span class="other-facts" class:low={!!info && info.width < 500}>{info ? `${info.width}×${info.height}` : ' '}</span>
                  </button>
                {/each}
                <button class="cover-other add" title="Your own picture: click to choose a file, right-click to paste one. You can also drop a picture anywhere on this album" onclick={() => chooseCoverFor(group, false)} oncontextmenu={(event) => { event.preventDefault(); chooseCoverFor(group, true) }}><span class="plus">+</span><span class="other-label">Your own</span><span class="other-facts">Choose</span></button>
              </div>
              {#if choices.length > 0}<button class="ghost compare-button" onclick={() => openCompare(group, choices, ownCover)}>Compare at full size</button>{/if}
            </div>
            <div class="tag-sections">
              <div class="tag-panel">
                <div class="tag-row head">
                  <span class="panel-title span-label">Applies to all tracks</span>
                  <span></span>
                  <span class="online-head"><span class="source-name">{match ? match.source : 'Online'}</span><span class="spacer"></span>{#if match && rows.some(row => row.theirs && !row.mine)}<button class="small primary" onclick={() => fillBlanksFrom(group, match)} title="Only fields you've left empty; yours stay as they are">Fill blanks</button>{/if}<button class="small ghost" onclick={() => openLookup(group.ids)} title="Search MusicBrainz, iTunes or Discogs for this album yourself">{match ? 'Wrong album?' : 'Search…'}</button></span>
                </div>
                {#each rows as row (row.field)}
                  <div class="tag-row">
                    <span class="row-label">{row.label}</span>
                    <input class="wide" class:need={row.field === 'artist' ? row.mine === '' : !row.mine} value={row.mine ?? ''} placeholder={row.mine === null ? 'Differs between tracks' : `No ${row.label.toLowerCase()}`} onchange={(event) => setGroupField(group, row.field, (event.target as HTMLInputElement).value.trim())} />
                    {#if row.field === 'artist' && row.mine === null}<span class="state">Varies</span>{:else if !row.mine}<span class="state need">{row.mine === null ? 'Mixed' : 'Missing'}</span>{:else if row.theirs && !row.same}<span class="state differs">Differs</span>{:else}<span class="state good">✓</span>{/if}
                    <span class="offer">
                      {#if row.theirs}<button class="chip" class:same={row.same} disabled={row.same} onclick={() => { if (row.field === 'albumArtist' && main.toLowerCase() === row.theirs.toLowerCase()) sameAlbumArtist(group); else setGroupField(group, row.field, row.theirs) }} title={row.same ? 'Yours already matches' : `Use “${row.theirs}”`}>{row.same ? '✓ ' : '← '}{row.theirs}</button>{/if}
                      {#if row.field === 'albumArtist' && main && main !== row.mine && main.toLowerCase() !== (row.theirs ?? '').toLowerCase()}<button class="chip" onclick={() => sameAlbumArtist(group)} title={`Set the album artist of every track to ${main}, the artist most of its tracks have`}>← {main} <i>main artist</i></button>{/if}
                    </span>
                  </div>
                {/each}
              </div>
              <div class="tag-panel">
                <div class="tag-row head tracks-head-row">
                  <span class="panel-title">Each track</span>
                  <span class="column-title">Artist</span><span class="column-title">Title</span><span></span>
                  <span class="online-head"><span class="source-name">{match ? `${match.source} title` : ''}</span><span class="spacer"></span>{#if match && list.some(track => theirTitle(track, match) && theirTitle(track, match) !== track.title)}<button class="small ghost" onclick={() => useAllTitles(list, match)}>Use all</button>{/if}</span>
                </div>
                {#each list as track (track.id)}
                  {@const theirs = match ? theirTitle(track, match) : ''}
                  {@const blank = !track.artist || !track.title}
                  <div class="tag-row" class:left-out={track.skip}>
                    <span class="row-label number-cell">{track.trackNumber || list.indexOf(track) + 1}</span>
                    <input class:need={!track.artist && !track.skip} value={track.artist} placeholder="No artist" onchange={(event) => editField(track, 'artist', (event.target as HTMLInputElement).value.trim())} />
                    <input class:need={!track.title && !track.skip} value={track.title} placeholder="No title" onchange={(event) => editField(track, 'title', (event.target as HTMLInputElement).value.trim())} />
                    {#if track.skip}<span class="state">Left out</span>{:else if blank}<span class="state need">Missing</span>{:else if theirs && theirs !== track.title}<span class="state differs">Differs</span>{:else}<span class="state good">✓</span>{/if}
                    <span class="offer">{#if theirs}<button class="chip" class:same={theirs === track.title} disabled={theirs === track.title} onclick={() => editField(track, 'title', theirs)} title={theirs === track.title ? 'Yours already matches' : `Use “${theirs}”`}>{theirs === track.title ? '✓ ' : '← '}{theirs}</button>{:else if match}<span class="quiet not-listed">Not on this release</span>{/if}</span>
                  </div>
                {/each}
                {#if match && match.trackTitles.length && found < match.trackTitles.length}{@const absent = match.trackTitles.filter(title => !list.some(track => sameTitle(track.title, title)))}<p class="quiet missing">{absent.length > 8 ? `${absent.length} other tracks on this release aren't here` : `On the release but not here: ${absent.join(', ')}`}</p>{/if}
              </div>
            </div>
          </div>
        </section>
        {/if}
      {/each}
      {#if session.albumLooked}<p class="tags-foot"><button class="link" onclick={() => { if (session) session.albumLooked = false; sendToShell({ type: 'suggestAgain', id: sessionId }) }}>Look all albums up again</button></p>{/if}
    </div>
  {:else if step === 'export'}
    <!-- Export report (SPEC.md › Export): one page, album by album, every tag that will be written, editable to the last minute -->
    <div class="tags-page export-page">
      <p class="export-total"><strong>{exportable.length} track{exportable.length === 1 ? '' : 's'} in {listAlbums.length} album{listAlbums.length === 1 ? '' : 's'}</strong>{' · '}≈ {sizeText(estimateBytes(exportable))} in all <button class="ghost small-button" onclick={() => sendToShell({ type: 'openOutputFolder', profileId: outputProfileId })} title="Open the output profile's folder, where all the album folders go">📂 Open output folder</button></p>
      {#each listAlbums as group (group.key + group.first)}
        {@const list = inAlbumOrder(tracks.filter(track => group.ids.includes(track.id)))}
        {@const own = commonOf(list, 'outputProfileId')}
        {@const allTicked = group.ids.every(id => selectedIds.includes(id))}
        {@const folders = Array.from(new Set(list.filter(track => !track.skip).map(track => folderOf(exportPaths[track.id]?.path))))}
        <section class="report-card">
          <header class="report-head">
            <input type="checkbox" class="tick" checked={allTicked} indeterminate={!allTicked && group.ids.some(id => selectedIds.includes(id))} onclick={(event) => { event.stopPropagation(); tick(event, group.ids, !allTicked) }} title="Tick every track in this album" aria-label="Select this album's tracks" />
            <span class="report-title" title="The folder this album is written to, inside the output profile's folder">📁 <strong>{folders.length ? folders.join('  ·  ') || '(the output folder itself)' : (group.album || 'No album')}</strong></span>
            {#if list.some(track => track.exported && track.exportedPath)}<button class="ghost small-button" onclick={() => sendToShell({ type: 'openFolder', path: list.find(track => track.exported && track.exportedPath)?.exportedPath ?? '' })} title="Open this album's folder in Explorer">📂 Open folder</button>{/if}
            <span class="plan-size">≈ {sizeText(estimateBytes(list))}</span><span class="head-spacer"></span>
            <select class="report-profile" value={own ? own : '__session'} onchange={(event) => setAlbumProfile(group, (event.target as HTMLSelectElement).value)} title="This album's output profile">
              <option value="__session">{app.outputProfiles.find(profile => profile.id === outputProfileId)?.name ?? 'Default'} (session)</option>
              {#each app.outputProfiles as profile (profile.id)}<option value={profile.id}>{profile.name}</option>{/each}
              <option value="__manage">Manage output profiles…</option>
            </select>
            <button class="ghost small-button" disabled={exportingHere || queued} onclick={() => exportTracks(group.ids.filter(id => !tracks.find(track => track.id === id)?.skip))}>Export album</button>
          </header>
          <table class="tag-table report">
            <thead><tr><th class="c-tick"></th><th class="c-no" title="Track number / tracks in the album, as written">No.</th><th>Album artist</th><th>Artist (per track)</th><th>Title</th><th>Album</th><th class="c-year">Year</th><th>Genre</th><th class="c-disc" title="Disc number (blank: not written)">Disc</th><th>Composer</th><th>Comment</th><th class="c-art">Cover</th><th class="c-file">File</th><th class="c-status"></th></tr></thead>
            <tbody>
              {#each list as track, position (track.id)}
                <tr class:skipped-row={track.skip} class:exporting-now={exportingFile !== '' && fileOf(exportPaths[track.id]?.path) === exportingFile} data-report-row={track.id}>
                  <td class="c-tick"><input type="checkbox" class="tick" checked={selectedIds.includes(track.id)} onclick={(event) => { event.stopPropagation(); tick(event, [track.id], !selectedIds.includes(track.id)) }} aria-label="Select track" /></td>
                  <td class="c-no"><span class="no-pair"><input class="track-number" value={exportPaths[track.id]?.number || track.trackNumber || position + 1} onchange={(event) => setTrackNumber(track, (event.target as HTMLInputElement).value)} title="Track number written to the file: its place in the album unless you type another" /><span class="quiet">/{exportPaths[track.id]?.count || list.length}</span></span></td>
                  <td><input value={track.albumArtist ?? ''} placeholder={`(${list.map(member => member.artist).filter(Boolean)[0] ?? 'artist'})`} title="Blank: the album's main artist is written" onchange={(event) => editField(track, 'albumArtist', (event.target as HTMLInputElement).value.trim())} /></td>
                  <td><input value={track.artist} placeholder="Artist" onchange={(event) => editField(track, 'artist', (event.target as HTMLInputElement).value.trim())} /></td>
                  <td><input value={track.title} placeholder="Title" onchange={(event) => editField(track, 'title', (event.target as HTMLInputElement).value.trim())} /></td>
                  <td><input value={track.album} placeholder="Album" onchange={(event) => editField(track, 'album', (event.target as HTMLInputElement).value.trim())} /></td>
                  <td class="c-year"><input value={track.year ?? ''} placeholder="Year" onchange={(event) => editField(track, 'year', (event.target as HTMLInputElement).value.trim())} /></td>
                  <td><input value={track.genre ?? ''} placeholder="Genre" onchange={(event) => editField(track, 'genre', (event.target as HTMLInputElement).value.trim())} /></td>
                  <td class="c-disc"><input value={track.discNumber || ''} placeholder="-" onchange={(event) => editNumberField(track, 'discNumber', (event.target as HTMLInputElement).value)} /></td>
                  <td><input value={track.composer ?? ''} placeholder="Composer" onchange={(event) => editField(track, 'composer', (event.target as HTMLInputElement).value.trim())} /></td>
                  <td><input value={track.comment ?? ''} placeholder={exportPaths[track.id]?.comment || 'Comment'} title={exportPaths[track.id]?.comment && !track.comment ? `Blank: the output profile's comment (“${exportPaths[track.id]?.comment}”) is written` : 'Comment'} onchange={(event) => editComment(track, (event.target as HTMLInputElement).value.trim())} /></td>
                  <td class="c-art">{#if track.artPath && thumbs[track.artPath]}<img src={thumbs[track.artPath]} alt="Cover" />{:else}<span class="quiet">none</span>{/if}</td>
                  <td class="c-file quiet" title={exportPaths[track.id]?.path}>{fileOf(exportPaths[track.id]?.path)}</td>
                  <td class="c-status">{#if track.verifyError}<button class="failed no-hold" title={`${track.verifyError}. Export it again.`} onclick={() => exportTracks([track.id], true)}>Failed check</button>{:else if track.skip}<span class="quiet">left out</span>{:else if track.exported}<button class="exported no-hold" title={`${track.exportedPath}${track.verified ? ' · read back and checked' : ''}`} onclick={() => sendToShell({ type: 'openFolder', path: track.exportedPath })}>Exported</button>{/if}</td>
                </tr>
              {/each}
            </tbody>
          </table>
        </section>
      {/each}
    </div>
  {:else}
  <div class="list" data-row-list use:selectable={{ scope, noun: 'track', actions }} style:--columns={gridColumns}>
    <div class="row head" ondblclick={resetColumns} title="Drag a column's right edge to resize it; double-click here to reset"><span><input type="checkbox" class="tick" checked={tracks.length > 0 && selectedIds.length === tracks.length} indeterminate={selectedIds.length > 0 && selectedIds.length < tracks.length} onchange={toggleAll} title="Select all tracks" aria-label="Select all tracks" /><i class="col-resize" role="separator" aria-label="Resize column" onpointerdown={(event) => resizeDown(0, event)} onpointermove={resizeMove} onpointerup={resizeUp} onpointercancel={resizeUp}></i></span><span class="head-cell">Start · Length · ✓<i class="col-resize" role="separator" aria-label="Resize column" onpointerdown={(event) => resizeDown(1, event)} onpointermove={resizeMove} onpointerup={resizeUp} onpointercancel={resizeUp}></i></span><span class="head-cell"><i class="col-resize" role="separator" aria-label="Resize column" onpointerdown={(event) => resizeDown(2, event)} onpointermove={resizeMove} onpointerup={resizeUp} onpointercancel={resizeUp}></i></span><span class="head-cell">Artist<i class="col-resize" role="separator" aria-label="Resize column" onpointerdown={(event) => resizeDown(3, event)} onpointermove={resizeMove} onpointerup={resizeUp} onpointercancel={resizeUp}></i></span><span class="head-cell">Album<i class="col-resize" role="separator" aria-label="Resize column" onpointerdown={(event) => resizeDown(4, event)} onpointermove={resizeMove} onpointerup={resizeUp} onpointercancel={resizeUp}></i></span><span class="head-cell">Year<i class="col-resize" role="separator" aria-label="Resize column" onpointerdown={(event) => resizeDown(5, event)} onpointermove={resizeMove} onpointerup={resizeUp} onpointercancel={resizeUp}></i></span><span class="head-cell">No.<i class="col-resize" role="separator" aria-label="Resize column" onpointerdown={(event) => resizeDown(6, event)} onpointermove={resizeMove} onpointerup={resizeUp} onpointercancel={resizeUp}></i></span><span class="head-cell">Title<i class="col-resize" role="separator" aria-label="Resize column" onpointerdown={(event) => resizeDown(7, event)} onpointermove={resizeMove} onpointerup={resizeUp} onpointercancel={resizeUp}></i></span><span class="head-cell">Genre<i class="col-resize" role="separator" aria-label="Resize column" onpointerdown={(event) => resizeDown(8, event)} onpointermove={resizeMove} onpointerup={resizeUp} onpointercancel={resizeUp}></i></span><span class="head-cell">Composer<i class="col-resize" role="separator" aria-label="Resize column" onpointerdown={(event) => resizeDown(9, event)} onpointermove={resizeMove} onpointerup={resizeUp} onpointercancel={resizeUp}></i></span><span class="head-cell">Source<i class="col-resize" role="separator" aria-label="Resize column" onpointerdown={(event) => resizeDown(10, event)} onpointermove={resizeMove} onpointerup={resizeUp} onpointercancel={resizeUp}></i></span><span class="head-cell">Fades<i class="col-resize" role="separator" aria-label="Resize column" onpointerdown={(event) => resizeDown(11, event)} onpointermove={resizeMove} onpointerup={resizeUp} onpointercancel={resizeUp}></i></span><span class="head-cell"></span></div>
    {#each tracks as track, index (track.id)}
      {@const cutoff = cutoffFor(track.start, track.end)}
      {@const group = albumOf(track.id)}
      {#if group && group.first === track.id}
        {@const groupIndex = listAlbums.indexOf(group)}
        {@const isOpen = true}
        {@const allTicked = group.ids.every(id => selectedIds.includes(id))}
        {@const confirmed = group.ids.filter(id => { const member = tracks.find(candidate => candidate.id === id); return member?.startChecked && member?.endChecked }).length}
        {#if isOpen}
          <div class="album-row open row-grid" class:lifted={liftedAlbum === group.first} role="group" aria-label={albumName(group)} data-drop={'album:' + group.first} class:over={dropOn === 'album:' + group.first} onpointerdown={(event) => liftAlbumDown(group, event)} title="Press and hold here to pick up the whole album, then drop it on the album above or below to merge them">
            <span class="album-label" title="Press and hold here to pick up the whole album, then drop it on the album above or below to merge them"><input type="checkbox" class="tick no-hold" checked={allTicked} indeterminate={!allTicked && group.ids.some(id => selectedIds.includes(id))} onclick={(event) => { event.stopPropagation(); tick(event, group.ids, !allTicked) }} aria-label="Select this album's tracks" /><span class="album-name"><strong>{group.album || 'No album'}</strong> - {group.artist || 'unknown artist'}{group.year ? ` (${group.year})` : ''}</span><span class="quiet album-count">{group.ids.length} · {confirmed} ✓</span></span>
            <span class="head-field cell-artist"><input class="field" value={group.artist} placeholder="Artist" title="Artist and album artist for all its tracks (Enter applies)" onchange={(event) => setAlbumField(group, 'artist', (event.target as HTMLInputElement).value.trim())} onkeydown={(event) => { if (event.key === 'Enter') { const value = (event.target as HTMLInputElement).value.trim(); (event.target as HTMLInputElement).blur(); setAlbumField(group, 'artist', value) } }} /><button class="fill-down" onclick={(event) => { const input = (event.currentTarget as HTMLElement).previousElementSibling as HTMLInputElement; setAlbumField(group, 'artist', input.value.trim()) }} title="Put this artist (and album artist) on every track in the album (Enter does the same)">↓</button></span>
            <span class="head-field cell-album"><input class="field" value={group.album} placeholder="Album" title="This album's name, for all its tracks (Enter applies)" onchange={(event) => setAlbumField(group, 'album', (event.target as HTMLInputElement).value.trim())} onkeydown={(event) => { if (event.key === 'Enter') { const value = (event.target as HTMLInputElement).value.trim(); (event.target as HTMLInputElement).blur(); setAlbumField(group, 'album', value) } }} /><button class="fill-down" onclick={(event) => { const input = (event.currentTarget as HTMLElement).previousElementSibling as HTMLInputElement; setAlbumField(group, 'album', input.value.trim()) }} title="Put this album on every track in the album (Enter does the same)">↓</button></span>
            <span class="head-field cell-year"><input class="album-year" value={group.year} placeholder="Year" title="This album's year, for all its tracks (Enter applies)" onchange={(event) => setAlbumField(group, 'year', (event.target as HTMLInputElement).value.trim())} onkeydown={(event) => { if (event.key === 'Enter') { const value = (event.target as HTMLInputElement).value.trim(); (event.target as HTMLInputElement).blur(); setAlbumField(group, 'year', value) } }} /><button class="fill-down" onclick={(event) => { const input = (event.currentTarget as HTMLElement).previousElementSibling as HTMLInputElement; setAlbumField(group, 'year', input.value.trim()) }} title="Put this year on every track in the album (Enter does the same)">↓</button></span>
            <span class="head-field cell-genre"><input class="field" value={group.genre} placeholder="Genre" title="This album's genre, for all its tracks (Enter applies)" onchange={(event) => setAlbumField(group, 'genre', (event.target as HTMLInputElement).value.trim())} onkeydown={(event) => { if (event.key === 'Enter') { const value = (event.target as HTMLInputElement).value.trim(); (event.target as HTMLInputElement).blur(); setAlbumField(group, 'genre', value) } }} /><button class="fill-down" onclick={(event) => { const input = (event.currentTarget as HTMLElement).previousElementSibling as HTMLInputElement; setAlbumField(group, 'genre', input.value.trim()) }} title="Put this genre on every track in the album (Enter does the same)">↓</button></span>
            <span class="head-field cell-composer"><input class="field" value={group.composer} placeholder="Composer" title="This album's composer, for all its tracks (Enter applies)" onchange={(event) => setAlbumField(group, 'composer', (event.target as HTMLInputElement).value.trim())} onkeydown={(event) => { if (event.key === 'Enter') { const value = (event.target as HTMLInputElement).value.trim(); (event.target as HTMLInputElement).blur(); setAlbumField(group, 'composer', value) } }} /><button class="fill-down" onclick={(event) => { const input = (event.currentTarget as HTMLElement).previousElementSibling as HTMLInputElement; setAlbumField(group, 'composer', input.value.trim()) }} title="Put this composer on every track in the album (Enter does the same)">↓</button></span>
          </div>

        {/if}
      {/if}
      {#if group}
      <div class="row" class:lifting={lifting === track.id} class:lifted={lifted === track.id} onpointerdown={(event) => liftDown(track, event)} class:current={track.id === current?.id} class:playing-now={playingPart === track.id} data-select-key={track.id} role="button" tabindex="-1" onclick={() => { currentId = track.id; cursor = track.start; showFrame(track.start) }} ondblclick={() => zoomToTrack(track.id)} onkeydown={() => { }}>
        <span class="number"><input type="checkbox" class="tick no-hold" checked={selectedIds.includes(track.id)} onclick={(event) => { event.stopPropagation(); tick(event, [track.id], !selectedIds.includes(track.id)) }} aria-label="Select track {index + 1}" /> {index + 1}</span>
        <span class="hear no-hold">
          <button class="hear-start" class:checked={track.startChecked} class:doubt={!track.startSure && !track.startChecked} class:heard={focused?.trackId === track.id && focused.edge === 'start'} class:grabbed={grab?.trackId === track.id && grab.edge === 'start'} onclick={(event) => { event.stopPropagation(); focusEdge({ trackId: track.id, edge: 'start' }) }} title={`Hear the start, zoomed in. ${track.startNote || ''}${!track.startSure && !track.startChecked ? '. Amber: no clear gap here, so this cut is a best guess' : ''}`} aria-label="Hear start"><Icon name="play" size={10} /><span class="pill-time">{formatTime(track.start, rate, false)}</span>{#if track.detectedStart != null && track.detectedStart >= 0 && track.start !== track.detectedStart}<span class="pill-revert" role="button" tabindex="-1" onclick={(event) => { event.stopPropagation(); revertEdge(track, 'start') }} onkeydown={() => { }} title={`Moved. Click to put the start back where it was detected (${formatTime(track.detectedStart, rate, false)})`}>↺</span>{/if}</button><button class="hear-end" class:checked={track.endChecked} class:doubt={!track.endSure && !track.endChecked} class:heard={focused?.trackId === track.id && focused.edge === 'end'} class:grabbed={grab?.trackId === track.id && grab.edge === 'end'} onclick={(event) => { event.stopPropagation(); focusEdge({ trackId: track.id, edge: 'end' }) }} title={`Hear the end, zoomed in. Length ${formatDuration((track.end - track.start) / rate)}. ${track.endNote || ''}${!track.endSure && !track.endChecked ? '. Amber: no clear gap here, so this cut is a best guess' : ''}`} aria-label="Hear end">{#if track.detectedEnd != null && track.detectedEnd >= 0 && track.end !== track.detectedEnd}<span class="pill-revert" role="button" tabindex="-1" onclick={(event) => { event.stopPropagation(); revertEdge(track, 'end') }} onkeydown={() => { }} title={`Moved. Click to put the end back where it was detected (${formatTime(track.detectedEnd, rate, false)})`}>↺</span>{/if}<span class="pill-time">{formatLength(track.end - track.start, rate)}</span><Icon name="play" size={10} /></button><button class="ok" class:on={track.startChecked && track.endChecked} class:half={!!track.startChecked !== !!track.endChecked} onclick={(event) => { event.stopPropagation(); confirmTrack(track) }} title={track.startChecked && track.endChecked ? 'Start and end confirmed. Click to undo (Y)' : 'Happy with this start and end? Click to confirm both (Y)'} aria-label="Confirm this track's start and end">✓</button>
        </span>
        <button class="art-cell no-hold" onclick={(event) => { event.stopPropagation(); tagging = selectedIds.includes(track.id) ? selectedIds.slice() : [track.id] }} title="Edit tags and picture">{#if track.artPath && thumbs[track.artPath]}<img src={thumbs[track.artPath]} alt="" />{:else}<span>♪</span>{/if}</button>
        <input class="no-hold" value={track.artist} placeholder="Artist" onchange={(event) => editField(track, 'artist', (event.target as HTMLInputElement).value.trim())} onclick={(event) => event.stopPropagation()} />
        <input class="no-hold" value={track.album} placeholder="Album" onchange={(event) => editField(track, 'album', (event.target as HTMLInputElement).value.trim())} onclick={(event) => event.stopPropagation()} />
        <input class="no-hold" value={track.year ?? ''} placeholder="Year" onchange={(event) => editField(track, 'year', (event.target as HTMLInputElement).value.trim())} onclick={(event) => event.stopPropagation()} />
        <input class="no-hold track-number" value={track.trackNumber || (group ? group.ids.indexOf(track.id) + 1 : '')} title="Track number: its place in the album unless you type another" onchange={(event) => setTrackNumber(track, (event.target as HTMLInputElement).value)} onclick={(event) => event.stopPropagation()} />
        <input class="no-hold" value={track.title} placeholder="Title" onchange={(event) => editField(track, 'title', (event.target as HTMLInputElement).value.trim())} onclick={(event) => event.stopPropagation()} />
        <input class="no-hold" value={track.genre ?? ''} placeholder="Genre" onchange={(event) => editField(track, 'genre', (event.target as HTMLInputElement).value.trim())} onclick={(event) => event.stopPropagation()} />
        <input class="no-hold" value={track.composer ?? ''} placeholder="Composer" onchange={(event) => editField(track, 'composer', (event.target as HTMLInputElement).value.trim())} onclick={(event) => event.stopPropagation()} />
        <span class="source-cell" title={cutoff ? `Spectrum stops at ${(cutoff / 1000).toFixed(1)} kHz: ${describeCutoff(cutoff)}` : 'Too little sound to tell'}>{shortCutoff(cutoff)}</span>
        <span class="fades" title={track.fadeInMs || track.fadeOutMs ? `Fade in ${track.fadeInMs} ms, fade out ${track.fadeOutMs} ms` : 'No fades'}><span class="fade-mark">{track.fadeInMs ? '◢' : ''}{track.fadeOutMs ? '◣' : ''}</span></span>
        <span class="status">{#if track.note}<span class="note-mark" title={`Noted while recording: ${track.note}`}>⚠</span>{/if}{#if track.verifyError}<button class="failed no-hold" title={`${track.verifyError}. Export it again.`} onclick={(event) => { event.stopPropagation(); exportTracks([track.id], true) }}>Failed check</button>{:else if track.skip}<button class="skipped no-hold" title={`${track.skipReason}. Click to use this take after all.`} onclick={(event) => { event.stopPropagation(); setSkip(track, false) }}>Skipped</button>{:else if track.skipReason}<button class="damaged no-hold" title={`${track.skipReason}. Click to leave it out of the export.`} onclick={(event) => { event.stopPropagation(); setSkip(track, true) }}>Damaged?</button>{:else if track.exported}<button class="exported no-hold" title={`${track.exportedPath}${track.verified ? ' · read back and checked' : ''}`} onclick={(event) => { event.stopPropagation(); sendToShell({ type: 'openFolder', path: track.exportedPath }) }}>Exported</button>{/if}</span>
      </div>
      {#if lifted && group.ids.includes(lifted) && group.ids[group.ids.length - 1] === track.id}<div class="split-zone" data-drop="split" class:over={dropOn === 'split'}>Drop here to give it an album of its own</div>{/if}
      {/if}
    {:else}
      <div class="none quiet">No tracks found. Place the playhead and press N to add one, or use Redetect.</div>
    {/each}
  </div>
  {/if}
  {#if step === 'cuts'}<Hint id="edge-colours" text="Amber start or end: the app found no clear gap there, so the cut is a best guess and worth a listen. Blue: it fell in a gap. Green: you've confirmed it." />{/if}
  {#if step === 'cuts'}<Hint id="track-select" text="Tick tracks (or the box at the top for all) to tag, export, fade, merge or remove several at once; Ctrl/Shift+click and right-click work too. The small buttons on each row play that track's start or end." />{/if}
</div>
{/if}

{#if tagging}
  {@const taggingKeys = tagging}
  <TagEditor {sessionId} tracks={tracks.filter(track => taggingKeys.includes(track.id))} {thumbs} onapply={(fields, single) => { applyTags(taggingKeys, fields, single); tagging = null }} onlookup={(fields) => openLookup(taggingKeys, fields)} oncancel={() => tagging = null} />
{/if}
{#if lookingUp}
  {@const lookupKeys = lookingUp.keys}
  <LookupDialog {sessionId} trackCount={lookupKeys.length} artist={lookingUp.artist} title={lookingUp.title} album={lookingUp.album} onapply={(match, take, artPath, artThumb) => applyMatch(lookupKeys, match, take, artPath, artThumb)} oncancel={() => lookingUp = null} />
{/if}
{#if historyOpen}
  <Dialog wide title="History" text="Every state this track list has been in, newest first. Restoring is itself a change, so it can be undone." confirmLabel="Close" onconfirm={() => historyOpen = false} oncancel={() => historyOpen = false}>
    <div class="history">
      {#each snapshots as snapshot (snapshot.index)}<div class="snapshot"><span class="when">{new Date(snapshot.at).toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit', second: '2-digit' })}</span><span class="label">{snapshot.label}</span><span class="quiet">{snapshot.tracks} track{snapshot.tracks === 1 ? '' : 's'}</span><button class="ghost" onclick={() => restoreSnapshot(snapshot.index, snapshot.label)}>Restore</button></div>{:else}<p class="quiet">Nothing yet.</p>{/each}
    </div>
  </Dialog>
{/if}
{#if joining}
  {@const join = joining}
  {@const mover = tracks.find(track => track.id === join.trackId)}
  <Dialog title={join.mergeIds ? 'Merge these albums?' : 'Move this track into that album?'} wide text={`${join.mergeIds ? `${join.mergeIds.length} track${join.mergeIds.length === 1 ? '' : 's'} (${albumName({ artist: mover?.albumArtist || mover?.artist || '', album: mover?.album ?? '', year: mover?.year })})` : `“${mover?.title || 'This track'}” (${albumName({ artist: mover?.albumArtist || mover?.artist || '', album: mover?.album ?? '', year: mover?.year })})`} join ${albumName(join.target)}. Which album name, album artist, year and genre should they share? Each track keeps its own artist.`}
    confirmLabel="Use the other album's" altLabel={join.mergeIds ? 'Use these tracks\u2019' : 'Use this track\u2019s'} onconfirm={() => joinAlbum('theirs')} onalt={() => joinAlbum('mine')} oncancel={() => joining = null} />
{/if}
{#if splitting2}
  {@const split = splitting2}
  <Dialog title="An album of its own" text="This track becomes its own album. What's the album called?" confirmLabel="Make it its own album" onconfirm={splitOut} oncancel={() => splitting2 = null}>
    <label>Album<input value={split.name} oninput={(event) => { if (splitting2) splitting2.name = (event.target as HTMLInputElement).value }} /></label>
  </Dialog>
{/if}
{#if reverting}
  {@const job = reverting}
  {@const preview = tracks.filter(track => job.keys.includes(track.id)).slice(0, 4).map(track => { const captured = capturedFor(track); return captured ? `${captured.artist || '?'} - ${captured.album || 'no album'} - ${captured.title || '?'}` : `${track.title || 'Track'}: nothing captured` })}
  <Dialog title="Revert to captured tags" wide text={`Put ${job.keys.length === 1 ? 'this track' : `these ${job.keys.length} tracks`} back to what the player said while recording. Choose which tags:`} confirmLabel="Revert" onconfirm={revertCaptured} oncancel={() => reverting = null}>
    <label class="check"><input type="checkbox" checked={job.artist} onchange={(event) => { if (reverting) reverting.artist = (event.target as HTMLInputElement).checked }} /> Artist</label>
    <label class="check"><input type="checkbox" checked={job.album} onchange={(event) => { if (reverting) reverting.album = (event.target as HTMLInputElement).checked }} /> Album</label>
    <label class="check"><input type="checkbox" checked={job.title} onchange={(event) => { if (reverting) reverting.title = (event.target as HTMLInputElement).checked }} /> Title</label>
    <p class="quiet captured-preview">{preview.join(' · ')}{job.keys.length > 4 ? ' …' : ''}</p>
  </Dialog>
{/if}
{#if existing}
  {@const job = existing}
  <Dialog title="Some files are already there" wide text={`${job.files.length} of these track${job.files.length === 1 ? '' : 's'} already have a file in the output folder, probably from an earlier export that was interrupted. Keep them (each is checked: it must play, be the right length and carry the right title), or write them all again?`}
    confirmLabel="Keep them, export the rest" altLabel="Write them again" onconfirm={useExisting} onalt={() => { const keys = job.keys; existing = null; exportTracks(keys, false, true) }} oncancel={() => existing = null} />
{/if}
{#if exportCheck}
  {@const check = exportCheck}
  <Dialog title={check.missing ? 'Some tags are empty' : 'Some cuts aren’t confirmed'} text={[check.missing ? `${check.keys.length === 1 ? 'This track has' : 'Some of these tracks have'} no ${check.missing}. Players and libraries sort by these, so it's worth filling them in first.` : '', check.unconfirmed ? `${check.unconfirmed} start${check.unconfirmed === 1 ? '' : 's'} or end${check.unconfirmed === 1 ? '' : 's'} haven't been confirmed with ✓ yet. That may be fine.` : ''].filter(Boolean).join(' ')}
    confirmLabel={check.missing ? 'Fill in tags…' : 'Check the cuts'} altLabel="Export anyway" onalt={() => { const keys = check.keys; exportCheck = null; exportTracks(keys, true) }} onconfirm={() => { const keys = check.keys; const missing = check.missing; exportCheck = null; if (missing) tagging = keys; else step = 'cuts' }} oncancel={() => exportCheck = null} />
{/if}
{#if dialog?.kind === 'fades'}
  <Dialog title="Fades for {dialog.keys.length} track{dialog.keys.length === 1 ? '' : 's'}" text="Lengths in milliseconds; 0 is no fade. You hear them when auditioning, and they're in the exported files." confirmLabel="Set" onconfirm={applyDialog} oncancel={() => dialog = null}>
    <label>Fade in <input bind:value={dialog.fadeIn} inputmode="numeric" /></label>
    <label>Fade out <input bind:value={dialog.fadeOut} inputmode="numeric" /></label>
  </Dialog>
{:else if dialog?.kind === 'redetect'}
  <Dialog title="Find the tracks again?" text="Builds the track list again from the names noted while recording and the recording profile's gap settings. Your edits to this list are replaced (Undo brings them back)." confirmLabel="Redetect" onconfirm={applyDialog} oncancel={() => dialog = null} />
{:else if dialog?.kind === 'close'}
  <Dialog title="Close this session?" text={`This deletes the session's recording (the WAV) for good.${tracks.length - exportedCount > 0 ? ` ${tracks.length - exportedCount} of ${tracks.length} tracks haven't been exported yet.` : ' Every track has been exported.'} Exported files are kept.`} confirmLabel="Delete recording" danger onconfirm={applyDialog} oncancel={() => dialog = null} />
{/if}

{#if gapFor}
  {@const at = tracks.findIndex(track => track.id === gapFor?.id)}
  <div class="gap-backdrop" role="presentation" onpointerdown={(event) => { if (event.target === event.currentTarget) gapFor = null }}>
    <div class="gap-box" role="dialog" aria-label="Remove this track">
      <h3>Remove track {at + 1}: how should its neighbours meet?</h3>
      <button class="ghost gap-choice" onclick={() => closeGap('previous')}>Track {at}'s end moves to where track {at + 2} starts</button>
      <button class="ghost gap-choice" onclick={() => closeGap('next')}>Track {at + 2}'s start moves to where track {at} ends</button>
      <button class="ghost gap-choice" onclick={() => closeGap('middle')}>They meet in the middle</button>
      <button class="ghost gap-choice" onclick={() => closeGap('gap')}>Leave the gap (its audio is skipped on export)</button>
      <div class="gap-foot"><button class="ghost" onclick={() => gapFor = null}>Cancel</button></div>
    </div>
  </div>
{/if}

{#if comparing}
  {@const job = comparing}
  <CoverCompare sessionId={sessionId} title={`${job.group.album || 'Cover'}: compare covers`} items={job.items} onuse={(item) => { comparing = null; useChoice(job.group, item) }} onclose={() => comparing = null} />
{/if}

<style>
  .gap-backdrop { position: fixed; inset: 0; z-index: 120; display: grid; place-items: center; background: rgba(0, 0, 0, 0.25); }
  .gap-box { width: min(460px, calc(100vw - 40px)); display: flex; flex-direction: column; gap: 6px; padding: 16px 18px 14px; border-radius: 14px; background: var(--panel-solid); border: 1px solid var(--line); box-shadow: 0 24px 60px var(--shadow); }
  .gap-box h3 { margin: 0 0 6px; font-size: 15px; text-transform: none; letter-spacing: 0; color: var(--fg); }
  .gap-choice { text-align: left; padding: 7px 12px; }
  .gap-foot { display: flex; justify-content: flex-end; margin-top: 4px; }

  .session { display: flex; flex-direction: column; height: 100%; min-height: 0; gap: 8px; }
  .empty { padding: 40px; }
  :global(.session-title) { display: flex; align-items: baseline; gap: 10px; flex-wrap: nowrap; overflow: hidden; min-width: 0; }
  :global(.session-title) .name { font-size: 15px; font-weight: 600; background: none; border: 1px solid transparent; padding: 1px 6px; margin-left: -7px; min-width: 100px; width: 20ch; flex: none; }
  :global(.session-title) > span { white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
  .session-foot { display: flex; gap: 8px; align-items: center; font-size: 12px; padding: 0 2px; flex: none; }
  .link-quiet { background: none; border: 0; padding: 0; color: var(--fg-quiet); font: inherit; cursor: pointer; text-decoration: underline; text-decoration-color: color-mix(in srgb, currentColor 40%, transparent); }
  .link-quiet:hover { color: var(--fg); } .danger-text:hover { color: var(--danger); }
  .name:hover { border-color: var(--line); }
  .facts { color: var(--fg-quiet); font-size: 12.5px; }
  .clip-warning { color: var(--danger); font-size: 12px; font-weight: 600; }
  .source-chip { font-size: 12px; color: var(--fg-quiet); }
  .source-cell { font-family: var(--font-display); font-size: 12px; color: var(--fg-quiet); }
  .toolbar { display: flex; align-items: center; gap: 10px; flex-wrap: wrap; }
  .group { display: flex; align-items: center; gap: 4px; }
  .ghost.icon { padding: 5px 7px; }
  .spacer { flex: 1; }
  .export { display: inline-flex; align-items: center; gap: 6px; }
  .export-status { flex: 0 1 380px; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; font-size: 12px; color: var(--fg-quiet); text-align: right; } /* fixed place in the toolbar: a changing file name never moves anything */
  .wave-host { flex: none; min-height: 200px; position: relative; overflow: hidden; }
  .splitter { flex: none; height: 9px; margin: -4px 0; cursor: row-resize; display: grid; place-items: center; touch-action: none; }
  .splitter span { width: 48px; height: 4px; border-radius: 2px; background: var(--line); }
  .splitter:hover span { background: var(--accent); }
  .wave-mode { display: flex; align-items: center; gap: 0; }
  .wave-mode button { height: 22px; padding: 0 9px; font: inherit; font-size: 11.5px; font-weight: 600; cursor: pointer; background: var(--panel-solid); color: var(--fg-quiet); border: 1px solid var(--line); }
  .wave-mode button:first-child { border-radius: 6px 0 0 6px; } .wave-mode button:nth-child(2) { border-radius: 0 6px 6px 0; border-left: 0; }
  .wave-mode button.on { background: var(--accent-soft); color: var(--accent); }
  .wave-mode kbd { margin-left: 5px; }
  .readout { flex: none; white-space: nowrap; overflow: hidden; align-items: center; display: flex; gap: 18px; font-family: var(--font-display); font-size: 12px; color: var(--fg-quiet); }
  .grabbed { color: var(--accent); font-family: var(--font-ui); }
  .key { margin-left: auto; display: flex; align-items: center; gap: 6px; font-family: var(--font-ui); font-size: 11.5px; white-space: nowrap; }
  .swatch { display: inline-block; width: 3px; height: 12px; margin-left: 8px; border-radius: 1px; }
  .swatch.start { background: var(--start); } .swatch.end { background: var(--end); }
  .swatch.change { width: 0; border-left: 2px dashed var(--warn); border-radius: 0; }
  .group-label { font-size: 10.5px; font-weight: 600; letter-spacing: 0.05em; text-transform: uppercase; color: var(--fg-quiet); margin-right: 2px; }
  .list { flex: 1 1 auto; min-height: 120px; overflow-y: auto; border-top: 1px solid var(--line); }
  .row { display: grid; grid-template-columns: var(--columns); gap: 8px; align-items: center; padding: 0 8px; border-radius: 4px; cursor: default; font-size: 12.5px; min-height: 26px; }
  .row:not(.head):hover { background: var(--hover); }
  .row.current { box-shadow: inset 3px 0 0 var(--accent); }
  .row.playing-now { background: var(--accent-soft); }
  .head { position: sticky; top: 0; z-index: 1; padding: 6px 8px; font-size: 11px; color: var(--fg-quiet); text-transform: uppercase; letter-spacing: 0.05em; background: var(--panel-solid); border-radius: 0; }
  .number, .time, .fades { font-family: var(--font-display); font-size: 12px; color: var(--fg-quiet); }
  .edge { background: none; border: 1px solid transparent; border-radius: 5px; padding: 2px 4px; text-align: left; cursor: pointer; }
  .edge:hover { border-color: var(--line); background: var(--panel); }
  .edge.doubt { color: var(--warn); font-weight: 600; }
  .edge.checked { color: #3ecf6e; }
  .steps { display: flex; align-items: stretch; gap: 8px; padding: 8px; border-radius: 12px; background: var(--panel); border: 1px solid var(--line); }
  .step { display: flex; align-items: center; gap: 10px; flex: 1; min-width: 0; background: none; border: 1px solid transparent; border-radius: 9px; padding: 6px 10px; color: var(--fg); font: inherit; text-align: left; cursor: pointer; }
  .step:hover { background: var(--hover); }
  .step.active { background: var(--accent-soft); border-color: color-mix(in srgb, var(--accent) 40%, transparent); }
  .step-number { flex: none; width: 24px; height: 24px; border-radius: 50%; display: grid; place-items: center; background: var(--panel-strong); font-size: 12px; font-weight: 700; }
  .step.done .step-number { background: #3ecf6e; color: #06200e; }
  .step.done .step-number::before { content: "✓"; } .step.done .step-number { font-size: 0; } .step.done .step-number::before { font-size: 13px; }
  .step-text { display: flex; flex-direction: column; line-height: 1.2; min-width: 0; }
  .step-text span { font-size: 11.5px; color: var(--fg-quiet); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
  .step-actions { display: flex; align-items: center; gap: 6px; padding-left: 8px; border-left: 1px solid var(--line); }
  .step-actions .ghost kbd { margin-left: 4px; }
  .row input { background: transparent; border-color: transparent; padding: 1px 6px; min-width: 0; font-size: 12.5px; }
  .row input:hover { border-color: var(--line); }
  .row input:focus { background: var(--panel); }
  .art-cell { width: 26px; height: 26px; padding: 0; border: 1px solid var(--line); border-radius: 4px; background: var(--panel); color: var(--fg-quiet); cursor: pointer; overflow: hidden; display: grid; place-items: center; font-size: 12px; }
  .art-cell img { width: 100%; height: 100%; object-fit: cover; }
  .art-cell:hover { border-color: var(--accent); }
  /* Hear start / end: one split control per row, Start | End */
  .hear { display: flex; gap: 0; white-space: nowrap; }
  .hear .hear-start, .hear .hear-end { width: 82px; padding: 0 6px; justify-content: space-between; font-family: var(--font-display); font-weight: 500; }
  .hear .hear-start.doubt, .hear .hear-end.doubt { color: var(--warn); }
  .pill-time { font-size: 12px; }
  .pill-revert { color: var(--warn); font-family: var(--font-ui); font-size: 12px; padding: 0 2px; border-radius: 3px; }
  .pill-revert:hover { background: color-mix(in srgb, var(--warn) 25%, transparent); }
  .head-cell { position: relative; display: block; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .col-resize { position: absolute; top: -4px; right: -6px; width: 9px; bottom: -4px; cursor: col-resize; z-index: 2; }
  .col-resize::after { content: ''; position: absolute; left: 4px; top: 6px; bottom: 6px; width: 1px; background: var(--line); }
  .col-resize:hover::after { background: var(--accent); width: 2px; }
  .fade-mark { color: var(--accent); font-size: 13px; letter-spacing: 1px; }
  .hear button { display: inline-flex; align-items: center; justify-content: center; gap: 4px; height: 20px; padding: 0 9px; font: inherit; font-size: 11.5px; font-weight: 600; cursor: pointer; white-space: nowrap; background: var(--accent-soft); color: var(--accent); border: 1px solid color-mix(in srgb, var(--accent) 40%, transparent); }
  .hear .hear-start { border-radius: 7px 0 0 7px; } .hear .hear-end { border-radius: 0 7px 7px 0; border-left: 0; }
  .hear button:hover { background: var(--accent); color: var(--on-accent); }
  .hear button.checked { background: rgba(62, 207, 110, 0.18); color: #2e9e57; border-color: rgba(62, 207, 110, 0.5); }
  .tags-page { flex: 1 1 auto; min-height: 0; overflow-y: auto; display: flex; flex-direction: column; gap: 12px; padding: 2px 2px 16px; }
  .tags-note, .tags-foot { margin: 0; font-size: 12.5px; }
  .tags-summary { display: flex; align-items: center; gap: 12px; font-size: 13px; padding: 2px 4px; flex: none; }
  .tags-summary strong.done { color: #3ecf6e; }
  .tags-summary .spacer, .online-head .spacer { flex: 1; }
  .only-needy { display: inline-flex; align-items: center; gap: 6px; font-size: 12.5px; color: var(--fg-quiet); cursor: pointer; }
  .tag-card { flex: none; border-radius: 12px; padding: 8px 2px 10px; display: flex; flex-direction: column; gap: 8px; font-size: 12.5px; }
  .album-state { flex: none; align-self: center; padding: 3px 10px; border-radius: 999px; font-size: 12px; font-weight: 600; max-width: 50%; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .album-state.complete { color: #3ecf6e; background: color-mix(in srgb, #3ecf6e 14%, transparent); }
  .album-state.needs { color: var(--warn); background: color-mix(in srgb, var(--warn) 14%, transparent); }
  .tag-layout { display: grid; grid-template-columns: 236px minmax(0, 1fr); gap: 10px; align-items: start; }
  .tag-sections { display: flex; flex-direction: column; gap: 10px; min-width: 0; }
  .tag-panel { padding: 10px 12px 12px; border-radius: 12px; background: var(--glass); border: 1px solid var(--line); -webkit-backdrop-filter: blur(var(--blur)); backdrop-filter: blur(var(--blur)); min-width: 0; }
  .panel-title { font-size: 11px; font-weight: 700; text-transform: uppercase; letter-spacing: 0.06em; color: var(--fg); white-space: nowrap; }
  .column-title, .source-name { font-size: 11px; font-weight: 600; text-transform: uppercase; letter-spacing: 0.05em; color: var(--fg-quiet); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
  /* One column layout for both panels, so album fields and track fields line up */
  .tag-row { display: grid; grid-template-columns: 76px minmax(0, 1fr) minmax(0, 1fr) 58px minmax(0, 1.1fr); column-gap: 8px; align-items: center; min-height: 28px; }
  .tag-row.head { min-height: 28px; margin-bottom: 4px; }
  .tag-row.head:not(.tracks-head-row) { grid-template-columns: minmax(0, calc(76px + 8px + 2 * ((100% - 76px - 58px - 32px) / 3.1) + 8px)) 58px minmax(0, 1.1fr); }
  .tag-row.left-out { opacity: 0.5; }
  .row-label { color: var(--fg-quiet); font-size: 12px; white-space: nowrap; }
  .row-label.number-cell { font-family: var(--font-display); text-align: left; padding-left: 2px; }
  .tag-row input { box-sizing: border-box; width: 100%; min-width: 0; height: 24px; padding: 0 8px; font-size: 12.5px; }
  .tag-row input.wide { grid-column: 2 / span 2; }
  .tag-row input.need { border-color: color-mix(in srgb, var(--warn) 70%, transparent); background: color-mix(in srgb, var(--warn) 9%, transparent); }
  .state { font-size: 11px; font-weight: 600; color: var(--fg-quiet); text-align: center; white-space: nowrap; }
  .state.good { color: #3ecf6e; font-size: 13px; }
  .state.need { color: var(--warn); }
  .state.differs { color: var(--accent); }
  .online-head { display: flex; align-items: center; gap: 6px; min-width: 0; }
  button.small { flex: none; height: 22px; padding: 0 9px; font-size: 11.5px; }
  .offer { display: flex; gap: 4px; min-width: 0; }
  .chip { flex: 0 1 auto; min-width: 0; height: 24px; padding: 0 8px; border-radius: 6px; border: 1px dashed color-mix(in srgb, var(--accent) 45%, transparent); background: var(--accent-soft); color: var(--fg); font: inherit; font-size: 12px; text-align: left; cursor: pointer; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .chip:hover:not(:disabled) { border-style: solid; border-color: var(--accent); }
  .chip i { font-style: normal; color: var(--fg-quiet); font-size: 11px; }
  .chip.same { background: none; border-color: transparent; color: #3ecf6e; padding-left: 0; cursor: default; opacity: 1; }
  .not-listed { font-size: 12px; }
  .cover-panel { position: sticky; top: 0; display: flex; flex-direction: column; gap: 6px; }
  .cover-main { width: 100%; aspect-ratio: 1; padding: 0; border: 0; border-radius: 8px; overflow: hidden; background: var(--hover); cursor: zoom-in; display: grid; place-items: center; color: var(--warn); font: inherit; font-weight: 600; }
  .cover-main.empty { border: 1px dashed var(--warn); cursor: pointer; }
  .cover-main img { width: 100%; height: 100%; object-fit: cover; display: block; }
  .cover-facts { text-align: center; font-size: 12px; color: var(--fg-quiet); font-family: var(--font-display); min-height: 16px; }
  .cover-facts .low, .other-facts.low { color: var(--warn); font-weight: 600; }
  .cover-others { display: flex; gap: 6px; }
  .cover-other { flex: 1 1 0; min-width: 0; display: flex; flex-direction: column; align-items: center; gap: 2px; padding: 3px 2px; border-radius: 7px; border: 2px solid transparent; background: none; color: var(--fg); font: inherit; cursor: pointer; }
  .cover-other:hover { border-color: var(--line); }
  .cover-other.chosen { border-color: var(--accent); }
  .cover-other img, .cover-other .plus { width: 100%; max-width: 58px; aspect-ratio: 1; object-fit: cover; border-radius: 4px; display: grid; place-items: center; }
  .cover-other .plus { background: var(--hover); font-size: 20px; color: var(--fg-quiet); }
  .other-label { font-size: 10.5px; color: var(--fg-quiet); max-width: 100%; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .other-facts { font-size: 10px; color: var(--fg-quiet); font-family: var(--font-display); min-height: 13px; white-space: nowrap; }
  .compare-button { width: 100%; height: 26px; padding: 0; font-size: 12px; justify-content: center; text-align: center; display: inline-flex; align-items: center; }
  .tag-card.dropping { border-color: var(--accent); background: var(--accent-soft); }
  .tag-head { display: flex; align-items: center; gap: 12px; padding: 0 4px; }
  .tag-title { flex: 1; min-width: 0; }
  .tag-title h4 { margin: 0; font-size: 13.5px; font-weight: 600; text-transform: none; letter-spacing: 0; color: var(--fg); }
  .tag-title p { margin: 2px 0 0; font-size: 12px; }
  .tag-table { width: 100%; border-collapse: collapse; font-size: 12px; table-layout: fixed; }
  .tag-table input:not([type=checkbox]), .tag-table td.theirs button { box-sizing: border-box; max-width: 100%; }
  .tag-table.tracks td.mine input { width: 100%; padding: 0 6px; height: 20px; font-size: 12px; }
  .tag-card .tag-table td { padding: 1px 6px 1px 0; }
  .tag-card .tag-table td.mine .with-same > input { padding: 0 6px; height: 22px; font-size: 12px; }
  .tag-card .tag-table td.theirs button { padding: 1px 6px; font-size: 12px; }
  .tag-card .tag-table.tracks td.theirs button { padding: 0 6px; }
  .tag-table.tracks td { padding-right: 8px; }
  .tag-table:not(.tracks) th:first-child { width: 100px; }
  .tag-table.tracks th:first-child { width: 44px; }
  .tag-table th { text-align: left; font-weight: 500; color: var(--fg-quiet); padding: 3px 8px 3px 0; white-space: nowrap; font-size: 12px; }
  .tag-table thead th { font-size: 11px; text-transform: uppercase; letter-spacing: 0.06em; font-weight: 600; }
  .tag-table td { padding: 2px 6px 2px 0; vertical-align: middle; }
  .tag-table td.mine { width: 48%; }
  .tag-table td.mine .with-same { display: flex; align-items: center; gap: 10px; }
  .tag-table td.mine .with-same > input { flex: 1; min-width: 0; padding: 3px 8px; }
  .tag-table td.mine input[type=text], .tag-table td.mine > input:not([type]) { width: 100%; padding: 3px 8px; }
  .tag-table td.mine .same { display: inline-flex; flex-direction: row; gap: 4px; align-items: center; font-size: 11.5px; color: var(--fg-quiet); white-space: nowrap; flex: none; }
  .tag-table td.theirs button { width: 100%; text-align: left; padding: 3px 8px; border-radius: 6px; border: 1px dashed color-mix(in srgb, var(--accent) 45%, transparent); background: var(--accent-soft); color: var(--fg); font: inherit; cursor: pointer; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .tag-table td.theirs button:hover { border-style: solid; border-color: var(--accent); }
  .tag-table td.theirs button.same { background: none; border-color: transparent; color: #2e9e57; }
  .tag-tracks summary { cursor: pointer; font-size: 13px; font-weight: 600; padding: 2px 0; }
  .tag-table.tracks td.mine { width: auto; }
  .number-cell { font-family: var(--font-display); font-size: 12px; color: var(--fg-quiet); width: 34px; text-align: center; }
  .missing { margin: 6px 0 0; font-size: 12px; }
  .small-button { padding: 1px 8px; font-size: 11.5px; min-height: 0; height: 22px; margin-left: 8px; }
  .album-tail { display: flex; align-items: center; gap: 4px; justify-content: flex-end; }
  .drag { cursor: grab; color: var(--fg-quiet); font-size: 11px; padding: 0 3px; user-select: none; }
  .album-tile { display: flex; align-items: center; gap: 14px; width: 100%; margin-top: 6px; padding: 6px 12px; border-radius: 8px; border: 1px solid var(--line); background: var(--panel); color: var(--fg); font: inherit; font-size: 13px; text-align: left; cursor: pointer; transition: transform 120ms, border-color 120ms, background 120ms; }
  .album-tile:hover { background: var(--hover); }
  .album-tile.target { border-style: dashed; border-color: var(--accent); }
  .album-tile.over, .album-row.over { background: var(--accent-soft); border-color: var(--accent); border-style: solid; transform: scale(1.01); }
  .tile-name { flex: 1; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .tile-facts { color: var(--fg-quiet); font-size: 12px; white-space: nowrap; }
  .tile-facts .all { color: #2e9e57; }
  .tile-drop { color: var(--accent); font-weight: 600; font-size: 12px; }
  .chevron { color: var(--fg-quiet); font-size: 16px; }
  .album-row.open { border-color: color-mix(in srgb, var(--accent) 50%, var(--line)); cursor: grab; }
  /* The open album's header uses the track rows' columns, so its fields sit right above the tracks' own */
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
  .split-zone { margin: 6px 0; padding: 10px; border: 2px dashed var(--line); border-radius: 8px; text-align: center; font-size: 12.5px; color: var(--fg-quiet); }
  .split-zone.over { border-color: var(--accent); color: var(--accent); background: var(--accent-soft); }
  .row.lifting { animation: wiggle 350ms ease-in-out; }
  @keyframes wiggle { 0%, 100% { transform: none; } 25% { transform: rotate(-0.4deg); } 75% { transform: rotate(0.4deg); } }
  .row.lifted :global(*) { user-select: none; }
  .row.lifted { opacity: 0.85; box-shadow: 0 8px 22px var(--shadow); transform: scale(1.01); background: var(--panel-solid); cursor: grabbing; position: relative; z-index: 2; }
  .export-page { gap: 10px; }
  .export-total { margin: 0; font-size: 13px; color: var(--fg-quiet); }
  .export-total strong { color: var(--fg); }
  .report-card { border: 1px solid var(--line); border-radius: 8px; background: var(--panel); padding: 4px 8px 6px; }
  .report-head { display: flex; align-items: center; gap: 10px; padding: 0 0 2px; font-size: 12.5px; }
  .report-title { flex: 0 1 auto; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; font-size: 13.5px; }
  .report-profile { width: auto; max-width: 200px; padding: 2px 6px; font-size: 12.5px; }
  .tag-table.report { font-size: 11.5px; }
  .tag-table.report th { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; font-size: 9.5px; text-transform: uppercase; letter-spacing: 0.05em; font-weight: 600; padding: 1px 4px 1px 0; }
  .tag-table.report td { padding: 0 4px 0 0; line-height: 1.2; }
  .tag-table.report td input:not(.tick) { width: 100%; box-sizing: border-box; padding: 0 4px; height: 19px; background: transparent; border-color: transparent; font-size: 11.5px; }
  .tag-table.report tr.exporting-now td { background: var(--accent-soft); }
  .report .c-disc { width: 40px; }
  .tag-table.report td input:not(.tick):hover { border-color: var(--line); } .tag-table.report td input:not(.tick):focus { background: var(--panel-solid); }
  .report-folder { margin: -4px 0 4px 30px; font-size: 12px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .no-pair { display: flex; align-items: baseline; gap: 1px; } .no-pair input { width: 30px !important; text-align: right; }
  .report .c-file { width: 17%; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; font-size: 11.5px; }
  .report .c-tick { width: 24px; } .report .c-no { width: 58px; } .report .c-year { width: 54px; } .report .c-art { width: 44px; } .report .c-status { width: 92px; }
  .report .c-art img { width: 18px; height: 18px; object-fit: cover; border-radius: 3px; display: block; }
  .skipped-row { opacity: 0.5; }
  .export-plan { flex: none; max-height: 36%; overflow-y: auto; border: 1px solid var(--line); border-radius: 10px; background: var(--panel); padding: 6px 12px; }
  .export-table th:first-child { width: auto; }
  .export-table th:nth-child(2) { width: 60px; } .export-table th:nth-child(3) { width: 230px; } .export-table th:nth-child(4) { width: 90px; }
  .export-table select { width: 100%; }
  .plan-album { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .plan-size { font-family: var(--font-display); font-size: 12.5px; text-align: right; }
  .plan-total td { border-top: 1px solid var(--line); padding-top: 4px; }
  .note-mark { color: var(--warn); margin-right: 4px; cursor: help; }
  .danger-button { background: var(--danger) !important; border-color: transparent !important; }
  .head-spacer { flex: 1; }
  .export-size { font-family: var(--font-display); font-size: 13px; color: var(--fg-quiet); white-space: nowrap; }
  .check { flex-direction: row !important; align-items: center; gap: 8px; font-size: 13px; color: var(--fg) !important; }
  .captured-preview { font-size: 12px; margin: 0; }
  .album-row { display: grid; grid-template-columns: 70px minmax(100px, 1fr) minmax(100px, 1fr) 64px minmax(80px, 0.7fr) minmax(220px, auto); gap: 8px; align-items: center; margin-top: 6px; padding: 2px 8px; border-radius: 8px; background: var(--panel); border: 1px solid var(--line); }
  .album-row input { font-weight: 600; padding: 1px 6px; font-size: 12.5px; }
  .album-year { text-align: center; }
  .album-label { font-size: 11px; text-transform: uppercase; letter-spacing: 0.06em; color: var(--fg-quiet); font-weight: 600; }
  .album-count { font-size: 12px; white-space: nowrap; }
  .join { width: 20px; height: 20px; padding: 0; margin-left: 2px; background: var(--panel); border: 1px solid var(--line); border-radius: 4px; color: var(--fg); cursor: pointer; font-size: 11px; vertical-align: middle; }
  .join:hover { background: var(--hover); }
  .edge-cell { display: flex; align-items: center; gap: 2px; min-width: 0; white-space: nowrap; } /* never wraps: rows keep their shape whatever their state */
  .revert { width: 18px; height: 20px; padding: 0; flex: none; background: none; border: 0; border-radius: 4px; color: var(--warn); cursor: pointer; font-size: 13px; }
  .revert:hover { background: var(--hover); }
  /* Last heard: an underline bar. Selected on the wave: a solid outline in the line's colour. Confirmed stays green */
  .hear button.heard { box-shadow: inset 0 -3px 0 0 var(--accent); }
  .hear .hear-start.grabbed { outline: 2px solid #3ecf6e; outline-offset: 1px; z-index: 1; }
  .hear .hear-end.grabbed { outline: 2px solid #e5484d; outline-offset: 1px; z-index: 1; }
  .hear button.holding { animation: hold-fill 550ms linear forwards; }
  @keyframes hold-fill { from { box-shadow: inset 0 0 0 0 rgba(62, 207, 110, 0.85); } to { box-shadow: inset 90px 0 0 0 rgba(62, 207, 110, 0.85); } }
  .hear .hear-end.holding { animation-name: hold-fill-end; }
  @keyframes hold-fill-end { from { box-shadow: inset 0 0 0 0 rgba(62, 207, 110, 0.85); } to { box-shadow: inset -90px 0 0 0 rgba(62, 207, 110, 0.85); } }
  .hear .ok { width: 24px; padding: 0; margin: 0 4px; border-radius: 7px; background: var(--panel); color: var(--fg-quiet); border: 1px solid var(--line); font-weight: 700; }
  .hear .ok.on { background: #3ecf6e; color: #fff; border-color: transparent; }
  .hear .ok.half { background: color-mix(in srgb, #3ecf6e 35%, var(--panel)); color: var(--fg); }
  .hear .ok:hover { background: color-mix(in srgb, #3ecf6e 40%, var(--panel)); color: var(--fg); }
  .exported { background: none; border: 0; color: #3ecf6e; font: inherit; font-size: 12px; cursor: pointer; padding: 0; }
  .failed { background: none; border: 1px solid color-mix(in srgb, var(--danger) 50%, transparent); border-radius: 999px; padding: 1px 8px; font: inherit; font-size: 11px; cursor: pointer; color: var(--danger); }
  .skipped, .damaged { background: none; border: 1px solid var(--line); border-radius: 999px; padding: 1px 8px; font: inherit; font-size: 11px; cursor: pointer; color: var(--fg-quiet); }
  .damaged { color: var(--warn); border-color: color-mix(in srgb, var(--warn) 50%, transparent); }
  .row:has(.skipped) { opacity: 0.55; }
  .exported:hover { text-decoration: underline; }
  .none { padding: 16px 8px; }
  .header-actions { margin-left: auto; display: flex; gap: 6px; }
  .tick { width: 15px; height: 15px; margin: 0; accent-color: var(--accent); cursor: pointer; }
  label { display: flex; flex-direction: column; gap: 4px; font-size: 12px; color: var(--fg-quiet); }
  .tags-step { display: flex; flex-direction: column; gap: 4px; padding: 6px 0; border-top: 1px solid var(--line); }
  .album-card { display: flex; gap: 14px; padding: 10px 12px; border-radius: 10px; background: var(--panel); }
  .cover-col { display: flex; flex-direction: column; gap: 6px; flex: none; }
  .covers { display: flex; gap: 8px; }
  .covers figure { margin: 0; display: flex; flex-direction: column; gap: 3px; align-items: center; }
  .covers img, .no-cover { width: 96px; height: 96px; object-fit: cover; border-radius: 6px; background: var(--hover); }
  .no-cover { display: grid; place-items: center; color: var(--fg-quiet); font-size: 12px; }
  .cover-label { font-size: 10px; text-transform: uppercase; letter-spacing: 0.05em; color: var(--fg-quiet); font-weight: 600; }
  .cover-buttons { display: flex; gap: 4px; flex-wrap: wrap; max-width: 210px; }
  .album-body { flex: 1; min-width: 0; display: flex; flex-direction: column; gap: 8px; }
  .album-head { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; font-size: 13.5px; }
  .album-actions { margin-left: auto; display: flex; gap: 4px; }
  .album-fields { display: grid; grid-template-columns: max-content minmax(0, max-content) 1fr auto; gap: 4px 12px; align-items: center; font-size: 13px; }
  .field-label { color: var(--fg-quiet); font-size: 12px; }
  .field-theirs { font-weight: 600; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .field-state { color: var(--fg-quiet); font-size: 12px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .field-state.ok { color: #3ecf6e; }
  .field-buttons { display: flex; gap: 4px; }
  .album-complete { font-size: 12.5px; padding: 4px 8px; border-radius: 6px; background: color-mix(in srgb, var(--warn) 14%, transparent); }
  .album-complete.complete { background: rgba(62, 207, 110, 0.12); }
  .track-offers { display: flex; flex-direction: column; gap: 3px; }
  .track-offers-head { display: flex; align-items: center; justify-content: space-between; padding: 2px 8px; font-size: 13px; }
  .arrow { color: var(--fg-quiet); }
  .chip-text { display: inline-flex; align-items: center; gap: 5px; background: var(--accent-soft); border-radius: 999px; padding: 2px 10px 2px 6px; font-size: 12px; }
  .off-album { background: color-mix(in srgb, var(--warn) 12%, transparent); }
  .match-again { display: flex; justify-content: flex-end; font-size: 12px; padding: 0 8px; }
  .link { background: none; border: none; padding: 0; color: var(--accent); font: inherit; cursor: pointer; text-decoration: underline; }
  .album-check { padding: 6px 10px; border-radius: 8px; background: color-mix(in srgb, var(--warn) 14%, transparent); border-left: 3px solid var(--warn); font-size: 13px; }
  .album-check.complete { background: rgba(62, 207, 110, 0.12); border-left-color: #3ecf6e; }
  .suggest-note { margin: 2px 8px; font-size: 12.5px; }
  .suggest { display: flex; align-items: center; gap: 6px; flex-wrap: wrap; padding: 5px 8px; border-radius: 8px; background: var(--panel); font-size: 12.5px; }
  .suggest-track { font-weight: 600; min-width: 0; max-width: 240px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .suggest-source { color: var(--accent); font-size: 11px; text-transform: uppercase; letter-spacing: 0.05em; font-weight: 600; }
  .chip { display: inline-flex; align-items: center; gap: 5px; background: var(--accent-soft); color: var(--fg); border: 1px solid transparent; border-radius: 999px; padding: 2px 10px 2px 6px; font: inherit; font-size: 12px; cursor: pointer; max-width: 320px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .chip:hover { border-color: var(--accent); }
  .chip-label { font-size: 10px; text-transform: uppercase; letter-spacing: 0.05em; color: var(--accent); font-weight: 700; }
  .suggest-actions { margin-left: auto; display: flex; gap: 4px; }
  .history { max-height: 50vh; overflow-y: auto; display: flex; flex-direction: column; gap: 2px; }
  .snapshot { display: grid; grid-template-columns: 70px 1fr auto auto; gap: 10px; align-items: center; padding: 3px 6px; border-radius: 6px; font-size: 13px; }
  .snapshot:hover { background: var(--hover); }
  .when { font-family: var(--font-display); font-size: 12px; color: var(--fg-quiet); }
  .label { color: var(--fg); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
</style>
