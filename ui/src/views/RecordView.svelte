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

  // The recording so far (the last minute once longer), drawn from the peaks as they are written
  let liveCanvas = $state<HTMLCanvasElement>(), liveWidth = $state(600)
  let liveTimer = 0
  async function drawLive() {
    const status = app.status
    if (!liveCanvas || !status?.recording || !status.sampleRate) return
    const totalFrames = app.levels.seconds * status.sampleRate, span = Math.min(60, Math.max(10, app.levels.seconds)) * status.sampleRate // fills the width from the start, then scrolls the last minute
    const start = Math.max(0, totalFrames - span)
    try {
      const data = await request<{ mode: string; channels: number[][] }>({ type: 'peaks', id: status.sessionId, start, end: start + span, columns: Math.round(liveWidth) })
      if (!liveCanvas || data.mode !== 'peaks') return
      const ratio = devicePixelRatio, height = 90, context = liveCanvas.getContext('2d')!
      liveCanvas.width = Math.round(liveWidth * ratio); liveCanvas.height = height * ratio; context.setTransform(ratio, 0, 0, ratio, 0, 0)
      context.clearRect(0, 0, liveWidth, height)
      context.fillStyle = getComputedStyle(document.documentElement).getPropertyValue('--wave').trim() || '#5ccfff'
      const lanes = data.channels.length, laneHeight = height / lanes
      data.channels.forEach((values, lane) => { const middle = laneHeight * (lane + 0.5); for (let column = 0; column < values.length / 2; column++) { const low = values[column * 2], high = values[column * 2 + 1]; if (column * span / (values.length / 2) + start > totalFrames) break; context.fillRect(column, middle - high * (laneHeight / 2 - 1), 1, Math.max(1, (high - low) * (laneHeight / 2 - 1))) } })
      const marks = getComputedStyle(document.documentElement).getPropertyValue('--warn').trim() || '#ffb74d'
      context.fillStyle = marks
      context.font = '10.5px "Segoe UI Variable Text", "Segoe UI", sans-serif'; context.textBaseline = 'top'
      status.changes.forEach((change, index) => { const x = (change.frame - start) / span * liveWidth; if (x < 0 || x > liveWidth) return; context.fillRect(x - 1, 0, 2, height); context.fillText(`${index + 1} ${change.title || ''}`.trim(), x + 4, 3) })
    } catch { }
  }
  $effect(() => { if (recording) { liveTimer = window.setInterval(drawLive, 300); return () => clearInterval(liveTimer) } })

  function nudge(index: number, frame: number, seconds: number) { sendToShell({ type: 'editChange', index, frame: Math.max(0, frame + seconds * (app.status?.sampleRate ?? 48000)) }) }
  function setTime(index: number, text: string, rate: number) {
    const parts = text.trim().split(':').map(Number)
    if (parts.some(part => Number.isNaN(part))) return
    const seconds = parts.reduce((total, part) => total * 60 + part, 0)
    sendToShell({ type: 'editChange', index, frame: Math.round(seconds * rate) })
  }
  // Dragging a marker on the live waveform
  let dragging: null | { index: number } = null
  function markerAt(x: number) { const status = app.status; if (!status?.sampleRate) return -1; const { start, span } = liveRange(); return status.changes.findIndex(change => Math.abs((change.frame - start) / span * liveWidth - x) < 7) }
  function liveRange() { const status = app.status!; const totalFrames = app.levels.seconds * status.sampleRate, span = Math.min(60, Math.max(10, app.levels.seconds)) * status.sampleRate; return { start: Math.max(0, totalFrames - span), span } }
  function onLiveDown(event: PointerEvent) { const index = markerAt(event.offsetX); if (index < 0) return; dragging = { index }; liveCanvas?.setPointerCapture(event.pointerId) }
  function onLiveMove(event: PointerEvent) { if (liveCanvas) liveCanvas.style.cursor = dragging || markerAt(event.offsetX) >= 0 ? 'ew-resize' : 'default'; if (!dragging) return; const { start, span } = liveRange(); sendToShell({ type: 'editChange', index: dragging.index, frame: Math.round(start + event.offsetX / liveWidth * span) }) }
  function onLiveUp() { dragging = null }
  const sizeText = $derived(app.levels.bytes > 1e9 ? `${(app.levels.bytes / 1e9).toFixed(2)} GB` : `${(app.levels.bytes / 1e6).toFixed(0)} MB`)
  const current = $derived(app.status?.current)
</script>

