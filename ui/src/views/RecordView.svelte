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

  // The last minute of the recording, drawn from the peaks as they're written
  let liveCanvas = $state<HTMLCanvasElement>(), liveWidth = $state(600)
  let liveTimer = 0
  async function drawLive() {
    const status = app.status
    if (!liveCanvas || !status?.recording || !status.sampleRate) return
    const totalFrames = app.levels.seconds * status.sampleRate, span = 60 * status.sampleRate
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
      for (const change of status.changes) { const x = (change.frame - start) / span * liveWidth; if (x >= 0 && x <= liveWidth) context.fillRect(x, 0, 1.5, height) }
    } catch { }
  }
  $effect(() => { if (recording) { liveTimer = window.setInterval(drawLive, 300); return () => clearInterval(liveTimer) } })

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

  <h3>Now playing</h3>
  <p class="now">{#if current?.title}<strong>{current.title}</strong>{#if current.artist} · {current.artist}{/if}<span class="quiet"> · {current.label}</span>{:else}<span class="quiet">Nothing named yet. {source === 'none' ? 'Tracks will be split on gaps.' : 'Play something in a browser or app; gaps split tracks when there are no names.'}</span>{/if}</p>

  {#if recording}
    <canvas class="live" bind:this={liveCanvas} bind:clientWidth={liveWidth}></canvas>
    <h3>Tracks marked so far</h3>
    <ol class="changes">
      {#each app.status?.changes ?? [] as change}
        <li><span class="time">{formatTime(change.frame, app.status?.sampleRate ?? 48000, false)}</span>{change.title || (change.manual ? 'Marked by hand' : 'Untitled')}{#if change.artist}<span class="quiet"> · {change.artist}</span>{/if}</li>
      {:else}<li class="quiet">None yet.</li>{/each}
    </ol>
  {/if}
</div>

<style>
  .record { max-width: 860px; padding-bottom: 20px; }
  .settings-row { display: flex; gap: 8px; align-items: center; flex-wrap: wrap; }
  .device { margin: 8px 0 14px; font-size: 12.5px; }
  .deck { display: flex; align-items: center; gap: 18px; padding: 16px 18px; border-radius: 14px; background: var(--panel); border: 1px solid var(--line); }
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
  .now { margin: 0; }
  .live { display: block; width: 100%; height: 90px; margin-top: 14px; border-radius: 8px; background: var(--panel); }
  .changes { margin: 0; padding-left: 22px; max-height: 260px; overflow-y: auto; }
  .changes li { padding: 2px 0; }
  .time { display: inline-block; min-width: 64px; font-family: var(--font-display); font-size: 12px; color: var(--fg-quiet); }
</style>
