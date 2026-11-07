<script lang="ts">
  import { untrack } from 'svelte'
  import { request, sendToShell, onShellMessage } from '../bridge'
  import { app, nav, toast, formatTime, formatDuration, writeSetting, type SessionDetail, type Track } from '../state.svelte'
  import { selection, selectKeys, clearSelection, type SelectionAction } from '../lib/selection.svelte'
  import { selectable } from '../lib/selectable.svelte'
  import Waveform, { type WaveView, type Grab } from '../parts/Waveform.svelte'
  import Icon from '../parts/Icon.svelte'
  import Hint from '../parts/Hint.svelte'
  import Dropdown from '../parts/Dropdown.svelte'
  import Dialog from '../parts/Dialog.svelte'
  // One session (SPEC.md › Editing, Auditioning, Export): waveform, track list, auditions and export
  let { sessionId }: { sessionId: string } = $props()

  let session = $state<SessionDetail | null>(null)
  let loadError = $state(''), loadProgress = $state(0)
  let tracks = $state<Track[]>([])
  let view = $state<WaveView>({ start: 0, framesPerPixel: 1000 })
  let cursor = $state(0), currentId = $state(''), grab = $state<Grab>(null)
  let snapToZero = $state(readFlag('snapZero'))
  let exportStatus = $state('')
  let waveform = $state<ReturnType<typeof Waveform>>()
  const scope = $derived('tracks:' + sessionId)
  const rate = $derived(session?.sampleRate ?? 48000)
  const selectedIds = $derived(selection.scope === scope ? selection.keys : [])
  const playing = $derived(!!app.status?.playing && app.status.playingSession === sessionId)
  function readFlag(name: string) { try { return localStorage.getItem('wtr.' + name) === '1' } catch { return false } }

  onShellMessage('openProgress', message => { if (message.id === sessionId) loadProgress = message.fraction })
  onShellMessage('exportProgress', message => exportStatus = message.total ? `Exporting ${Math.min(message.done + 1, message.total)} of ${message.total}${message.current ? ': ' + message.current : ''}` : '')

  async function load() {
    session = null; loadError = ''; loadProgress = 0; past = []; future = []; clearSelection()
    try {
      const detail = await request<SessionDetail>({ type: 'openSession', id: sessionId })
      session = detail; tracks = detail.tracks; currentId = detail.tracks[0]?.id ?? ''; cursor = detail.tracks[0]?.start ?? 0
      view = { start: 0, framesPerPixel: Math.max(1, detail.totalFrames / 1000) } // the waveform fits it once it knows its width
      requestAnimationFrame(() => fit())
    } catch (error) { loadError = (error as Error).message }
  }
  $effect(() => { sessionId; untrack(load) })

  // ---- Edits: every change is undoable and saved straight away ----
  let past: string[] = [], future: string[] = []
  let saveTimer = 0
  function save() { clearTimeout(saveTimer); saveTimer = window.setTimeout(() => sendToShell({ type: 'saveSession', id: sessionId, tracks: $state.snapshot(tracks) }), 250) }
  function commit(next: Track[], label: string) {
    past = past.concat([JSON.stringify($state.snapshot(tracks))]).slice(-200); future = []
    tracks = next.slice().sort((first, second) => first.start - second.start); save()
    toast(label, { undo })
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

  async function setEdgeAtCursor(edge: 'start' | 'end') {
    const track = current; if (!track) { toast('Put the playhead inside a track first'); return }
    const frame = await snapped(cursor)
    const next = copy(), edited = next.find(candidate => candidate.id === track.id)!
    if (edge === 'start' && frame >= edited.end || edge === 'end' && frame <= edited.start) { toast(edge === 'start' ? 'The start must be before the end' : 'The end must be after the start'); return }
    edited[edge] = frame; grab = { trackId: track.id, edge }
    commit(next, `Track ${tracks.indexOf(track) + 1} ${edge === 'start' ? 'starts' : 'ends'} at ${formatTime(frame, rate)}`)
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
  function deleteTracks(keys: string[]) {
    const ids = keys.length ? keys : current ? [current.id] : []
    if (!ids.length) return
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
  function editField(track: Track, field: 'artist' | 'title' | 'album', value: string) {
    if (track[field] === value) return
    const next = copy(); const edited = next.find(candidate => candidate.id === track.id)!; edited[field] = value; edited.exported = edited.exported && false
    commit(next, `Track ${tracks.indexOf(track) + 1}: ${field} changed`)
  }

  // ---- Bulk dialogs: tags and fades for the selected tracks ----
  let dialog = $state<null | { kind: 'tags'; keys: string[]; artist: string; album: string } | { kind: 'fades'; keys: string[]; fadeIn: string; fadeOut: string } | { kind: 'close' } | { kind: 'redetect' }>(null)
  function applyDialog() {
    const open = dialog; dialog = null
    if (!open) return
    if (open.kind === 'tags') { const next = copy(); for (const track of next) if (open.keys.includes(track.id)) { if (open.artist.trim()) track.artist = open.artist.trim(); if (open.album.trim()) track.album = open.album.trim() } commit(next, `Tags set on ${open.keys.length} track${open.keys.length === 1 ? '' : 's'}`) }
    else if (open.kind === 'fades') { const fadeIn = Math.max(0, Number(open.fadeIn) || 0), fadeOut = Math.max(0, Number(open.fadeOut) || 0); const next = copy(); for (const track of next) if (open.keys.includes(track.id)) { track.fadeInMs = Math.round(fadeIn); track.fadeOutMs = Math.round(fadeOut) } commit(next, `Fades set on ${open.keys.length} track${open.keys.length === 1 ? '' : 's'}`) }
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
  function playFrom(frame: number) { sendToShell({ type: 'play', id: sessionId, segments: [[Math.max(0, Math.round(frame)), session!.totalFrames]] }) }
  function stop() { sendToShell({ type: 'stopPlayback' }) }
  function audition(which: 'starts' | 'ends', keys?: string[]) {
    const length = app.auditionSeconds * rate, segments: number[][] = []
    for (const track of targets(keys)) {
      segments.push(which === 'starts' ? [track.start, Math.min(track.end, track.start + length)] : [Math.max(track.start, track.end - length), track.end])
    }
    if (!segments.length) return
    sendToShell({ type: 'play', id: sessionId, segments })
    toast(`Playing the ${which === 'starts' ? 'start' : 'end'} of ${segments.length} track${segments.length === 1 ? '' : 's'}, ${app.auditionSeconds} s each`, { ms: 3000 })
  }
  // Just one edge of the current track (or of the grabbed edge's track): for working on a start or an end without waiting through the other
  function hearEdge(edge: 'start' | 'end', trackId = '') {
    const track = (trackId ? tracks.find(candidate => candidate.id === trackId) : grab ? tracks.find(candidate => candidate.id === grab!.trackId) : null) ?? current
    if (!track) { toast('Click a track first'); return }
    const length = app.auditionSeconds * rate
    sendToShell({ type: 'play', id: sessionId, segments: [edge === 'start' ? [track.start, Math.min(track.end, track.start + length)] : [Math.max(track.start, track.end - length), track.end]] })
    if (!trackId) showFrame(edge === 'start' ? track.start : track.end)
  }
  // The waveform follows what's playing
  $effect(() => {
    const frame = app.playhead
    if (!playing || frame < 0 || !session) return
    untrack(() => { const span = waveWidth() * view.framesPerPixel; if (frame < view.start || frame > view.start + span * 0.95) view = waveform?.clampView({ start: frame - span * 0.1, framesPerPixel: view.framesPerPixel }) ?? view })
  })

  // ---- Export (SPEC.md › Export) ----
  const outputProfileId = $derived(session?.outputProfileId && app.outputProfiles.some(profile => profile.id === session!.outputProfileId) ? session.outputProfileId : app.activeOutput)
  async function exportTracks(keys?: string[]) {
    const chosen = targets(keys)
    if (!chosen.length || app.status?.exporting) return
    clearTimeout(saveTimer); sendToShell({ type: 'saveSession', id: sessionId, tracks: $state.snapshot(tracks) })
    exportStatus = `Exporting ${chosen.length} track${chosen.length === 1 ? '' : 's'}…`
    try {
      const result = await request<{ done: number; error: string; tracks: Track[]; folder: string }>({ type: 'export', id: sessionId, trackIds: chosen.map(track => track.id), profileId: outputProfileId })
      exportStatus = ''
      if (result.tracks) tracks = result.tracks
      const exportedPath = result.tracks?.find(track => chosen.some(chosenTrack => chosenTrack.id === track.id) && track.exportedPath)?.exportedPath
      if (result.error) toast(`Exported ${result.done} of ${chosen.length}. ${result.error.split('\n')[0]}`, { ms: 12000 })
      else toast(`Exported ${result.done} track${result.done === 1 ? '' : 's'}`, { action: { label: 'Show in Explorer', key: 'O', run: () => sendToShell({ type: 'openFolder', path: exportedPath ?? result.folder }) } })
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
  function onKey(event: KeyboardEvent) {
    if (!session || dialog || (event.target as HTMLElement)?.matches?.('input, textarea, select')) return
    const key = event.key, lower = key.toLowerCase()
    const step = event.ctrlKey ? 1 : Math.max(1, Math.round(view.framesPerPixel)) * (event.shiftKey ? 10 : 1)
    let handled = true
    if (event.ctrlKey && lower === 'z' && !event.shiftKey) undo()
    else if (event.ctrlKey && (lower === 'y' || (lower === 'z' && event.shiftKey))) redo()
    else if (event.ctrlKey && lower === 'e') exportTracks()
    else if (event.ctrlKey && key === '0') fit()
    else if (key === ' ') { if (playing) stop(); else playFrom(cursor) }
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
    else if (key === ',') hearEdge('start')
    else if (key === '.') hearEdge('end')
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
  const actions: SelectionAction[] = [
    { label: 'Hear starts', run: keys => audition('starts', keys) },
    { label: 'Hear ends', run: keys => audition('ends', keys) },
    { label: 'Export', run: keys => exportTracks(keys) },
    { label: 'Tags…', key: 'T', run: keys => { const chosen = tracks.filter(track => keys.includes(track.id)); dialog = { kind: 'tags', keys, artist: chosen.every(track => track.artist === chosen[0].artist) ? chosen[0].artist : '', album: chosen.every(track => track.album === chosen[0].album) ? chosen[0].album : '' } } },
    { label: 'Fades…', key: 'F', run: keys => { const first = tracks.find(track => keys.includes(track.id)); dialog = { kind: 'fades', keys, fadeIn: String(first?.fadeInMs ?? 0), fadeOut: String(first?.fadeOutMs ?? 0) } } },
    { label: 'Merge', run: keys => mergeTracks(keys) },
    { label: 'Remove', key: 'Delete', danger: true, run: keys => deleteTracks(keys) },
  ]
  const exportedCount = $derived(tracks.filter(track => track.exported).length)
  const auditionChoices: [string, string][] = [['2', '2 s'], ['5', '5 s'], ['10', '10 s'], ['20', '20 s']]
  const outputChoices = $derived(app.outputProfiles.map(profile => [profile.id, profile.name] as [string, string]))
</script>

<svelte:window onkeydown={onKey} />

{#if loadError}
  <div class="empty"><h2>Can't open this session</h2><p class="quiet">{loadError}</p></div>
{:else if !session}
  <div class="empty"><p class="quiet">Opening the session{loadProgress ? `, reading the waveform ${Math.round(loadProgress * 100)}%` : ''}…</p></div>
{:else}
<div class="session">
  <header>
    <input class="name" value={session.name} onchange={(event) => { const name = (event.target as HTMLInputElement).value.trim(); if (name && session) { session.name = name; sendToShell({ type: 'saveSession', id: sessionId, name }) } }} aria-label="Session name" />
    <span class="facts">{new Date(session.created).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' })} · {formatDuration(session.totalFrames / rate)} · {session.device || 'Unknown device'} · {rate / 1000} kHz {session.bitDepth === 32 ? 'float' : session.bitDepth + '-bit'} · {tracks.length} tracks{exportedCount ? `, ${exportedCount} exported` : ''}</span>
    {#if session.clippedSamples > 0}<span class="clip-warning" title="Samples recorded at full scale">{session.clippedSamples} clipped samples</span>{/if}
  </header>

  <div class="toolbar">
    <button class="ghost" class:on={playing} onclick={() => playing ? stop() : playFrom(cursor)} title="Play from the playhead (Space)"><Icon name={playing ? 'stop' : 'play'} size={16} />{playing ? 'Stop' : 'Play'}</button>
    <div class="group" title="This track">
      <span class="group-label">This track</span>
      <button class="ghost" onclick={() => hearEdge('start')} title="Hear this track's start (,)"><Icon name="starts" size={16} />Start<kbd>,</kbd></button>
      <button class="ghost" onclick={() => hearEdge('end')} title="Hear this track's end (.)"><Icon name="ends" size={16} />End<kbd>.</kbd></button>
    </div>
    <div class="group">
      <span class="group-label">{selectedIds.length ? 'Selected' : 'All tracks'}</span>
      <button class="ghost" onclick={() => audition('starts')} title="Run through the start of every track (selected, or all)">Starts</button>
      <button class="ghost" onclick={() => audition('ends')} title="Run through the end of every track">Ends</button>
      <Dropdown label="Each" value={String(app.auditionSeconds)} options={auditionChoices} align="left" onchange={(value) => { app.auditionSeconds = Number(value); sendToShell({ type: 'setAuditionSeconds', seconds: Number(value) }) }} />
    </div>
    <div class="group">
      <button class="ghost icon" onclick={() => zoomAtCursor(0.5)} title="Zoom in (+, or the wheel)"><Icon name="zoom-in" size={16} /></button>
      <button class="ghost icon" onclick={() => zoomAtCursor(2)} title="Zoom out (-)"><Icon name="zoom-out" size={16} /></button>
      <button class="ghost icon" onclick={fit} title="Whole session (Ctrl+0)"><Icon name="fit" size={16} /></button>
      <button class="ghost" class:on={snapToZero} onclick={() => { snapToZero = !snapToZero; writeSetting('snapZero', snapToZero ? '1' : '') }} title="Edges land on the nearest zero crossing">Snap to zero</button>
    </div>
    <div class="group">
      <button class="ghost icon" onclick={splitAtCursor} title="Split at the playhead (X)"><Icon name="split" size={16} /></button>
      <button class="ghost icon" onclick={() => dialog = { kind: 'redetect' }} title="Find the tracks again with the recording profile's gap settings"><Icon name="redetect" size={16} /></button>
    </div>
    <div class="spacer"></div>
    {#if exportStatus}<span class="export-status">{exportStatus}</span>{/if}
    {#if outputChoices.length}<Dropdown label="As" value={outputProfileId} options={outputChoices} onchange={(value) => { if (session) session.outputProfileId = value; sendToShell({ type: 'saveSession', id: sessionId, outputProfileId: value }) }} />{/if}
    <button class="primary export" disabled={!!app.status?.exporting || !tracks.length} onclick={() => exportTracks()} title="Export the selected tracks, or all (Ctrl+E)"><Icon name="export" size={16} />Export {selectedIds.length ? selectedIds.length : 'all'}</button>
  </div>

  <Hint id="waveform-basics" text="Wheel zooms (down to single samples), Ctrl+wheel makes the wave taller, Shift+wheel scrolls. Drag a green start or red end line to move it. Click to place the playhead; S and E set the current track's start and end there." />
  <div class="wave-host" bind:this={waveHost}>
    <Waveform bind:this={waveform} bind:view {sessionId} sampleRate={rate} channels={session.channels} totalFrames={session.totalFrames} {tracks} changes={session.changes} {selectedIds} currentId={current?.id ?? ''} {cursor} playhead={playing ? app.playhead : -1} {grab}
      onCursor={(frame) => { cursor = frame; grab = null; if (playing) playFrom(frame) }} {onEdge} {onEdgeDone} onPickTrack={(trackId, additive) => { currentId = trackId; if (additive) selectKeys(scope, Array.from(new Set(selectedIds.concat([trackId]))), actions, 'track'); scrollRowIntoView(trackId) }} onZoomTrack={zoomToTrack} />
  </div>
  <div class="readout"><span>Playhead {formatTime(cursor, rate)}</span>{#if view.framesPerPixel < 2}<span>sample {cursor.toLocaleString()}</span>{/if}{#if current}<span>Track {tracks.indexOf(current) + 1}: {formatTime(current.start, rate)} – {formatTime(current.end, rate)}</span>{/if}{#if grab}<span class="grabbed">{grab.edge === 'start' ? 'Start' : 'End'} grabbed: ← → nudge, Enter hear it, Esc let go</span>{/if}<span class="key"><i class="swatch start"></i>track start <i class="swatch end"></i>track end <i class="swatch change"></i>name changed while recording</span></div>

  <div class="list" data-row-list use:selectable={{ scope, noun: 'track', actions }}>
    <div class="row head"><span>#</span><span>Hear</span><span>Start</span><span>Length</span><span>Artist</span><span>Title</span><span>Album</span><span>Fades</span><span></span></div>
    {#each tracks as track, index (track.id)}
      <div class="row" class:current={track.id === current?.id} data-select-key={track.id} role="button" tabindex="-1" onclick={() => { currentId = track.id; cursor = track.start; showFrame(track.start) }} ondblclick={() => zoomToTrack(track.id)} onkeydown={() => { }}>
        <span class="number">{index + 1}</span>
        <span class="hear no-hold"><button onclick={(event) => { event.stopPropagation(); hearEdge('start', track.id) }} title="Hear this track's start" aria-label="Hear start"><Icon name="starts" size={14} /></button><button onclick={(event) => { event.stopPropagation(); hearEdge('end', track.id) }} title="Hear this track's end" aria-label="Hear end"><Icon name="ends" size={14} /></button></span>
        <span class="time">{formatTime(track.start, rate, false)}</span>
        <span class="time">{formatDuration((track.end - track.start) / rate)}</span>
        <input class="no-hold" value={track.artist} placeholder="Artist" onchange={(event) => editField(track, 'artist', (event.target as HTMLInputElement).value.trim())} onclick={(event) => event.stopPropagation()} />
        <input class="no-hold" value={track.title} placeholder="Title" onchange={(event) => editField(track, 'title', (event.target as HTMLInputElement).value.trim())} onclick={(event) => event.stopPropagation()} />
        <input class="no-hold" value={track.album} placeholder="Album" onchange={(event) => editField(track, 'album', (event.target as HTMLInputElement).value.trim())} onclick={(event) => event.stopPropagation()} />
        <span class="fades">{track.fadeInMs || track.fadeOutMs ? `${track.fadeInMs}/${track.fadeOutMs} ms` : ''}</span>
        <span class="status">{#if track.exported}<button class="exported no-hold" title={track.exportedPath} onclick={(event) => { event.stopPropagation(); sendToShell({ type: 'openFolder', path: track.exportedPath }) }}>Exported</button>{/if}</span>
      </div>
    {:else}
      <div class="none quiet">No tracks found. Place the playhead and press N to add one, or use Redetect.</div>
    {/each}
  </div>
  <div class="footer">
    <Hint id="track-select" text="The two small buttons on each row play that track's start or end. Hold a row, right-click it or Ctrl+click to select several: the bar at the bottom runs through their starts or ends, exports, tags, fades, merges or removes them." />
    <button class="ghost danger" onclick={() => dialog = { kind: 'close' }}><Icon name="trash" size={15} />Close session…</button>
  </div>
</div>
{/if}

{#if dialog?.kind === 'tags'}
  <Dialog title="Tags for {dialog.keys.length} track{dialog.keys.length === 1 ? '' : 's'}" text="Empty fields are left as they are." confirmLabel="Set" onconfirm={applyDialog} oncancel={() => dialog = null}>
    <label>Artist <input bind:value={dialog.artist} /></label>
    <label>Album <input bind:value={dialog.album} /></label>
  </Dialog>
{:else if dialog?.kind === 'fades'}
  <Dialog title="Fades for {dialog.keys.length} track{dialog.keys.length === 1 ? '' : 's'}" text="Lengths in milliseconds; 0 is no fade. You hear them when auditioning, and they're in the exported files." confirmLabel="Set" onconfirm={applyDialog} oncancel={() => dialog = null}>
    <label>Fade in <input bind:value={dialog.fadeIn} inputmode="numeric" /></label>
    <label>Fade out <input bind:value={dialog.fadeOut} inputmode="numeric" /></label>
  </Dialog>
{:else if dialog?.kind === 'redetect'}
  <Dialog title="Find the tracks again?" text="Builds the track list again from the names noted while recording and the recording profile's gap settings. Your edits to this list are replaced (Undo brings them back)." confirmLabel="Redetect" onconfirm={applyDialog} oncancel={() => dialog = null} />
{:else if dialog?.kind === 'close'}
  <Dialog title="Close this session?" text={`This deletes the session's recording (the WAV) for good.${tracks.length - exportedCount > 0 ? ` ${tracks.length - exportedCount} of ${tracks.length} tracks haven't been exported yet.` : ' Every track has been exported.'} Exported files are kept.`} confirmLabel="Delete recording" danger onconfirm={applyDialog} oncancel={() => dialog = null} />
{/if}

<style>
  .session { display: flex; flex-direction: column; height: 100%; min-height: 0; gap: 8px; }
  .empty { padding: 40px; }
  header { display: flex; align-items: baseline; gap: 12px; flex-wrap: wrap; }
  .name { font-size: 20px; font-weight: 600; background: none; border: 1px solid transparent; padding: 2px 6px; margin-left: -7px; min-width: 120px; width: 22ch; }
  .name:hover { border-color: var(--line); }
  .facts { color: var(--fg-quiet); font-size: 12.5px; }
  .clip-warning { color: var(--danger); font-size: 12px; font-weight: 600; }
  .toolbar { display: flex; align-items: center; gap: 10px; flex-wrap: wrap; }
  .group { display: flex; align-items: center; gap: 4px; }
  .ghost.icon { padding: 5px 7px; }
  .spacer { flex: 1; }
  .export { display: inline-flex; align-items: center; gap: 6px; }
  .export-status { color: var(--fg-quiet); font-size: 12px; max-width: 320px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .wave-host { flex: 1 1 52%; min-height: 220px; }
  .readout { display: flex; gap: 18px; font-family: var(--font-display); font-size: 12px; color: var(--fg-quiet); }
  .grabbed { color: var(--accent); font-family: var(--font-ui); }
  .key { margin-left: auto; display: flex; align-items: center; gap: 6px; font-family: var(--font-ui); font-size: 11.5px; white-space: nowrap; }
  .swatch { display: inline-block; width: 3px; height: 12px; margin-left: 8px; border-radius: 1px; }
  .swatch.start { background: var(--start); } .swatch.end { background: var(--end); }
  .swatch.change { width: 0; border-left: 2px dashed var(--warn); border-radius: 0; }
  .group-label { font-size: 10.5px; font-weight: 600; letter-spacing: 0.05em; text-transform: uppercase; color: var(--fg-quiet); margin-right: 2px; }
  .list { flex: 1 1 38%; min-height: 120px; overflow-y: auto; border-top: 1px solid var(--line); }
  .row { display: grid; grid-template-columns: 34px 58px 78px 72px minmax(80px, 1fr) minmax(120px, 1.6fr) minmax(80px, 1fr) 90px 80px; gap: 8px; align-items: center; padding: 3px 8px; border-radius: 6px; cursor: default; }
  .row:not(.head):hover { background: var(--hover); }
  .row.current { box-shadow: inset 3px 0 0 var(--accent); }
  .head { position: sticky; top: 0; z-index: 1; padding: 6px 8px; font-size: 11px; color: var(--fg-quiet); text-transform: uppercase; letter-spacing: 0.05em; background: var(--panel-solid); border-radius: 0; }
  .number, .time, .fades { font-family: var(--font-display); font-size: 12px; color: var(--fg-quiet); }
  .row input { background: transparent; border-color: transparent; padding: 3px 6px; min-width: 0; }
  .row input:hover { border-color: var(--line); }
  .row input:focus { background: var(--panel); }
  .hear { display: flex; gap: 2px; }
  .hear button { display: grid; place-items: center; width: 26px; height: 24px; padding: 0; background: none; border: 1px solid transparent; border-radius: 6px; color: var(--fg-quiet); cursor: pointer; }
  .hear button:hover { background: var(--accent-soft); color: var(--accent); border-color: var(--line); }
  .exported { background: none; border: 0; color: #3ecf6e; font: inherit; font-size: 12px; cursor: pointer; padding: 0; }
  .exported:hover { text-decoration: underline; }
  .none { padding: 16px 8px; }
  .footer { display: flex; align-items: center; justify-content: space-between; gap: 12px; }
  .footer :global(.hint) { margin: 0; flex: 1; }
  label { display: flex; flex-direction: column; gap: 4px; font-size: 12px; color: var(--fg-quiet); }
</style>
