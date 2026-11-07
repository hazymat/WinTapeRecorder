<script lang="ts">
  import { untrack } from 'svelte'
  import { request, sendToShell, onShellMessage } from '../bridge'
  import { app, nav, toast, formatTime, formatDuration, writeSetting, readSetting, describeCutoff, shortCutoff, medianCutoff, type SessionDetail, type Track, type AlbumMatch } from '../state.svelte'
  import { selection, selectKeys, clearSelection, type SelectionAction } from '../lib/selection.svelte'
  import { selectable } from '../lib/selectable.svelte'
  import Waveform, { type WaveView, type Grab } from '../parts/Waveform.svelte'
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
    focused = target; currentId = track.id; grab = { trackId: track.id, edge: target.edge }
    const frame = track[target.edge]; cursor = frame
    // Zoomed to what you'll hear (SPEC.md › Editing): a start shows half the Listen length before it and the full length after;
    // an end the full length before and half after. Zoom freely from there
    const listen = app.auditionSeconds * rate, span = listen * 1.5
    view = waveform.clampView({ start: target.edge === 'start' ? frame - listen / 2 : frame - listen, framesPerPixel: span / waveWidth() })
    scrollRowIntoView(track.id)
    if (play) hearEdge(target.edge, track.id)
  }
  // Next to confirm (C): the next start or end without a ✓, counting from where you are: just after the edge last jumped to if
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
  function confirmFocused() { const target = grab ? { trackId: grab.trackId, edge: grab.edge } : focused; const track = target ? tracks.find(candidate => candidate.id === target.trackId) : null; if (!track || !target) { toast('Pick a start or end first: click Start or End on a track'); return } toggleChecked(track, target.edge) }
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
  function setListen(seconds: number) { app.auditionSeconds = seconds; sendToShell({ type: 'setAuditionSeconds', seconds }) }
  function nextListen() { const choices = auditionChoices.map(([value]) => Number(value)); const at = choices.indexOf(app.auditionSeconds); setListen(choices[(at + 1) % choices.length]); toast(`Listening to ${app.auditionSeconds} s of each start or end`, { ms: 1500 }) }
  const sessionCutoff = $derived(session ? medianCutoff(session.quality ?? [], 0, session.totalFrames) : null)
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
  function setSkip(track: Track, skip: boolean) { const next = copy(); const edited = next.find(candidate => candidate.id === track.id)!; edited.skip = skip; commit(next, skip ? `Track ${tracks.indexOf(track) + 1} left out of the export` : `Track ${tracks.indexOf(track) + 1} back in`) }
  function editField(track: Track, field: 'artist' | 'title' | 'album' | 'year' | 'genre', value: string) {
    if ((track[field] ?? '') === value) return
    const next = copy(); const edited = next.find(candidate => candidate.id === track.id)!; edited[field] = value; edited.exported = edited.exported && false
    commit(next, `Track ${tracks.indexOf(track) + 1}: ${field} changed`)
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
        if (name === 'trackNumber' || name === 'trackCount') track[name] = Number(value) || 0
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
    untrack(() => { const span = waveWidth() * view.framesPerPixel; if (frame < view.start || frame > view.start + span * 1.01) view = waveform?.clampView({ start: frame - span * 0.1, framesPerPixel: view.framesPerPixel }) ?? view })
  })

  // ---- Export (SPEC.md › Export) ----
  const outputProfileId = $derived(session?.outputProfileId && app.outputProfiles.some(profile => profile.id === session!.outputProfileId) ? session.outputProfileId : app.activeOutput)
  // Tags worth having on every file before it goes out (SPEC.md › Export): the dialog offers to fill them in first
  const wanted: [keyof Track, string][] = [['artist', 'artist'], ['album', 'album'], ['albumArtist', 'album artist'], ['genre', 'genre']]
  let exportCheck = $state<null | { keys: string[]; missing: string; unconfirmed: number }>(null)
  // Hold a row's Start or End to confirm it (it fills, then turns green); hold again to undo. A plain click hears it
  let holding = $state<{ trackId: string; edge: 'start' | 'end' } | null>(null), holdTimer = 0, held = false
  function holdDown(track: Track, edge: 'start' | 'end', event: PointerEvent) {
    if (event.button !== 0) return
    held = false; holding = { trackId: track.id, edge }; clearTimeout(holdTimer)
    holdTimer = window.setTimeout(() => { held = true; holding = null; toggleChecked(track, edge) }, 550)
  }
  function holdUp() { clearTimeout(holdTimer); holding = null }
  // A start or end confirmed by hand: green once the user is happy with it
  function toggleChecked(track: Track, edge: 'start' | 'end') {
    const next = copy(), edited = next.find(candidate => candidate.id === track.id)!
    const on = !(edge === 'start' ? edited.startChecked : edited.endChecked)
    if (edge === 'start') edited.startChecked = on; else edited.endChecked = on
    commit(next, `Track ${tracks.indexOf(track) + 1} ${edge} ${on ? 'confirmed' : 'not confirmed'}`)
  }
  async function exportTracks(keys?: string[], checked = false) {
    const chosen = targets(keys)
    if (!chosen.length || app.status?.exporting) return
    if (!checked) {
      // Empty tags and unconfirmed cuts are pointed out once; exporting anyway is always possible
      const missing = wanted.filter(([field]) => chosen.some(track => !(track[field] as string | undefined))).map(([, label]) => label)
      const unconfirmed = chosen.reduce((count, track) => count + (track.startChecked ? 0 : 1) + (track.endChecked ? 0 : 1), 0)
      if (missing.length || unconfirmed) { exportCheck = { keys: chosen.map(track => track.id), missing: missing.join(', '), unconfirmed }; return }
    }
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
    if (event.ctrlKey && lower === 'v' && inTagsStep() && !tagging && !lookingUp) chooseCover(true, albums.length === 1 ? albums[0] : undefined)
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
    else if (lower === 'l') nextListen()
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
  const actions: SelectionAction[] = [
    { label: 'Hear starts', run: keys => audition('starts', keys) },
    { label: 'Hear ends', run: keys => audition('ends', keys) },
    { label: 'Export', run: keys => exportTracks(keys) },
    { label: 'Tags…', key: 'T', run: keys => tagging = keys },
    { label: 'Look up…', key: 'L', run: keys => openLookup(keys) },
    { label: 'Fades…', key: 'F', run: keys => { const first = tracks.find(track => keys.includes(track.id)); dialog = { kind: 'fades', keys, fadeIn: String(first?.fadeInMs ?? 0), fadeOut: String(first?.fadeOutMs ?? 0) } } },
    { label: 'Merge', run: keys => mergeTracks(keys) },
    { label: 'Remove', key: 'Delete', danger: true, run: keys => deleteTracks(keys) },
  ]
  const exportedCount = $derived(tracks.filter(track => track.exported).length)
  // Tick boxes: the same selection as Ctrl+click, so the bar at the bottom acts on the ticked tracks
  function toggleOne(trackId: string) {
    const next = selectedIds.includes(trackId) ? selectedIds.filter(id => id !== trackId) : selectedIds.concat([trackId])
    if (next.length) selectKeys(scope, tracks.filter(track => next.includes(track.id)).map(track => track.id), actions, 'track'); else clearSelection()
  }
  function toggleAll() { if (selectedIds.length === tracks.length) clearSelection(); else selectKeys(scope, tracks.map(track => track.id), actions, 'track') }
  const auditionChoices: [string, string][] = [['2', '2 s'], ['5', '5 s'], ['10', '10 s'], ['20', '20 s']]
  const outputChoices = $derived(app.outputProfiles.map(profile => [profile.id, profile.name] as [string, string]))
</script>

<!-- Space is the app's play/stop: it must never also press the button last clicked (that replayed an old audition) -->
<svelte:window onkeydown={onKey} onkeyup={(event) => { if (event.key === ' ' && !(event.target as HTMLElement)?.matches?.('input, textarea, select')) event.preventDefault() }} />

{#if loadError}
  <div class="empty"><h2>Can't open this session</h2><p class="quiet">{loadError}</p></div>
{:else if !session}
  <div class="empty"><p class="quiet">Opening the session{loadProgress ? `, reading the waveform ${Math.round(loadProgress * 100)}%` : ''}…</p></div>
{:else}
<div class="session">
  <header>
    <input class="name" value={session.name} onchange={(event) => { const name = (event.target as HTMLInputElement).value.trim(); if (name && session) { session.name = name; sendToShell({ type: 'saveSession', id: sessionId, name }) } }} aria-label="Session name" />
    <span class="facts">{new Date(session.created).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' })} · {formatDuration(session.totalFrames / rate)} · {session.device || 'Unknown device'} · {rate / 1000} kHz {session.bitDepth === 32 ? 'float' : session.bitDepth + '-bit'} · {tracks.length} tracks{exportedCount ? `, ${exportedCount} exported` : ''}</span>
    {#if session.cutOff}<span class="clip-warning" title="The app or PC stopped while this was recording. Everything up to the last couple of seconds was saved.">Cut off by a crash or power cut at {formatTime(session.totalFrames, rate, false)}</span>{/if}
    {#if session.otherSounds?.length}<span class="clip-warning" title={session.otherSounds.map(other => `${formatTime(other.frame, rate, false)}: ${other.app}, ${(other.ms / 1000).toFixed(1)} s`).join(String.fromCharCode(10))}>{session.otherSounds.length} sound{session.otherSounds.length === 1 ? '' : 's'} from other apps</span>{/if}
    {#if session.clippedSamples > 0}<span class="clip-warning" title="Samples recorded at full scale">{session.clippedSamples} clipped samples</span>{/if}
    {#if sessionCutoff}<span class="source-chip" title={describeCutoff(sessionCutoff)}>Source: spectrum stops at {(sessionCutoff / 1000).toFixed(1)} kHz, {describeCutoff(sessionCutoff)}</span>{/if}
    {#if session.dropouts?.length}<span class="clip-warning" title={session.dropouts.map(dropout => `${formatTime(dropout.frame, rate, false)}: ${dropout.ms} ms lost`).join('\n')}>{session.dropouts.length} dropout{session.dropouts.length === 1 ? '' : 's'} (marked in red on the ruler)</span>{/if}
    <span class="header-actions">
      <button class="ghost" onclick={() => sendToShell({ type: 'openFolder', path: session!.folder + '\\audio.wav' })} title="Open the session's folder in Explorer, with the WAV selected"><Icon name="folder" size={15} />Show files</button>
      <button class="ghost danger" onclick={() => dialog = { kind: 'close' }}><Icon name="trash" size={15} />Close session…</button>
    </span>
  </header>

  <nav class="steps" aria-label="Steps">
    <button class="step" class:active={step === 'cuts'} class:done={unchecked.length === 0} onclick={() => step = 'cuts'}><span class="step-number">1</span><span class="step-text"><strong>Check the cuts</strong><span>{unchecked.length === 0 ? 'all confirmed' : `${unchecked.length} to confirm${doubtful.length ? `, ${doubtful.length} doubtful` : ''}`}</span></span></button>
    <button class="step" class:active={step === 'tags'} class:done={untagged.length === 0} onclick={() => step = 'tags'}><span class="step-number">2</span><span class="step-text"><strong>Tags</strong><span>{untagged.length === 0 ? 'complete' : `${untagged.length} track${untagged.length === 1 ? '' : 's'} missing details`}</span></span></button>
    <button class="step" class:active={step === 'export'} class:done={unexported.length === 0 && exportable.length > 0} onclick={() => step = 'export'}><span class="step-number">3</span><span class="step-text"><strong>Export</strong><span>{exportable.length === 0 ? 'nothing to export' : unexported.length === 0 ? `${exportable.length} exported${unverified.length ? `, ${unverified.length} failed the check` : ', all verified'}` : `${unexported.length} of ${exportable.length} to go`}</span></span></button>
    <div class="step-actions">
      {#if step === 'cuts'}
        <button class="primary" onclick={checkNext} title="Jump to the next start or end without a ✓, from the track you're on, zoomed in, and play it (C)">Next to confirm<kbd>C</kbd></button>
        
        <Dropdown label="Track ends" value={betweenTracks} options={[['meet', 'Meet the next start'], ['trim', 'Trim silence'], ['keep', 'Keep as recorded']]} onchange={setBetween} />
      {:else if step === 'tags'}
        <button class="primary" onclick={() => tagging = (selectedIds.length ? selectedIds : untagged.length ? untagged.map(track => track.id) : exportable.map(track => track.id))}>{selectedIds.length ? `Edit ${selectedIds.length} selected…` : untagged.length ? `Fill in ${untagged.length}…` : 'Edit tags…'}</button>
        <button class="ghost" onclick={() => openLookup(exportable.map(track => track.id))} title="Search MusicBrainz, iTunes or Discogs yourself and pick the album">Look up album…</button>
      {:else}
        {#if exportStatus}<span class="export-status">{exportStatus}</span>{/if}
        {#if outputChoices.length}<Dropdown label="Output profile" value={outputProfileId} options={outputChoices} onchange={(value) => { if (session) session.outputProfileId = value; sendToShell({ type: 'saveSession', id: sessionId, outputProfileId: value }) }} />{/if}
        <button class="primary" disabled={!!app.status?.exporting || !exportable.length} onclick={() => exportTracks(selectedIds.length ? selectedIds : unexported.length ? unexported.map(track => track.id) : undefined)} title="Ctrl+E"><Icon name="export" size={16} />{selectedIds.length ? `Export ${selectedIds.length} selected` : unexported.length && unexported.length < exportable.length ? `Export the ${unexported.length} left` : 'Export all'}</button>
      {/if}
    </div>
  </nav>
  <div class="toolbar">
    <button class="ghost" class:on={playing} onclick={() => playing ? stop() : playFrom(cursor)} title="Play from the playhead: click in the wave or on a track to move it (Space)"><Icon name={playing ? 'stop' : 'play'} size={16} />{playing ? 'Stop' : 'Play'}<kbd>Space</kbd></button>
    {#if step === 'cuts'}
    <div class="group">
      <Dropdown label="Listen" value={String(app.auditionSeconds)} options={auditionChoices} align="left" onchange={(value) => setListen(Number(value))} />
      <kbd class="loose" title="L changes how much of each start or end you hear">L</kbd>
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
    </div>
    {#if step === 'cuts'}
    <div class="group">
      <button class="ghost icon" onclick={splitAtCursor} title="Split at the playhead (X)"><Icon name="split" size={16} /><kbd>X</kbd></button>
      <button class="ghost icon" onclick={() => dialog = { kind: 'redetect' }} title="Find the tracks again with the recording profile's gap settings"><Icon name="redetect" size={16} /></button>
    </div>
    {/if}
    <div class="spacer"></div>
    {#if exportStatus && step !== 'export'}<span class="export-status">{exportStatus}</span>{/if}
  </div>

  <Hint id="waveform-cuts" text="Next to confirm (C) jumps to the next start or end without a ✓, from the track you're on, and plays it. Drag the line, nudge it with ← →, or press S / E to set it at the playhead; then tick ✓ beside it in the list. Wheel zooms, Shift+wheel scrolls, Ctrl+wheel sets the height, W switches wave and spectrogram." />
  <div class="wave-host" bind:this={waveHost}>

    <Waveform bind:this={waveform} bind:view {sessionId} sampleRate={rate} channels={session.channels} totalFrames={session.totalFrames} {tracks} changes={session.changes} dropouts={session.dropouts ?? []} mode={waveMode} {autoHeight} {selectedIds} currentId={current?.id ?? ''} {cursor} playhead={playing ? app.playhead : -1} {grab}
      onCursor={(frame) => { cursor = frame; grab = null; if (playing) playFrom(frame) }} {onEdge} {onEdgeDone} onRelease={() => grab = null} onPickTrack={(trackId, additive) => { currentId = trackId; if (additive) selectKeys(scope, Array.from(new Set(selectedIds.concat([trackId]))), actions, 'track'); scrollRowIntoView(trackId) }} onZoomTrack={zoomToTrack} />
  </div>
  <div class="readout"><div class="wave-mode" role="group" aria-label="Wave or spectrogram">
      <button class:on={waveMode === 'wave'} onclick={() => setWaveMode('wave')} title="Waveform: levels, starts and ends (W switches)">Wave</button><button class:on={waveMode === 'spectrogram'} onclick={() => setWaveMode('spectrogram')} title="Spectrogram: frequency over time. A flat top edge means a lossy source; a vertical gap is a dropout (W switches)">Spectrogram</button><kbd>W</kbd>
    </div><span>Playhead {formatTime(cursor, rate)}</span>{#if view.framesPerPixel < 2}<span>sample {cursor.toLocaleString()}</span>{/if}{#if current}<span>Track {tracks.indexOf(current) + 1}: {formatTime(current.start, rate)} – {formatTime(current.end, rate)}</span>{/if}{#if grab}<span class="grabbed">{grab.edge === 'start' ? 'Start' : 'End'} grabbed: ← → nudge, Enter hear it, Esc let go</span>{/if}<span class="key"><i class="swatch start"></i>track start <i class="swatch end"></i>track end <i class="swatch change"></i>name changed while recording</span></div>

  {#if step === 'tags'}
    <div class="tags-step">
      {#if !session.albumLooked}
        <p class="quiet suggest-note">Matching albums from your track names{albumProgress ? ` (${albumProgress.done} of ${albumProgress.total} looked up)` : ''}…</p>
      {:else if !albums.length}
        <p class="quiet suggest-note">No album found online for these titles. <button class="link" onclick={() => openLookup(exportable.map(track => track.id))}>Look up album…</button> searches yourself; Edit tags… fills them in by hand.</p>
      {:else}
        {#if albums.length > 1}<p class="quiet suggest-note">{albums.length} albums in this session. Each exports to its own folder.</p>{/if}
        {#each albums as match, albumIndex (albumIndex + match.releaseId)}
          {@const list = albumTracks(match)}
          {@const offers = albumOffers(match)}
          {@const pending = offers.filter(offer => !offer.same && !offer.kept)}
          {@const ownCover = commonValue(list, 'artPath')}
          {@const missing = missingFrom(match)}
          {@const coverShown = !!match.artUrl && !failedCovers[match.artUrl]}
          {@const playerCover = list.map(track => track.playerArtPath ?? '').find(path => path && thumbs[path]) ?? ''}
          <div class="album-card">
            <div class="cover-col">
              <div class="covers">
                <figure><span class="cover-label">Yours</span>{#if ownCover && thumbs[ownCover]}<img src={thumbs[ownCover]} alt="Your cover" />{:else}<div class="no-cover">{ownCover === null ? 'Mixed' : 'None'}</div>{/if}</figure>
                {#if coverShown}<figure><span class="cover-label">{match.source}</span><img src={match.artUrl} alt="Cover from {match.source}" onerror={() => failedCovers = Object.assign({}, failedCovers, { [match.artUrl]: true })} /></figure>{/if}
                {#if playerCover && playerCover !== ownCover}<figure><span class="cover-label">Player</span><img src={thumbs[playerCover]} alt="The picture the player sent" /></figure>{/if}
              </div>
              <div class="cover-buttons">
                {#if coverShown}<button class="ghost" onclick={() => useMatchCover(match)} title="Download this cover and put it on this album's tracks">Use {match.source}'s</button>{/if}
                {#if playerCover && playerCover !== ownCover}<button class="ghost" onclick={() => applyTags(list.map(track => track.id), { artPath: playerCover }, false)} title="The picture the player sent while it played (often smaller than a database cover)">Use the player's</button>{/if}
                <button class="ghost" onclick={() => chooseCover(false, match)} title="A JPEG or PNG of your own, on this album's tracks (or the selected ones)">Choose…</button>
                <button class="ghost" onclick={() => chooseCover(true, match)} title="The picture on the clipboard, on this album's tracks (or the selected ones)">Paste{#if albums.length === 1}<kbd>Ctrl+V</kbd>{/if}</button>
              </div>
            </div>
            <div class="album-body">
              <div class="album-head">
                <span><span class="suggest-source">{match.source}</span> <strong>{match.album}</strong>{match.albumArtist ? ` · ${match.albumArtist}` : ''}{match.detail ? ` · ${match.detail}` : ''}{albums.length > 1 ? ` · your tracks ${tracks.indexOf(list[0]) + 1}–${tracks.indexOf(list[list.length - 1]) + 1}` : ''}</span>
                <span class="album-actions">
                  {#if pending.length > 1}<button class="primary" onclick={() => useAllAlbum(match)}>Use all</button>{/if}
                  <button class="ghost" onclick={() => openLookup(list.map(track => track.id))} title="Pick a different album for these tracks yourself">Not this album?</button>
                </span>
              </div>
              <div class="album-fields">
                {#each offers as offer (offer.field)}
                  <span class="field-label">{offer.label}</span>
                  <span class="field-theirs">{offer.theirs || '—'}</span>
                  {#if !offer.theirs}<span class="quiet">not given</span><span></span>
                  {:else if offer.same}<span class="field-state ok">✓ yours matches</span><span></span>
                  {:else if offer.kept}<span class="field-state">keeping yours: {offer.yours === null ? 'mixed' : offer.yours || 'empty'}</span><button class="ghost" onclick={() => keepAlbumField(albumIndex, offer.field, false)}>Undo</button>
                  {:else}<span class="field-state">yours: {offer.yours === null ? 'differs between tracks' : offer.yours || 'empty'}</span><span class="field-buttons"><button class="ghost" onclick={() => useAlbumField(match, [offer.field])}>Use</button><button class="ghost" onclick={() => keepAlbumField(albumIndex, offer.field, true)}>Keep mine</button></span>{/if}
                {/each}
              </div>
              {#if match.trackTitles.length}
                <div class="album-complete" class:complete={missing.length === 0}>{match.trackTitles.length - missing.length} of the release's {match.trackTitles.length} tracks are here{#if missing.length}{' · '}missing: {missing.join(', ')}{/if}</div>
              {/if}
            </div>
          </div>
        {/each}
        <div class="match-again"><button class="link" onclick={() => { if (session) session.albumLooked = false; sendToShell({ type: 'suggestAgain', id: sessionId }) }}>Match albums again</button></div>
        {#if trackOffers.length}
          <div class="track-offers">
            <div class="track-offers-head"><span><strong>Titles and numbers</strong><span class="quiet">{' · '}as the release has them</span></span>{#if trackOffers.length > 1}<button class="ghost" onclick={() => useTrackOffer(trackOffers)}>Use for all {trackOffers.length}</button>{/if}</div>
            {#each trackOffers as track (track.id)}
              {@const suggestion = track.suggestion!}
              <div class="suggest">
                <span class="suggest-track">{tracks.indexOf(track) + 1}. {track.title}</span>
                <span class="arrow">→</span>
                {#if suggestion.title !== track.title}<span class="chip-text"><span class="chip-label">Title</span>{suggestion.title}</span>{/if}
                {#if suggestion.trackNumber}<span class="chip-text"><span class="chip-label">No.</span>{suggestion.trackNumber}{suggestion.trackCount ? '/' + suggestion.trackCount : ''}</span>{/if}
                <span class="suggest-actions"><button class="ghost" onclick={() => useTrackOffer([track])}>Use</button><button class="ghost" onclick={() => dismissSuggestion(track)}>Keep mine</button></span>
              </div>
            {/each}
          </div>
        {/if}
        {#each offAlbum as track (track.id)}
          <div class="suggest off-album">
            <span class="suggest-track">{tracks.indexOf(track) + 1}. {track.title}</span>
            <span>isn't on this release.</span>
            <span class="suggest-actions"><button class="ghost" onclick={() => openLookup([track.id])}>Look it up…</button><button class="ghost" onclick={() => dismissSuggestion(track)}>It's fine</button></span>
          </div>
        {/each}
      {/if}
    </div>
  {/if}
  <div class="list" data-row-list use:selectable={{ scope, noun: 'track', actions }}>
    <div class="row head"><span><input type="checkbox" class="tick" checked={tracks.length > 0 && selectedIds.length === tracks.length} indeterminate={selectedIds.length > 0 && selectedIds.length < tracks.length} onchange={toggleAll} title="Select all tracks" aria-label="Select all tracks" /></span><span>Hear (hold: ✓)</span><span>Start</span><span>Length</span><span></span><span>Artist</span><span>Album</span><span>Year</span><span>Title</span><span>Genre</span><span>Source</span><span>Fades</span><span></span></div>
    {#each tracks as track, index (track.id)}
      {@const cutoff = medianCutoff(session.quality ?? [], track.start, track.end)}
      <div class="row" class:current={track.id === current?.id} data-select-key={track.id} role="button" tabindex="-1" onclick={() => { currentId = track.id; cursor = track.start; showFrame(track.start) }} ondblclick={() => zoomToTrack(track.id)} onkeydown={() => { }}>
        <span class="number"><input type="checkbox" class="tick no-hold" checked={selectedIds.includes(track.id)} onclick={(event) => { event.stopPropagation(); toggleOne(track.id) }} aria-label="Select track {index + 1}" /> {index + 1}</span>
        <span class="hear no-hold"><button class="hear-start" class:checked={track.startChecked} class:holding={holding?.trackId === track.id && holding.edge === 'start'} onpointerdown={(event) => holdDown(track, 'start', event)} onpointerup={holdUp} onpointerleave={holdUp} onclick={(event) => { event.stopPropagation(); if (held) { held = false; return } focusEdge({ trackId: track.id, edge: 'start' }) }} title={`Click: hear this track's start, zoomed in. Hold: ${track.startChecked ? 'undo the ✓' : 'confirm it (✓, green)'}`} aria-label="Hear start"><Icon name="play" size={11} /><span>{track.startChecked ? '✓ ' : ''}Start</span></button><button class="hear-end" class:checked={track.endChecked} class:holding={holding?.trackId === track.id && holding.edge === 'end'} onpointerdown={(event) => holdDown(track, 'end', event)} onpointerup={holdUp} onpointerleave={holdUp} onclick={(event) => { event.stopPropagation(); if (held) { held = false; return } focusEdge({ trackId: track.id, edge: 'end' }) }} title={`Click: hear this track's end, zoomed in. Hold: ${track.endChecked ? 'undo the ✓' : 'confirm it (✓, green)'}`} aria-label="Hear end"><span>End{track.endChecked ? ' ✓' : ''}</span><Icon name="play" size={11} /></button></span>
        <span class="edge-cell"><button class="time edge no-hold" class:doubt={!track.startSure && !track.startChecked} class:checked={track.startChecked} title={`${track.startNote || 'Start'}${track.startChecked ? ' · checked' : ''}. Click to zoom in and hear it.`} onclick={(event) => { event.stopPropagation(); focusEdge({ trackId: track.id, edge: 'start' }) }}>{track.startChecked ? '✓ ' : !track.startSure ? '? ' : ''}{formatTime(track.start, rate, false)}</button>{#if track.detectedStart != null && track.detectedStart >= 0 && track.start !== track.detectedStart}<button class="revert no-hold" onclick={(event) => { event.stopPropagation(); revertEdge(track, 'start') }} title={`Back to where it was detected (${formatTime(track.detectedStart, rate, false)})`}>↺</button>{/if}</span>
        <span class="edge-cell"><button class="time edge no-hold" class:doubt={!track.endSure && !track.endChecked} class:checked={track.endChecked} title={`${track.endNote || 'End'}${track.endChecked ? ' · checked' : ''}. Click to zoom in and hear it.`} onclick={(event) => { event.stopPropagation(); focusEdge({ trackId: track.id, edge: 'end' }) }}>{track.endChecked ? '✓ ' : !track.endSure ? '? ' : ''}{formatDuration((track.end - track.start) / rate)}</button>{#if track.detectedEnd != null && track.detectedEnd >= 0 && track.end !== track.detectedEnd}<button class="revert no-hold" onclick={(event) => { event.stopPropagation(); revertEdge(track, 'end') }} title={`End back to where it was detected (${formatTime(track.detectedEnd, rate, false)})`}>↺</button>{/if}</span>
        <button class="art-cell no-hold" onclick={(event) => { event.stopPropagation(); tagging = selectedIds.includes(track.id) ? selectedIds.slice() : [track.id] }} title="Edit tags and picture">{#if track.artPath && thumbs[track.artPath]}<img src={thumbs[track.artPath]} alt="" />{:else}<span>♪</span>{/if}</button>
        <input class="no-hold" value={track.artist} placeholder="Artist" onchange={(event) => editField(track, 'artist', (event.target as HTMLInputElement).value.trim())} onclick={(event) => event.stopPropagation()} />
        <input class="no-hold" value={track.album} placeholder="Album" onchange={(event) => editField(track, 'album', (event.target as HTMLInputElement).value.trim())} onclick={(event) => event.stopPropagation()} />
        <input class="no-hold" value={track.year ?? ''} placeholder="Year" onchange={(event) => editField(track, 'year', (event.target as HTMLInputElement).value.trim())} onclick={(event) => event.stopPropagation()} />
        <input class="no-hold" value={track.title} placeholder="Title" onchange={(event) => editField(track, 'title', (event.target as HTMLInputElement).value.trim())} onclick={(event) => event.stopPropagation()} />
        <input class="no-hold" value={track.genre ?? ''} placeholder="Genre" onchange={(event) => editField(track, 'genre', (event.target as HTMLInputElement).value.trim())} onclick={(event) => event.stopPropagation()} />
        <span class="source-cell" title={cutoff ? `Spectrum stops at ${(cutoff / 1000).toFixed(1)} kHz: ${describeCutoff(cutoff)}` : 'Too little sound to tell'}>{shortCutoff(cutoff)}</span>
        <span class="fades">{track.fadeInMs || track.fadeOutMs ? `${track.fadeInMs}/${track.fadeOutMs} ms` : ''}</span>
        <span class="status">{#if track.verifyError}<button class="failed no-hold" title={`${track.verifyError}. Export it again.`} onclick={(event) => { event.stopPropagation(); exportTracks([track.id], true) }}>Failed check</button>{:else if track.skip}<button class="skipped no-hold" title={`${track.skipReason}. Click to use this take after all.`} onclick={(event) => { event.stopPropagation(); setSkip(track, false) }}>Skipped</button>{:else if track.skipReason}<button class="damaged no-hold" title={`${track.skipReason}. Click to leave it out of the export.`} onclick={(event) => { event.stopPropagation(); setSkip(track, true) }}>Damaged?</button>{:else if track.exported}<button class="exported no-hold" title={`${track.exportedPath}${track.verified ? ' · read back and checked' : ''}`} onclick={(event) => { event.stopPropagation(); sendToShell({ type: 'openFolder', path: track.exportedPath }) }}>Exported</button>{/if}</span>
      </div>
    {:else}
      <div class="none quiet">No tracks found. Place the playhead and press N to add one, or use Redetect.</div>
    {/each}
  </div>
  <Hint id="track-select" text="Tick tracks (or the box at the top for all) to tag, export, fade, merge or remove several at once; Ctrl/Shift+click and right-click work too. The small buttons on each row play that track's start or end." />
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

<style>
  .session { display: flex; flex-direction: column; height: 100%; min-height: 0; gap: 8px; }
  .empty { padding: 40px; }
  header { display: flex; align-items: baseline; gap: 12px; flex-wrap: wrap; }
  .name { font-size: 20px; font-weight: 600; background: none; border: 1px solid transparent; padding: 2px 6px; margin-left: -7px; min-width: 120px; width: 22ch; }
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
  .export-status { color: var(--fg-quiet); font-size: 12px; max-width: 320px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .wave-host { flex: 1 1 52%; min-height: 220px; position: relative; }
  .wave-mode { display: flex; align-items: center; gap: 0; }
  .wave-mode button { height: 22px; padding: 0 9px; font: inherit; font-size: 11.5px; font-weight: 600; cursor: pointer; background: var(--panel-solid); color: var(--fg-quiet); border: 1px solid var(--line); }
  .wave-mode button:first-child { border-radius: 6px 0 0 6px; } .wave-mode button:nth-child(2) { border-radius: 0 6px 6px 0; border-left: 0; }
  .wave-mode button.on { background: var(--accent-soft); color: var(--accent); }
  .wave-mode kbd { margin-left: 5px; }
  kbd.loose { margin: 0 6px 0 -2px; }
  .readout { display: flex; gap: 18px; font-family: var(--font-display); font-size: 12px; color: var(--fg-quiet); }
  .grabbed { color: var(--accent); font-family: var(--font-ui); }
  .key { margin-left: auto; display: flex; align-items: center; gap: 6px; font-family: var(--font-ui); font-size: 11.5px; white-space: nowrap; }
  .swatch { display: inline-block; width: 3px; height: 12px; margin-left: 8px; border-radius: 1px; }
  .swatch.start { background: var(--start); } .swatch.end { background: var(--end); }
  .swatch.change { width: 0; border-left: 2px dashed var(--warn); border-radius: 0; }
  .group-label { font-size: 10.5px; font-weight: 600; letter-spacing: 0.05em; text-transform: uppercase; color: var(--fg-quiet); margin-right: 2px; }
  .list { flex: 1 1 38%; min-height: 120px; overflow-y: auto; border-top: 1px solid var(--line); }
  .row { display: grid; grid-template-columns: 52px 150px 88px 82px 30px minmax(80px, 1fr) minmax(80px, 1fr) 56px minmax(120px, 1.5fr) minmax(60px, 0.6fr) 52px 70px 72px; gap: 8px; align-items: center; padding: 3px 8px; border-radius: 6px; cursor: default; }
  .row:not(.head):hover { background: var(--hover); }
  .row.current { box-shadow: inset 3px 0 0 var(--accent); }
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
  .row input { background: transparent; border-color: transparent; padding: 3px 6px; min-width: 0; }
  .row input:hover { border-color: var(--line); }
  .row input:focus { background: var(--panel); }
  .art-cell { width: 26px; height: 26px; padding: 0; border: 1px solid var(--line); border-radius: 4px; background: var(--panel); color: var(--fg-quiet); cursor: pointer; overflow: hidden; display: grid; place-items: center; font-size: 12px; }
  .art-cell img { width: 100%; height: 100%; object-fit: cover; }
  .art-cell:hover { border-color: var(--accent); }
  /* Hear start / end: one split control per row, Start | End */
  .hear { display: flex; gap: 0; }
  .hear button { display: inline-flex; align-items: center; justify-content: center; gap: 4px; height: 24px; padding: 0 9px; font: inherit; font-size: 11.5px; font-weight: 600; cursor: pointer; white-space: nowrap; background: var(--accent-soft); color: var(--accent); border: 1px solid color-mix(in srgb, var(--accent) 40%, transparent); }
  .hear .hear-start { border-radius: 7px 0 0 7px; } .hear .hear-end { border-radius: 0 7px 7px 0; border-left: 0; }
  .hear button:hover { background: var(--accent); color: var(--on-accent); }
  .hear button.checked { background: rgba(62, 207, 110, 0.18); color: #2e9e57; border-color: rgba(62, 207, 110, 0.5); }
  .edge-cell { display: flex; align-items: center; gap: 2px; min-width: 0; }
  .revert { width: 18px; height: 20px; padding: 0; flex: none; background: none; border: 0; border-radius: 4px; color: var(--warn); cursor: pointer; font-size: 13px; }
  .revert:hover { background: var(--hover); }
  .hear button.holding { animation: hold-fill 550ms linear forwards; }
  @keyframes hold-fill { from { box-shadow: inset 0 0 0 0 rgba(62, 207, 110, 0.85); } to { box-shadow: inset 90px 0 0 0 rgba(62, 207, 110, 0.85); } }
  .hear .hear-end.holding { animation-name: hold-fill-end; }
  @keyframes hold-fill-end { from { box-shadow: inset 0 0 0 0 rgba(62, 207, 110, 0.85); } to { box-shadow: inset -90px 0 0 0 rgba(62, 207, 110, 0.85); } }
  .hear .ok { width: 24px; padding: 0; margin: 0 4px; border-radius: 7px; background: var(--panel); color: var(--fg-quiet); border: 1px solid var(--line); font-weight: 700; }
  .hear .ok.on { background: #3ecf6e; color: #fff; border-color: transparent; }
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