<div class="record">
  <h2>Record</h2>
  <Hint id="record-intro" text="Record a whole listening session into one file. Each time the playing track's name changes, a new track is marked; with no names, gaps split them. You fine-tune and export afterwards." />
  <div class="settings-row">
    <Dropdown label="Profile" value={profileId} options={profileChoices} align="left" onchange={(value) => { if (!recording) sendToShell({ type: 'setActiveProfile', kind: 'recording', profileId: value }) }} />
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
        <div class="quiet">{app.status?.sessionName} · {sizeText}{app.levels.minutesLeft >= 0 && app.levels.minutesLeft < 15 ? ` · WAV limit in ${Math.max(0, Math.floor(app.levels.minutesLeft))} min` : ''}</div>
      {:else}
        <input class="session-name" bind:value={sessionName} placeholder="Session name (optional)" onkeydown={(event) => { if (event.key === 'Enter') toggleRecording() }} />
        <div class="quiet">Ctrl+R starts and stops recording from anywhere in the app.</div>
      {/if}
    </div>
    {#if recording}<button class="ghost marker" onclick={() => sendToShell({ type: 'marker' })} title="Mark a new track here (M)"><Icon name="marker" size={16} />New track here<kbd>M</kbd></button>{/if}
  </div>

  <h3>Input level</h3>
  <LevelMeter />
  {#if app.levels.clipped > 0}<p class="warn">The input reached full scale. Turn the source down a little to avoid clipping.</p>{/if}

  <h3>Source quality</h3>
  <p class="source">{#if app.status?.cutoffHz}<strong>Spectrum stops at {(app.status.cutoffHz / 1000).toFixed(1)} kHz</strong><span class="quiet">{' · '}{app.status.cutoffText}</span>{:else}<span class="quiet">{app.status?.capturing ? 'Measuring: needs a couple of seconds of sound.' : 'Measured from the sound as it arrives.'}</span>{/if}</p>
  <h3>Now playing</h3>
  <p class="now">{#if current?.title}<strong>{current.title}</strong>{#if current.artist}{' · '}{current.artist}{/if}<span class="quiet">{' · '}{current.label}</span>{:else}<span class="quiet">Nothing named yet. {source === 'none' ? 'Tracks will be split on gaps.' : 'Play something in a browser or app; gaps split tracks when there are no names.'}</span>{/if}</p>

  {#if recording}
    <canvas class="live-wave" bind:this={liveCanvas} bind:clientWidth={liveWidth} onpointerdown={onLiveDown} onpointermove={onLiveMove} onpointerup={onLiveUp}></canvas>
    <h3>Tracks marked so far</h3>
    <Hint id="live-edit" text="Everything here can be fixed while the recording runs: retype a name, nudge a marker with the arrows, type a time, or drag the orange line on the waveform above. The audio is untouched." />
    <div class="changes">
      {#each app.status?.changes ?? [] as change, index (index + ':' + change.frame)}
        {@const rate = app.status?.sampleRate ?? 48000}
        <div class="change">
          <span class="number">{index + 1}</span>
          <span class="nudge"><button onclick={() => nudge(index, change.frame, -5)} title="5 s earlier">«</button><button onclick={() => nudge(index, change.frame, -1)} title="1 s earlier">‹</button></span>
          <input class="time-box" value={formatTime(change.frame, rate, false)} onchange={(event) => setTime(index, (event.target as HTMLInputElement).value, rate)} title="Where this track starts (m:ss)" aria-label="Start time" />
          <span class="nudge"><button onclick={() => nudge(index, change.frame, 1)} title="1 s later">›</button><button onclick={() => nudge(index, change.frame, 5)} title="5 s later">»</button></span>
          <input value={change.artist} placeholder="Artist" onchange={(event) => sendToShell({ type: 'editChange', index, artist: (event.target as HTMLInputElement).value.trim() })} />
          <input value={change.title} placeholder={change.manual ? 'Title (marked by hand)' : 'Title'} onchange={(event) => sendToShell({ type: 'editChange', index, title: (event.target as HTMLInputElement).value.trim() })} />
          <input value={change.album} placeholder="Album" onchange={(event) => sendToShell({ type: 'editChange', index, album: (event.target as HTMLInputElement).value.trim() })} />
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
  .live-wave { display: block; width: 100%; height: 90px; margin-top: 14px; border-radius: 8px; background: var(--panel); }
  .changes { display: flex; flex-direction: column; gap: 4px; max-height: 300px; overflow-y: auto; }
  .change { display: grid; grid-template-columns: 24px auto 76px auto minmax(90px, 1fr) minmax(120px, 1.4fr) minmax(90px, 1fr) 28px; gap: 6px; align-items: center; }
  .number { font-family: var(--font-display); font-size: 12px; color: var(--fg-quiet); text-align: right; }
  .nudge { display: flex; gap: 2px; }
  .nudge button { width: 24px; height: 26px; padding: 0; background: var(--panel); border: 1px solid var(--line); border-radius: 5px; color: var(--fg); font: inherit; cursor: pointer; }
  .nudge button:hover { background: var(--hover); }
  .time-box { font-family: var(--font-display); font-size: 12px; text-align: center; padding: 4px 4px; }
  .remove { width: 26px; height: 26px; padding: 0; background: none; border: 0; border-radius: 5px; color: var(--fg-quiet); cursor: pointer; }
  .remove:hover { background: var(--hover); color: var(--danger); }
  .live-wave { cursor: default; }
</style>
