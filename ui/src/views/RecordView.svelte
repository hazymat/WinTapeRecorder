<script lang="ts">
  import { request, sendToShell } from '../bridge'
  import { app, nav, toast, formatTime, writeSetting } from '../state.svelte'
  import LevelMeter from '../parts/LevelMeter.svelte'
  import Dropdown from '../parts/Dropdown.svelte'
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
    else { sendToShell({ type: 'record', profileId, source, name: sessionName.trim() }); sessionName = '' }
  }
  async function stopRecording() {
    const result = await request<{ sessionId: string }>({ type: 'stopRecording' })
    if (result?.sessionId) { nav.view = 'session'; nav.sessionId = result.sessionId; writeSetting('session', result.sessionId); toast('Recording saved. Check the tracks, then export.') }
  }

  // The live waveform (SPEC.md › Recording › Live editing). Overview: the whole recording fitted to the width, with a 10 s gap at the
  // right that fills before the view rescales. Inspect: one marker pinned at the centre; drag the audio under it and let go to move it
  let liveCanvas = $state<HTMLCanvasElement>(), liveWidth = $state(600)
  const liveHeight = 110
  let liveTimer = 0
  let liveMode = $state<'overview' | 'inspect'>('overview')
  let secondsToWiden = $state(10)
  let inspect = $state<{ index: number; centre: number } | null>(null)
  let overviewSpan = 0 // frames shown; grows in steps of 10 s as the recording reaches the edge
  const inspectSeconds = 8
  function liveRange() {
    const status = app.status!; const rate = status.sampleRate, recorded = app.levels.seconds * rate
    if (liveMode === 'inspect' && inspect) { const span = inspectSeconds * rate; return { start: inspect.centre - span / 2, span } }
    if (recorded >= overviewSpan) overviewSpan = recorded + 10 * rate
    return { start: 0, span: overviewSpan }
  }
  let drawing = false
  async function drawLive() {
    const status = app.status
    if (!liveCanvas || !status?.recording || !status.sampleRate || drawing) return
    drawing = true
    try {
      const rate = status.sampleRate, recorded = app.levels.seconds * rate, { start, span } = liveRange()
      const data = await request<{ mode: string; channels: number[][] }>({ type: 'peaks', id: status.sessionId, start: Math.max(0, start), end: start + span, columns: Math.round(liveWidth) })
      if (!liveCanvas || data.mode !== 'peaks') return
      const ratio = devicePixelRatio, height = liveHeight, context = liveCanvas.getContext('2d')!
      if (liveCanvas.width !== Math.round(liveWidth * ratio)) { liveCanvas.width = Math.round(liveWidth * ratio); liveCanvas.height = height * ratio }
      context.setTransform(ratio, 0, 0, ratio, 0, 0); context.clearRect(0, 0, liveWidth, height)
      const style = getComputedStyle(document.documentElement), colour = (name: string, fallback: string) => style.getPropertyValue(name).trim() || fallback
      const toX = (frame: number) => (frame - start) / span * liveWidth
      // Inspect: the audio before the view's start (start < 0 at the very beginning) is simply blank
      const dataStart = Math.max(0, start), lanes = data.channels.length, laneHeight = height / lanes, columns = data.channels[0]?.length / 2 || 0, framesPerColumn = (start + span - dataStart) / columns
      context.fillStyle = colour('--wave', '#5ccfff')
      data.channels.forEach((values, lane) => { const middle = laneHeight * (lane + 0.5); for (let column = 0; column < columns; column++) { const at = dataStart + column * framesPerColumn; if (at > recorded) break; const x = toX(at), low = values[column * 2], high = values[column * 2 + 1]; context.fillRect(x, middle - high * (laneHeight / 2 - 1), Math.max(1, liveWidth / columns), Math.max(1, (high - low) * (laneHeight / 2 - 1))) } })
      // The gap still to fill, and where "now" is
      if (liveMode === 'overview') { context.fillStyle = colour('--panel-strong', 'rgba(255,255,255,0.1)'); context.fillRect(toX(recorded), 0, liveWidth - toX(recorded), height); secondsToWiden = Math.max(0, Math.round((span - recorded) / rate)) }
      // Markers: the one being inspected stays pinned at the centre
      context.font = '10.5px "Segoe UI Variable Text", "Segoe UI", sans-serif'; context.textBaseline = 'top'
      status.changes.forEach((change, index) => {
        const pinned = liveMode === 'inspect' && inspect?.index === index
        const x = pinned ? liveWidth / 2 : toX(change.frame); if (x < -2 || x > liveWidth + 2) return
        context.fillStyle = pinned ? colour('--accent', '#5ccfff') : colour('--warn', '#ffb74d')
        if (pinned) { context.globalAlpha = 0.18; context.fillRect(x - 8, 0, 16, height); context.globalAlpha = 1 }
        context.fillRect(x - 1, 0, pinned ? 3 : 2, height); context.fillText(`${index + 1} ${change.title || ''}`.trim(), x + 5, 3)
      })
      // The time scale
      context.fillStyle = colour('--fg-quiet', '#888'); context.textBaseline = 'bottom'; context.font = '10px "Segoe UI Variable Text", "Segoe UI", sans-serif'
      const stepSeconds = [1, 2, 5, 10, 30, 60, 120, 300, 600, 900, 1800].find(step => step * rate / span * liveWidth >= 70) ?? 1800
      for (let second = Math.ceil(Math.max(0, start) / rate / stepSeconds) * stepSeconds; second * rate < start + span; second += stepSeconds) { const x = toX(second * rate); context.fillRect(x, height - 10, 1, 4); context.fillText(formatTime(second * rate, rate, false), x + 3, height - 1) }
    } catch { } finally { drawing = false }
  }
  $effect(() => { if (recording) { liveTimer = window.setInterval(drawLive, 300); return () => clearInterval(liveTimer) } else { liveMode = 'overview'; inspect = null; overviewSpan = 0 } })
  function markerAt(x: number) { const status = app.status; if (!status?.sampleRate) return -1; const { start, span } = liveRange(); return status.changes.findIndex((change, index) => Math.abs((liveMode === 'inspect' && inspect?.index === index ? liveWidth / 2 : (change.frame - start) / span * liveWidth) - x) < 8) }
  function inspectMarker(index: number) { const change = app.status?.changes[index]; if (!change) return; liveMode = 'inspect'; inspect = { index, centre: change.frame }; drawLive() }
  function leaveInspect() { liveMode = 'overview'; inspect = null; drawLive() }
  // Inspect: dragging the audio moves it under the pinned marker; letting go places the marker there
  let dragging: null | { startX: number; startCentre: number } = null
  function onLiveDown(event: PointerEvent) {
    if (event.button !== 0) return
    if (liveMode === 'overview') { const index = markerAt(event.offsetX); if (index >= 0) inspectMarker(index); return }
    if (!inspect) return
    dragging = { startX: event.offsetX, startCentre: inspect.centre }; liveCanvas?.setPointerCapture(event.pointerId)
  }
  function onLiveMove(event: PointerEvent) {
    if (liveCanvas) liveCanvas.style.cursor = liveMode === 'inspect' ? (dragging ? 'grabbing' : 'grab') : markerAt(event.offsetX) >= 0 ? 'pointer' : 'default'
    if (!dragging || !inspect || !app.status) return
    const framesPerPixel = inspectSeconds * app.status.sampleRate / liveWidth
    inspect = { index: inspect.index, centre: Math.max(0, Math.min(app.levels.seconds * app.status.sampleRate, dragging.startCentre - (event.offsetX - dragging.startX) * framesPerPixel)) }
    drawLive()
  }
  function onLiveUp() {
    if (!dragging || !inspect) { dragging = null; return }
    dragging = null
    sendToShell({ type: 'editChange', index: inspect.index, frame: Math.round(inspect.centre) })
    setTimeout(leaveInspect, 400)
  }
  function onLiveKey(event: KeyboardEvent) { if (event.key === 'Escape' && liveMode === 'inspect') { event.preventDefault(); leaveInspect() } }

  function nudge(index: number, frame: number, seconds: number) { sendToShell({ type: 'editChange', index, frame: Math.max(0, frame + seconds * (app.status?.sampleRate ?? 48000)) }) }
  function setTime(index: number, text: string, rate: number) {
    const parts = text.trim().split(':').map(Number)
    if (parts.some(part => Number.isNaN(part))) return
    const seconds = parts.reduce((total, part) => total * 60 + part, 0)
    sendToShell({ type: 'editChange', index, frame: Math.round(seconds * rate) })
  }
  // Issues while recording (SPEC.md › Recording › Issues): each one names the track it hit and what to do about it
  type Issue = { kind: 'dropout' | 'clipping' | 'noname' | 'retake' | 'silence'; frame: number; index: number; text: string; action: string }
  const issues = $derived.by<Issue[]>(() => {
    const status = app.status; if (!status?.recording) return []
    const rate = status.sampleRate || 48000, recorded = app.levels.seconds * rate
    const trackAt = (frame: number) => { let found = -1; status.changes.forEach((change, index) => { if (change.frame <= frame) found = index }); return found }
    const name = (index: number) => { const change = status.changes[index]; return change?.title ? `“${change.title}”` : index >= 0 ? `track ${index + 1}` : 'the recording' }
    const list: Issue[] = []
    for (const dropout of status.dropoutList ?? []) { const index = trackAt(dropout.frame); list.push({ kind: 'dropout', frame: dropout.frame, index, text: `Dropout at ${formatTime(dropout.frame, rate, false)} in ${name(index)}: about ${dropout.ms} ms lost.`, action: 'Play that track again; the new take replaces this one.' }) }
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
</script>

<svelte:window onkeydown={onLiveKey} />
<div class="record">
  <h2>Record</h2>
  <Hint id="record-intro" text="Record a whole listening session into one file. Each time the playing track's name changes, a new track is marked; with no names, gaps split them. You fine-tune and export afterwards." />
  <div class="settings-row">
    <Dropdown label="Recording profile" value={profileId} options={profileChoices} align="left" onchange={(value) => { if (!recording) sendToShell({ type: 'setActiveProfile', kind: 'recording', profileId: value }) }} />
    <Dropdown label="Track names" value={source} options={sourceChoices} align="left" onchange={(value) => source = value} />
    <button class="ghost" onclick={() => sendToShell({ type: 'openSettings', tab: 'Recording' })}>Edit profiles…</button>
  </div>
  {#if recording && app.status?.dropouts}<p class="warn">{app.status.dropouts} dropout{app.status.dropouts === 1 ? '' : 's'}: the PC was too busy and samples were lost. They're marked in the session.</p>{/if}
  <p class="quiet device">{app.status?.device ? `From ${app.status.device}${profile?.loopback ? ' (what it plays)' : ''}` : 'No audio device open.'}{profile?.bitDepth ? ` · ${profile.bitDepth === 32 ? '32-bit float' : profile.bitDepth + '-bit'} WAV` : ''}</p>

  <div class="deck" class:live={recording}>
    <button class="record-button" class:on={recording} onclick={toggleRecording} title={recording ? 'Stop (Ctrl+R)' : 'Record (Ctrl+R)'} aria-label={recording ? 'Stop recording' : 'Start recording'}><Icon name={recording ? 'stop' : 'record'} size={34} /></button>
    <div class="deck-info">
      {#if recording}
        <div class="clock">{formatTime(app.levels.seconds * 1000, 1000, false)}</div>
        <div class="quiet">{app.status?.sessionName} · {sizeText} WAV{#if firstEstimate}{' · '}<span class="estimate" title={estimates.map(estimate => `${estimate.name}: ${estimate.text}`).join('\n')}>{firstEstimate.text} as {firstEstimate.name}{estimates.length > 1 ? ` (+${estimates.length - 1} more)` : ''}</span>{/if}{app.levels.minutesLeft >= 0 && app.levels.minutesLeft < 15 ? ` · WAV limit in ${Math.max(0, Math.floor(app.levels.minutesLeft))} min` : ''}</div>
      {:else}
        <input class="session-name" bind:value={sessionName} placeholder="Session name (optional)" onkeydown={(event) => { if (event.key === 'Enter') toggleRecording() }} />
        <div class="quiet">Ctrl+R starts and stops recording from anywhere in the app.</div>
      {/if}
    </div>
    {#if recording}<button class="ghost marker" onclick={() => sendToShell({ type: 'marker' })} title="Mark a new track here (M)"><Icon name="marker" size={16} />New track here<kbd>M</kbd></button>{/if}
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

  <h3>Source quality</h3>
  <p class="source">{#if app.status?.cutoffHz}<strong>Spectrum stops at {(app.status.cutoffHz / 1000).toFixed(1)} kHz</strong><span class="quiet">{' · '}{app.status.cutoffText}</span>{:else}<span class="quiet">{app.status?.capturing ? 'Measuring: needs a couple of seconds of sound.' : 'Measured from the sound as it arrives.'}</span>{/if}</p>
  <h3>Now playing</h3>
  <p class="now">{#if current?.title}<strong>{current.title}</strong>{#if current.artist}{' · '}{current.artist}{/if}<span class="quiet">{' · '}{current.label}</span>{:else}<span class="quiet">Nothing named yet. {source === 'none' ? 'Tracks will be split on gaps.' : 'Play something in a browser or app; gaps split tracks when there are no names.'}</span>{/if}</p>

  {#if recording}
    <div class="live-head">
      {#if liveMode === 'inspect'}<span><strong>Marker {(inspect?.index ?? 0) + 1}</strong><span class="quiet">{' · '}drag the audio under the line and let go to place it</span></span><button class="ghost" onclick={leaveInspect}>Back to whole recording<kbd>Esc</kbd></button>
      {:else}<span><strong>Whole recording</strong><span class="quiet">{' · '}click a marker, or its number below, to zoom in and move it</span></span><span class="quiet">{secondsToWiden} s until the view widens</span>{/if}
    </div>
    <canvas class="live-wave" bind:this={liveCanvas} bind:clientWidth={liveWidth} tabindex="-1" onpointerdown={onLiveDown} onpointermove={onLiveMove} onpointerup={onLiveUp} onpointercancel={onLiveUp} onkeydown={onLiveKey}></canvas>
    <h3>Tracks marked so far</h3>
    <Hint id="live-edit" text="Obvious slips can be fixed while the recording runs: retype a name, nudge a marker with the arrows or a typed time, or click its number to zoom in and drag the audio under the line. Fine listening comes afterwards, in Check the cuts." />
    <div class="changes">
      {#each app.status?.changes ?? [] as change, index (index + ':' + change.frame)}
        {@const rate = app.status?.sampleRate ?? 48000}
        <div class="change">
          <button class="number" class:on={liveMode === 'inspect' && inspect?.index === index} onclick={() => liveMode === 'inspect' && inspect?.index === index ? leaveInspect() : inspectMarker(index)} title="Zoom in on this marker and drag the audio under it">{index + 1}</button>
          <span class="nudge"><button onclick={() => nudge(index, change.frame, -5)} title="5 s earlier">«</button><button onclick={() => nudge(index, change.frame, -1)} title="1 s earlier">‹</button></span>
          <input class="time-box" value={formatTime(change.frame, rate, false)} onchange={(event) => setTime(index, (event.target as HTMLInputElement).value, rate)} title="Where this track starts (m:ss)" aria-label="Start time" />
          <span class="nudge"><button onclick={() => nudge(index, change.frame, 1)} title="1 s later">›</button><button onclick={() => nudge(index, change.frame, 5)} title="5 s later">»</button></span>
          <input value={change.artist} placeholder="Artist" onchange={(event) => sendToShell({ type: 'editChange', index, artist: (event.target as HTMLInputElement).value.trim() })} />
          <input value={change.title} placeholder={change.manual ? 'Title (marked by hand)' : 'Title'} onchange={(event) => sendToShell({ type: 'editChange', index, title: (event.target as HTMLInputElement).value.trim() })} />
          <input value={change.album} placeholder="Album" onchange={(event) => sendToShell({ type: 'editChange', index, album: (event.target as HTMLInputElement).value.trim() })} />
          <button class="retake" class:on={!!change.problem} onclick={() => sendToShell({ type: 'setProblem', index, problem: change.problem ? '' : 'retake' })} title={change.problem ? `${change.problem === 'dropout' ? 'Dropout' : change.problem === 'clipping' ? 'Clipping' : 'Marked for a retake'}: a later take of the same track replaces this one. Click to keep this take.` : 'Something went wrong with this take: mark it for a retake, then play the track again'}>{change.problem ? 'Retake' : '↻'}</button>
          <button class="remove" onclick={() => sendToShell({ type: 'removeChange', index })} title="Remove this marker (the audio stays)" aria-label="Remove marker">✕</button>
        </div>
      {:else}<p class="quiet">None yet. Press M to mark where a track starts.</p>{/each}
    </div>
  {/if}
</div>

<style>
  .record { max-width: 860px; padding-bottom: 20px; }
  .settings-row { display: flex; gap: 8px; align-items: center; flex-wrap: wrap; }
  .device { margin: 8px 0 14px; font-size: 12.5px; }
  .deck { display: flex; flex-wrap: wrap; align-items: center; gap: 18px; padding: 16px 18px; border-radius: 14px; background: var(--panel); border: 1px solid var(--line); }
  .deck.live { border-color: color-mix(in srgb, var(--danger) 55%, var(--line)); }
  .record-button { flex: none; width: 72px; height: 72px; border-radius: 50%; display: grid; place-items: center; border: 2px solid color-mix(in srgb, var(--danger) 60%, transparent); background: var(--panel-strong); color: var(--danger); cursor: pointer; transition: transform 120ms, background 120ms; }
  .record-button:hover { transform: scale(1.04); }
  .record-button.on { background: var(--danger); color: #fff; animation: pulse 1.6s ease-in-out infinite; }
  @keyframes pulse { 0%, 100% { box-shadow: 0 0 0 0 color-mix(in srgb, var(--danger) 60%, transparent); } 60% { box-shadow: 0 0 0 12px color-mix(in srgb, var(--danger) 0%, transparent); } }
  .deck-info { flex: 1; min-width: 0; display: flex; flex-direction: column; gap: 4px; }
  .clock { font-family: var(--font-display); font-size: 34px; color: var(--readout); text-shadow: 0 0 10px var(--readout-glow); letter-spacing: 0.04em; }
  .session-name { font-size: 16px; padding: 6px 10px; max-width: 360px; }
  .marker { flex: none; }
  .warn { color: var(--danger); font-size: 12.5px; margin: 8px 0 0; }
  .now, .source { margin: 0; }
  .estimate { border-bottom: 1px dotted var(--fg-quiet); cursor: help; }
  .live-wave { display: block; width: 100%; height: 110px; border-radius: 8px; background: var(--panel); outline: none; }
  .live-head { display: flex; align-items: center; justify-content: space-between; gap: 10px; margin: 14px 0 4px; font-size: 12px; }
  .changes { display: flex; flex-direction: column; gap: 4px; max-height: 300px; overflow-y: auto; }
  .change { display: grid; grid-template-columns: 30px auto 76px auto minmax(90px, 1fr) minmax(120px, 1.4fr) minmax(90px, 1fr) auto 28px; gap: 6px; align-items: center; }
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
