<script lang="ts">
  import { sendToShell, onShellMessage, request } from './bridge'
  import { app, nav, ui, toast, writeSetting, readSetting, formatDuration, type SessionSummary, tour } from './state.svelte'
  import { selection, clearSelection } from './lib/selection.svelte'
  import RecordView from './views/RecordView.svelte'
  import SessionView from './views/SessionView.svelte'
  import ActionStrip from './parts/ActionStrip.svelte'
  import WhatsNew from './parts/WhatsNew.svelte'
  import ResizeEdges from './parts/ResizeEdges.svelte'
  import Icon from './parts/Icon.svelte'
  import Tour from './parts/Tour.svelte'
  import Dialog from './parts/Dialog.svelte'

  // ---- From the shell ----
  onShellMessage('status', message => app.status = message as any)
  onShellMessage('levels', message => app.levels = { peaks: message.peaks, clipped: message.clipped, seconds: message.seconds, bytes: message.bytes, minutesLeft: message.minutesLeft, silentSeconds: message.silentSeconds ?? 0, silenceStopIn: message.silenceStopIn ?? -1 })
  onShellMessage('playhead', message => app.playhead = message.frame)
  onShellMessage('sources', message => app.sources = message.sources)
  onShellMessage('settings', message => { app.hints = message.hints; app.recordingProfiles = message.recordingProfiles; app.outputProfiles = message.outputProfiles; app.activeRecording = message.activeRecording; app.activeOutput = message.activeOutput; app.auditionSeconds = message.auditionSeconds })
  onShellMessage('sessionsChanged', () => loadSessions())
  // An export keeps running while the next session is recorded: its progress sits on the session in the sidebar, and the end is announced wherever you are
  onShellMessage('exportDone', message => { loadSessions(); toast(message.error ? `Export finished with problems: ${message.done} of ${message.total} written, ${message.verified} verified. ${String(message.error).split('\n')[0]}` : `Export finished: ${message.done} track${message.done === 1 ? '' : 's'} written and verified`, { ms: message.error ? 15000 : 6000 }) })
  onShellMessage('recordingStopped', message => { loadSessions(); clearSelection(); nav.view = 'session'; nav.sessionId = message.sessionId; writeSetting('session', message.sessionId); toast(message.reason, { ms: 15000 }) })
  onShellMessage('tip', message => toast(`${message.title}: ${message.text}`, { ms: 10000 }))
  onShellMessage('window', message => app.window = { glass: !!message.glass, frameless: !!message.frameless, maximised: !!message.maximised })
  onShellMessage('skin', message => {
    ui.skin = message.skin || 'auto'; ui.accent = message.accent ?? ''; ui.glassTransparency = message.glassTransparency ?? -1; ui.glassTintHue = message.glassTintHue ?? 0; ui.lookVersion++
    writeSetting('skin', ui.skin); writeSetting('accent', ui.accent); writeSetting('glassTransparency', String(ui.glassTransparency)); writeSetting('glassTintHue', String(ui.glassTintHue))
  })
  sendToShell({ type: 'ready' })

  async function loadSessions() { try { app.sessions = await request<SessionSummary[]>({ type: 'sessions' }) } catch { } }
  loadSessions()
  // A session that disappeared (closed, or its folder moved) falls back to the recorder
  $effect(() => { if (nav.view === 'session' && app.sessions.length && !app.sessions.some(session => session.id === nav.sessionId)) { nav.view = 'record'; nav.sessionId = '' } })
  if (nav.sessionId) nav.view = 'session'

  function openSession(session: SessionSummary) {
    if (session.recording) { nav.view = 'record'; return }
    clearSelection(); nav.view = 'session'; nav.sessionId = session.id; writeSetting('session', session.id)
  }

  // ---- Look (from WinSqueeze): skin, accent, glass tint ----
  const prefersDark = window.matchMedia('(prefers-color-scheme: dark)')
  let systemDark = $state(prefersDark.matches)
  prefersDark.addEventListener('change', () => systemDark = prefersDark.matches)
  $effect(() => { document.documentElement.dataset.skin = ui.skin; sendToShell({ type: 'theme', dark: ui.skin === 'auto' ? systemDark : ui.skin !== 'light' }) })
  $effect(() => {
    const rootStyle = document.documentElement.style, accent = ui.accent
    if (!accent) { for (const name of ['--accent', '--accent-soft', '--on-accent']) rootStyle.removeProperty(name); return }
    const channels = [1, 3, 5].map(start => parseInt(accent.slice(start, start + 2), 16))
    rootStyle.setProperty('--accent', `rgb(${channels.join(', ')})`); rootStyle.setProperty('--accent-soft', `rgba(${channels.join(', ')}, 0.15)`)
    rootStyle.setProperty('--on-accent', 0.2126 * channels[0] + 0.7152 * channels[1] + 0.0722 * channels[2] > 150 ? '#06141c' : '#ffffff')
  })
  $effect(() => {
    const rootStyle = document.documentElement.style, light = ui.skin === 'light' || (ui.skin === 'auto' && !systemDark)
    ui.lookVersion
    const defaultTint = ui.glassTransparency < 0 && !ui.glassTintHue
    const opacity = ui.glassTransparency < 0 ? (light ? 0.42 : 0.38) : 1 - ui.glassTransparency / 100
    const colour = ui.glassTintHue ? (light ? `hsl(${ui.glassTintHue} 45% 92%` : `hsl(${ui.glassTintHue} 40% 12%`) : (light ? 'rgb(242 245 250' : 'rgb(12 14 19')
    // A nudge then the real value next frame forces a fresh paint of the glass
    rootStyle.setProperty('--glass-tint', `${colour} / ${(opacity + 0.004).toFixed(3)})`)
    requestAnimationFrame(() => { if (defaultTint) { rootStyle.removeProperty('--glass-tint'); return } rootStyle.setProperty('--glass-tint', `${colour} / ${opacity.toFixed(2)})`) })
  })
  $effect(() => { ui.skin; ui.glassTransparency; ui.glassTintHue; ui.lookVersion; systemDark; app.window.glass; app.window.frameless; requestAnimationFrame(() => sendToShell({ type: 'edgeColour', colour: getComputedStyle(document.body).backgroundColor })) })
  $effect(() => { document.documentElement.classList.toggle('glass', app.window.glass); document.documentElement.classList.toggle('frameless', app.window.frameless); document.documentElement.classList.toggle('maximised', app.window.maximised) })

  // Frameless: drag the window from empty parts of the top strip; double-click maximises
  function dragWindow(event: MouseEvent) {
    if (!app.window.frameless || event.button !== 0 || (event.target as HTMLElement).closest('button, input, select, a, .no-drag')) return
    if (event.detail === 2) { sendToShell({ type: 'windowControl', action: 'maximise' }); return }
    sendToShell({ type: 'dragWindow' })
  }

  // ---- Keys that work everywhere ----
  async function toggleRecording() {
    if (app.status?.recording) {
      const result = await request<{ sessionId: string }>({ type: 'stopRecording' })
      if (result?.sessionId) { nav.view = 'session'; nav.sessionId = result.sessionId; writeSetting('session', result.sessionId); toast('Recording saved. Check the tracks, then export.') }
    }
    else { nav.view = 'record'; sendToShell({ type: 'record', profileId: app.activeRecording, source: 'auto', name: '' }) }
  }
  function onKey(event: KeyboardEvent) {
    const typing = (event.target as HTMLElement)?.matches?.('input, textarea, select')
    if (event.ctrlKey && event.key.toLowerCase() === 'r') { event.preventDefault(); toggleRecording() }
    else if (!typing && !event.ctrlKey && !event.altKey && event.key.toLowerCase() === 'm' && app.status?.recording) { event.preventDefault(); sendToShell({ type: 'marker' }) }
    else if (!typing && !event.ctrlKey && ui.toast?.action && event.key.toUpperCase() === ui.toast.action.key) { event.preventDefault(); const action = ui.toast.action; ui.toast = null; action.run() }
    else if (event.key === 'Escape' && ui.toast && !selection.keys.length) ui.toast = null
  }

  // Right-click a session: its files in Explorer, or close it
  // First run: a short welcome pointing at the tour; shown once
  let welcome = $state(readSetting('welcomeSeen', '') !== '1')
  function closeWelcome(start: boolean) { welcome = false; writeSetting('welcomeSeen', '1'); if (start) { tour.open = true; tour.tab = 'concept' } }
  let sessionMenu = $state<null | { session: SessionSummary; left: number; top: number }>(null)
  // Renaming in the list itself: Enter or clicking away keeps the name, Esc leaves it as it was
  let renaming = $state('')
  function rename(session: SessionSummary, name: string) { renaming = ''; if (name.trim() && name.trim() !== session.name) sendToShell({ type: 'renameSession', id: session.id, name: name.trim() }) }
  function focusSelect(node: HTMLInputElement) { node.focus(); node.select() }
  let closing = $state<SessionSummary | null>(null)
  let closingAt = $state<{ left: number; top: number } | null>(null) // set: the confirmation drops down there instead of a dialog in the middle
  function askClose(session: SessionSummary, event: Event) { const box = ((event.currentTarget as HTMLElement).closest('.session') ?? event.currentTarget as HTMLElement).getBoundingClientRect(); closingAt = { left: box.left + 8, top: Math.min(box.bottom + 2, innerHeight - 150) }; closing = session }
  async function closeSession(session: SessionSummary) {
    closing = null
    try { await request({ type: 'deleteSession', id: session.id }); if (nav.sessionId === session.id) { nav.view = 'record'; nav.sessionId = ''; writeSetting('session', '') } toast(`Closed “${session.name}” and deleted its recording`) }
    catch (error) { toast('Could not close the session: ' + (error as Error).message) }
  }
  const devVersion = $derived(app.status?.version && !/^\d+\.\d+\.0$/.test(app.status.version) ? app.status.version : '')
  const dateLabel = (created: string) => new Date(created).toLocaleString(undefined, { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' })
</script>

<svelte:window onkeydown={(event) => { if (event.key === 'Escape' && closingAt) { closing = null; closingAt = null; return } onKey(event) }} onpointerdown={(event) => { if (sessionMenu && !(event.target as HTMLElement).closest('.session-menu')) sessionMenu = null; if (closingAt && !(event.target as HTMLElement).closest('.close-drop, .session-tool, .session-menu')) { closing = null; closingAt = null } }} />
{#if sessionMenu}
  {@const menuSession = sessionMenu.session}
  <div class="session-menu" role="menu" style:left="{sessionMenu.left}px" style:top="{sessionMenu.top}px">
    <button role="menuitem" onclick={() => { sendToShell({ type: 'openFolder', path: menuSession.id + '\\audio.wav' }); sessionMenu = null }}><Icon name="folder" size={15} />Show session files</button>
    <button role="menuitem" onclick={() => { renaming = menuSession.id; sessionMenu = null }}><Icon name="edit" size={15} />Rename</button>
    <button role="menuitem" disabled={menuSession.recording} onclick={() => { const target = menuSession; sessionMenu = null; openSession(target) }}><Icon name="play" size={15} />Open</button>
    <button role="menuitem" class="danger" disabled={menuSession.recording} onclick={(event) => { const target = menuSession; const at = { left: sessionMenu?.left ?? 20, top: sessionMenu?.top ?? 100 }; sessionMenu = null; closingAt = at; closing = target }}><Icon name="trash" size={15} />Close session…</button>
  </div>
{/if}
{#if closing}
  {@const closingSession = closing}
  {#if closingAt}
    <div class="close-drop" role="dialog" aria-label="Close this session?" style:left="{closingAt.left}px" style:top="{closingAt.top}px">
      <strong>Delete this session's recording?</strong>
      <span class="quiet">{closingSession.tracks - closingSession.exported > 0 ? `${closingSession.tracks - closingSession.exported} of ${closingSession.tracks} tracks aren't exported yet. ` : ''}Exported files are kept.</span>
      <span class="close-buttons"><button class="danger-fill" onclick={() => { closingAt = null; closeSession(closingSession) }}>Delete</button><button class="ghost" onclick={() => { closing = null; closingAt = null }}>Keep</button></span>
    </div>
  {:else}
  <Dialog title="Close “{closingSession.name}”?" text={`This deletes the session's recording (the WAV) for good.${closingSession.tracks - closingSession.exported > 0 ? ` ${closingSession.tracks - closingSession.exported} of ${closingSession.tracks} tracks haven't been exported yet.` : ''} Exported files are kept.`} confirmLabel="Delete recording" danger onconfirm={() => closeSession(closingSession)} oncancel={() => closing = null} />
  {/if}
{/if}
<WhatsNew />
{#if app.window.frameless && !app.window.maximised}<ResizeEdges />{/if}

<div class="shell">
  <nav class="sidebar">
    <div class="brand" role="presentation" onmousedown={dragWindow}>WinTapeRecorder{#if devVersion}<span class="dev-version">{devVersion}</span>{/if}</div>
    <button class="section" class:on={nav.view === 'record'} onclick={() => { clearSelection(); nav.view = 'record' }}><span class="glyph" class:recording={app.status?.recording}><Icon name="record" size={16} /></span>{app.status?.recording ? 'Recording…' : 'Record'}</button>
    <div class="heading">Sessions</div>
    <div class="sessions">
      {#each app.sessions as session (session.id)}
        {#if renaming === session.id}
          <div class="session on renaming"><input class="rename-field" value={session.name} use:focusSelect aria-label="Session name" onkeydown={(event) => { if (event.key === 'Enter') rename(session, (event.target as HTMLInputElement).value); else if (event.key === 'Escape') { event.stopPropagation(); renaming = '' } }} onblur={(event) => { if (renaming === session.id) rename(session, (event.target as HTMLInputElement).value) }} /></div>
        {:else}
        <button class="session" class:on={nav.view === 'session' && nav.sessionId === session.id} onclick={() => openSession(session)} oncontextmenu={(event) => { event.preventDefault(); sessionMenu = { session, left: Math.min(event.clientX, innerWidth - 220), top: Math.min(event.clientY, innerHeight - 110) } }} title={session.crashed ? 'This recording was cut short (crash or power cut). It opens with what was saved.' : ''}>
          <span class="session-name">{session.name}{#if session.recording}<span class="badge live">REC</span>{:else if session.crashed}<span class="badge">!</span>{/if}</span>
          <span class="session-more" role="button" tabindex="-1" title="Rename, show files, open or close this session (also on right-click)" onclick={(event) => { event.stopPropagation(); const box = (event.currentTarget as HTMLElement).getBoundingClientRect(); sessionMenu = { session, left: Math.min(box.left, innerWidth - 220), top: Math.min(box.bottom + 4, innerHeight - 110) } }} onkeydown={() => { }}>⋯</span>
          <span class="session-facts">{dateLabel(session.created)} · {formatDuration(session.seconds)} · {#if app.status?.exportingSession === session.id}<span class="exporting">exporting {app.status.exportDone}/{app.status.exportTotal}…</span>{:else if app.status?.exportQueue?.includes(session.id)}<span class="exporting">export queued</span>{:else}{session.exported}/{session.tracks} exported{session.exported && session.verified === session.exported ? ' ✓' : ''}{/if}</span>
          {#if nav.view === 'session' && nav.sessionId === session.id && !session.recording}<span class="session-tools"><span class="session-tool" role="button" tabindex="-1" title="Show this session's files in Explorer" onclick={(event) => { event.stopPropagation(); sendToShell({ type: 'openFolder', path: session.id + '\audio.wav' }) }} onkeydown={() => { }}><Icon name="folder" size={14} />Files</span><span class="session-tool danger" role="button" tabindex="-1" title="Close this session (deletes its recording; exported files stay)" onclick={(event) => { event.stopPropagation(); askClose(session, event) }} onkeydown={() => { }}><Icon name="trash" size={14} />Close…</span></span>{/if}
        </button>
        {/if}
      {:else}
        <p class="no-sessions">Recordings you make appear here until you close them.</p>
      {/each}
    </div>
    <div class="spacer"></div>
    <button class="section tour-button" class:on={tour.open} onclick={() => { tour.open = !tour.open; welcome = false; writeSetting('welcomeSeen', '1') }} title="A guided tour of the screen you're on (safe while recording)"><span class="glyph"><Icon name="tour" size={18} /></span>Tour</button>
    <button class="section" onclick={() => sendToShell({ type: 'openSettings' })}><span class="glyph"><Icon name="settings" size={18} /></span>Settings</button>
  </nav>
  <div class="content-column">
    {#if app.status?.updateReady}<div class="update-notice"><span>WinTapeRecorder {app.status.updateReady} is ready.</span><button class="primary" onclick={() => sendToShell({ type: 'applyUpdate' })}>Restart to update</button></div>{/if}
    <header class="topbar" role="presentation" onmousedown={dragWindow}>
      <div class="title"></div>
      {#if app.window.frameless}
        <div class="window-controls">
          <button onclick={() => sendToShell({ type: 'windowControl', action: 'minimise' })} aria-label="Minimise"><Icon name="minimise" size={16} /></button>
          <button onclick={() => sendToShell({ type: 'windowControl', action: 'maximise' })} aria-label={app.window.maximised ? 'Restore' : 'Maximise'}><Icon name={app.window.maximised ? 'restore' : 'maximise'} size={16} /></button>
          <button class="close" onclick={() => sendToShell({ type: 'windowControl', action: 'close' })} aria-label="Close"><Icon name="close" size={16} /></button>
        </div>
      {/if}
    </header>
    <main class="content">
      {#if nav.view === 'session' && nav.sessionId}
        {#key nav.sessionId}<SessionView sessionId={nav.sessionId} />{/key}
      {:else}
        <RecordView />
      {/if}
    </main>
    <ActionStrip />
  </div>
</div>

<Tour />
{#if welcome}
  <div class="welcome" role="dialog" aria-label="Welcome">
    <h3>Welcome to WinTapeRecorder</h3>
    <p>If you're anything like me, you're a little impatient to try this out and see if it fits your needs. When you're ready, look for <strong>Tour</strong> on the left. My humble suggestion:</p>
    <ol>
      <li>Start playing music from another app, then check here that the level meter moves and the track info comes in. Yes? Stop the music, choose <strong>Start on sound</strong>, press record, then play from the beginning.</li>
      <li>Watch the magic when tracks change.</li>
      <li>Open the <strong>Tour</strong>, which you can safely do while recording!</li>
    </ol>
    <div class="welcome-buttons"><button class="primary" onclick={() => closeWelcome(true)}>Start the tour now</button><button class="ghost" onclick={() => closeWelcome(false)}>Let me try first</button></div>
  </div>
{/if}

<style>
  .shell { display: grid; grid-template-columns: 240px minmax(0, 1fr); height: 100vh; }
  .sidebar { display: flex; flex-direction: column; gap: 2px; padding: 14px 10px; background: var(--glass); backdrop-filter: blur(var(--blur)) saturate(1.4); border-right: 1px solid var(--line); min-height: 0; }
  .brand { font-family: var(--font-display); color: var(--readout); text-shadow: 0 0 8px var(--readout-glow); letter-spacing: 0.06em; padding: 4px 10px 14px; font-size: 13px; }
  .dev-version { display: inline-block; margin-left: 8px; font-family: var(--font-ui); font-size: 10px; letter-spacing: 0; color: var(--fg-quiet); text-shadow: none; border: 1px solid var(--line); border-radius: 6px; padding: 0 5px; vertical-align: middle; }
  .section { display: flex; align-items: center; gap: 10px; background: none; border: 0; color: var(--fg); padding: 8px 10px; border-radius: 8px; cursor: pointer; font: inherit; text-align: left; }
  .section:hover, .session:hover { background: var(--hover); }
  .section.on, .session.on { background: var(--accent-soft); color: var(--accent); }
  .glyph { width: 20px; display: grid; place-items: center; color: var(--danger); }
  .section:last-child .glyph { color: inherit; }
  .glyph.recording { animation: blink 1.2s steps(2, start) infinite; }
  @keyframes blink { to { visibility: hidden; } }
  .heading { margin: 16px 10px 4px; font-size: 11px; font-weight: 600; letter-spacing: 0.06em; text-transform: uppercase; color: var(--fg-quiet); }
  .sessions { display: flex; flex-direction: column; gap: 2px; overflow-y: auto; min-height: 0; }
  .session { position: relative; }
  .close-drop { position: fixed; z-index: 130; width: 220px; display: flex; flex-direction: column; gap: 6px; padding: 10px 12px; border-radius: 10px; background: var(--panel-solid); border: 1px solid var(--line); box-shadow: 0 12px 30px var(--shadow); font-size: 12.5px; }
  .close-buttons { display: flex; gap: 6px; }
  .danger-fill { background: var(--danger); color: #fff; border: 0; border-radius: 7px; padding: 4px 12px; font: inherit; cursor: pointer; }
  .session-tools { display: flex; gap: 4px; margin-top: 4px; }
  .session-tool { display: inline-flex; align-items: center; gap: 4px; padding: 2px 8px; border-radius: 6px; font-size: 11.5px; color: var(--fg-quiet); background: var(--panel); border: 1px solid var(--line); }
  .session-tool:hover { color: var(--fg); background: var(--panel-strong); }
  .session-tool.danger:hover { color: var(--danger); }
  .session-more { position: absolute; right: 6px; top: 6px; width: 24px; height: 20px; display: grid; place-items: center; border-radius: 5px; color: var(--fg-quiet); opacity: 0; font-size: 15px; line-height: 1; }
  .session:hover .session-more, .session.on .session-more { opacity: 1; }
  .session-more:hover { background: var(--panel-strong); color: var(--fg); }
  .topbar .title { flex: 1; min-width: 0; display: flex; align-items: center; }
  .session { display: flex; flex-direction: column; align-items: flex-start; gap: 1px; background: none; border: 0; color: var(--fg); padding: 6px 10px; border-radius: 8px; cursor: pointer; font: inherit; text-align: left; }
  .session-name { font-weight: 600; font-size: 13px; display: flex; align-items: center; gap: 6px; max-width: 100%; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .session-facts { font-size: 11px; color: var(--fg-quiet); }
  .exporting { color: var(--accent); font-weight: 600; }
  .badge { font-size: 9px; font-weight: 700; padding: 0 5px; border-radius: 4px; background: var(--warn); color: #000; }
  .badge.live { background: var(--danger); color: #fff; }
  .no-sessions { margin: 4px 10px; font-size: 12px; color: var(--fg-quiet); }
  .spacer { flex: 1; }
  .content-column { display: flex; flex-direction: column; min-width: 0; min-height: 0; height: 100vh; }
  .update-notice { display: flex; align-items: center; justify-content: center; gap: 12px; padding: 6px 12px; background: var(--accent-soft); font-size: 13px; }
  .topbar { display: flex; align-items: center; gap: 8px; padding: 6px 10px 0 20px; min-height: 34px; }
  .title { flex: 1; }
  .window-controls { display: flex; gap: 2px; }
  .window-controls button { display: grid; place-items: center; width: 40px; height: 30px; background: none; border: 0; border-radius: 6px; color: var(--fg-quiet); cursor: pointer; }
  .window-controls button:hover { background: var(--hover); color: var(--fg); }
  .window-controls .close:hover { background: #c42b1c; color: #fff; }
  .session.renaming { padding: 6px 8px; }
  .rename-field { width: 100%; box-sizing: border-box; height: 28px; padding: 0 8px; font: inherit; font-size: 13.5px; font-weight: 600; }
  .welcome { position: fixed; z-index: 450; left: 270px; bottom: 70px; width: min(460px, calc(100vw - 300px)); padding: 16px 18px 14px; border-radius: 14px; background: linear-gradient(var(--panel-solid), var(--panel-solid)), var(--bg); border: 2px solid #ffd60a; box-shadow: 0 20px 60px var(--shadow); font-size: 13.5px; line-height: 1.5; }
  .welcome h3 { margin: 0 0 8px; font-size: 17px; text-transform: none; letter-spacing: 0; color: var(--fg); }
  .welcome p { margin: 0 0 8px; } .welcome ol { margin: 0 0 12px; padding-left: 20px; } .welcome li { margin-bottom: 4px; }
  .welcome-buttons { display: flex; gap: 8px; }
  .tour-button .glyph { color: #ffd60a; }
  .tour-button.on { color: #ffd60a; }
  .session-menu { position: fixed; z-index: 110; min-width: 200px; padding: 5px; display: flex; flex-direction: column; gap: 2px; background: var(--panel-solid); border: 1px solid var(--line); border-radius: 10px; box-shadow: 0 14px 36px var(--shadow); }
  .session-menu button { display: flex; align-items: center; gap: 8px; background: none; border: 0; color: var(--fg); font: inherit; text-align: left; padding: 7px 10px; border-radius: 7px; cursor: pointer; }
  .session-menu button:hover:not(:disabled) { background: var(--hover); }
  .session-menu button:disabled { opacity: 0.4; cursor: default; }
  .session-menu .danger { color: var(--danger); }
  .content { flex: 1; min-height: 0; overflow-y: auto; padding: 0 20px 14px; }
  .content:has(:global(.session)) { overflow: hidden; }
</style>
