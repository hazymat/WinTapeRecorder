<script lang="ts">
  // The tour (docs/TOUR.md): a floating window that steps through the screen you're on, pointing at each thing it talks about.
  // ← → step, Esc pauses (it remembers where you were); the Explore tab shows how the other screens fit in, and can open them
  import { app, nav, tour, writeSetting } from '../state.svelte'
  import { steps, concepts, starts, stages, contextNames, type TourContext } from '../lib/tour'

  // Recording but no music yet (armed, or started before the player): the tour waits with you, and moves on when the music arrives
  const context = $derived<TourContext>(nav.view === 'record' ? (!app.status?.recording ? 'record' : app.status.waitingForSound || app.levels.seconds - app.levels.silentSeconds < 1 ? 'waiting' : 'recording') : nav.step)
  const key = $derived(tour.tab === 'concept' || tour.tab === 'starts' ? tour.tab : context)
  const list = $derived(tour.tab === 'concept' ? concepts : tour.tab === 'starts' ? starts : steps[context])
  const index = $derived(Math.min(tour.index[key] ?? 0, list.length - 1))
  const step = $derived(list[index])
  let moreOpen = $state(false)

  // Where the thing being talked about is on screen, followed as the page moves
  let rect = $state<DOMRect | null>(null)
  let windowBox = $state<HTMLElement>()
  let dragged = $state<{ left: number; top: number } | null>(null)
  function findTarget(selector?: string): HTMLElement | null {
    if (!selector) return null
    if (selector === '@wave') return Array.from(document.querySelectorAll<HTMLElement>('canvas')).filter(visible).sort((first, second) => area(second) - area(first))[0] ?? null
    return Array.from(document.querySelectorAll<HTMLElement>(selector)).find(visible) ?? null
  }
  const area = (element: HTMLElement) => { const box = element.getBoundingClientRect(); return box.width * box.height }
  const visible = (element: HTMLElement) => { const box = element.getBoundingClientRect(); return box.width > 0 && box.height > 0 && !element.closest('.tour-window') }
  let frame = 0
  function follow() {
    const element = tour.open && tour.tab !== 'explore' ? findTarget(step?.target) : null
    const box = element?.getBoundingClientRect() ?? null
    if (!box !== !rect || (box && rect && (Math.abs(box.left - rect.left) > 0.5 || Math.abs(box.top - rect.top) > 0.5 || Math.abs(box.width - rect.width) > 0.5 || Math.abs(box.height - rect.height) > 0.5))) rect = box
    frame = requestAnimationFrame(follow)
  }
  $effect(() => { if (!tour.open) return; frame = requestAnimationFrame(follow); return () => cancelAnimationFrame(frame) })
  // A new step: bring its target into view, put the window back beside it, fold the extra text
  // Once the window has been moved, it stays where the user put it
  $effect(() => { step; context; tour.tab; moreOpen = false; queueMicrotask(() => findTarget(step?.target)?.scrollIntoView({ block: 'nearest', inline: 'nearest' })) })

  // The window sits beside its target where there's room, otherwise below or above it
  // The concepts, and anything not tied to something on screen, sit in the middle and take the attention; the rest sits beside what it points at
  const centred = $derived(tour.tab !== 'show' || !rect)
  const width = $derived(step?.image && tour.tab !== 'explore' ? Math.min(980, innerWidth - 40) : centred ? Math.min(640, innerWidth - 40) : 440)
  let windowHeight = $state(240)
  const placed = $derived.by(() => {
    if (dragged) return dragged
    const height = windowHeight, gap = 18, margin = 12
    if (centred) return { left: Math.round((innerWidth - width) / 2), top: Math.max(margin, Math.round((innerHeight - height) / 2)) }
    const clamp = (left: number, top: number) => ({ left: Math.max(margin, Math.min(innerWidth - width - margin, left)), top: Math.max(margin, Math.min(innerHeight - height - margin, top)) })
    if (!rect || tour.tab !== 'show') return clamp(innerWidth - width - 28, innerHeight - height - 70)
    if (innerWidth - rect.right > width + gap + margin) return clamp(rect.right + gap, rect.top)
    if (rect.left > width + gap + margin) return clamp(rect.left - width - gap, rect.top)
    if (innerHeight - rect.bottom > height + gap + margin) return clamp(rect.left, rect.bottom + gap)
    if (rect.top > height + gap + margin) return clamp(rect.left, rect.top - height - gap)
    return clamp(innerWidth - width - 28, innerHeight - height - 70)
  })
  // The line from the window to what it's pointing at
  const link = $derived.by(() => {
    if (!rect || tour.tab !== 'show' || !windowBox) return null
    const height = windowHeight, from = { x: Math.max(placed.left, Math.min(placed.left + width, rect.left + rect.width / 2)), y: Math.max(placed.top, Math.min(placed.top + height, rect.top + rect.height / 2)) }
    const to = { x: Math.max(rect.left - 4, Math.min(rect.right + 4, from.x)), y: Math.max(rect.top - 4, Math.min(rect.bottom + 4, from.y)) }
    return Math.hypot(to.x - from.x, to.y - from.y) < 8 ? null : { from, to }
  })

  let dragFrom: null | { x: number; y: number; left: number; top: number } = null
  function dragDown(event: PointerEvent) { if ((event.target as HTMLElement).closest('button')) return; dragFrom = { x: event.clientX, y: event.clientY, left: placed.left, top: placed.top }; (event.currentTarget as HTMLElement).setPointerCapture(event.pointerId) }
  function dragMove(event: PointerEvent) { if (dragFrom) dragged = { left: dragFrom.left + event.clientX - dragFrom.x, top: dragFrom.top + event.clientY - dragFrom.y } }

  // The app concept → Finding track starts → Show me → Explore, and back
  const order = ['concept', 'starts', 'show', 'explore'] as const
  function go(delta: number) {
    const at = order.indexOf(tour.tab)
    if (tour.tab === 'explore') { if (delta < 0) tour.tab = 'show'; return }
    const next = index + delta
    if (next < 0) { if (at > 0) { const back = order[at - 1]; tour.tab = back; if (back !== 'show') tour.index = Object.assign({}, tour.index, { [back]: (back === 'concept' ? concepts : starts).length - 1 }) } return }
    if (next >= list.length) { tour.tab = order[at + 1]; return }
    tour.index = Object.assign({}, tour.index, { [key]: next })
  }
  function pause() { tour.open = false; writeSetting('tourSeen', '1') }
  function onKey(event: KeyboardEvent) {
    if (!tour.open || (event.target as HTMLElement)?.matches?.('input:not([type=checkbox]), textarea, select')) return
    if (event.ctrlKey || event.altKey || event.metaKey) return
    if (event.key === 'ArrowRight' || event.key === 'ArrowLeft' || event.key === 'Escape') {
      event.preventDefault(); event.stopImmediatePropagation()
      if (event.key === 'Escape') pause(); else go(event.key === 'ArrowRight' ? 1 : -1)
    }
  }
  // Explore: open another screen, even while recording (the recording carries on)
  const finished = $derived(app.sessions.filter(session => !session.recording))
  function showStage(target: TourContext) {
    if (target !== 'cuts' && target !== 'tags' && target !== 'export') { nav.view = 'record'; tour.tab = 'show'; return }
    const session = nav.view === 'session' ? app.sessions.find(candidate => candidate.id === nav.sessionId) : finished[0]
    if (!session) return
    nav.sessionId = session.id; nav.view = 'session'; nav.wantStep = target
    tour.index = Object.assign({}, tour.index, { [target]: 0 }); tour.tab = 'show'
  }
</script>

<svelte:window onkeydowncapture={onKey} />

{#if tour.open}
  {#if rect && tour.tab === 'show'}
    <div class="tour-ring" style:left="{rect.left - 5}px" style:top="{rect.top - 5}px" style:width="{rect.width + 10}px" style:height="{rect.height + 10}px"></div>
    {#if link}<svg class="tour-link" aria-hidden="true"><line x1={link.from.x} y1={link.from.y} x2={link.to.x} y2={link.to.y} /><circle cx={link.to.x} cy={link.to.y} r="4" /></svg>{/if}
  {/if}
  {#if centred}<div class="tour-dim" role="presentation"></div>{/if}
  <div class="tour-window" class:centred bind:this={windowBox} bind:offsetHeight={windowHeight} style:left="{placed.left}px" style:top="{placed.top}px" style:width="{width}px" role="dialog" aria-label="Tour">
    <div class="tour-head" role="presentation" onpointerdown={dragDown} onpointermove={dragMove} onpointerup={() => dragFrom = null} title="Drag to move">
      <span class="tour-tabs" role="tablist">
        <button role="tab" class:on={tour.tab === 'concept'} aria-selected={tour.tab === 'concept'} onclick={() => tour.tab = 'concept'}>The app concept</button>
        <button role="tab" class:on={tour.tab === 'starts'} aria-selected={tour.tab === 'starts'} onclick={() => tour.tab = 'starts'}>Finding track starts</button>
        <button role="tab" class:on={tour.tab === 'show'} aria-selected={tour.tab === 'show'} onclick={() => tour.tab = 'show'}>Show me</button>
        <button role="tab" class:on={tour.tab === 'explore'} aria-selected={tour.tab === 'explore'} onclick={() => tour.tab = 'explore'}>Explore</button>
      </span>
      <span class="tour-where">{tour.tab === 'explore' ? 'The whole journey' : `${tour.tab === 'show' ? contextNames[context] + ' · ' : ''}${index + 1} of ${list.length}`}</span>
    </div>
    {#if tour.tab !== 'explore' && step}
      <div class="tour-body">
        <h3>{step.title}</h3>
        {#if step.keys}
          <div class="tour-keys">
            <span class="keycap">←</span><span>Previous</span>
            <span class="keycap">→</span><span>Next</span>
            <span class="keycap wide">Esc</span><span>Come back to it later</span>
          </div>
        {:else}<p>{step.text}</p>{/if}
        {#if step.image}<img class="tour-image" src={'tour/' + step.image} alt={step.title} />{/if}
        {#if step.more}
          <button class="tour-more" onclick={() => moreOpen = !moreOpen} aria-expanded={moreOpen}>{moreOpen ? '▾' : '▸'} More info</button>
          {#if moreOpen}{#if Array.isArray(step.more)}<ul class="tour-more-text">{#each step.more as item}<li>{item}</li>{/each}</ul>{:else}<p class="tour-more-text">{step.more}</p>{/if}{/if}
        {/if}
        {#if step.target && !rect}<p class="tour-absent">(It isn't on screen right now{context === 'recording' ? ': it appears once the first track has been marked' : ''}.)</p>{/if}
      </div>
    {:else}
      <div class="tour-body">
        {#if app.status?.recording}<p class="tour-calm">Your recording carries on while you look around. Recording… at the top left brings you straight back.</p>{/if}
        {#each stages as stage}
          {@const here = stage.context === context || (stage.context === 'record' && (context === 'recording' || context === 'waiting'))}
          <div class="tour-stage" class:here>
            <strong>{stage.title}{here ? ' · you are here' : ''}</strong>
            <p>{stage.text}</p>
            {#if !here}
              {#if stage.context === 'record' || nav.view === 'session' || finished.length}<button class="tour-open" onclick={() => showStage(stage.context)}>{stage.context === 'record' && app.status?.recording ? 'Back to the recording' : 'Show me this screen'}</button>
              {:else}<span class="tour-later">You'll see this once a recording is finished.</span>{/if}
            {/if}
          </div>
        {/each}
        <p class="tour-note">Checking markers while recording is a convenience, not a step you must finish first. Record a long session, and check markers whenever you have a few spare minutes.</p>
      </div>
    {/if}
    <div class="tour-buttons">
      <button onclick={() => go(-1)} disabled={tour.tab === 'concept' && index === 0}>Previous<kbd>←</kbd></button>
      <button class="tour-next" onclick={() => go(1)} disabled={tour.tab === 'explore'}>{index === list.length - 1 ? (tour.tab === 'concept' ? 'Finding track starts' : tour.tab === 'starts' ? 'Show me' : tour.tab === 'show' ? 'Explore' : 'Next') : 'Next'}<kbd>→</kbd></button>
      <span class="spacer"></span>
      <button onclick={pause}>Pause tour<kbd>Esc</kbd></button>
    </div>
  </div>
{/if}

<style>
  /* Bright against both the dark and the light look */
  .tour-ring { position: fixed; z-index: 500; pointer-events: none; border: 3px solid #ffd60a; border-radius: 9px; box-shadow: 0 0 0 3px rgba(0, 0, 0, 0.45), 0 0 22px 4px rgba(255, 214, 10, 0.45); animation: tour-pulse 1.6s ease-in-out infinite; }
  @keyframes tour-pulse { 50% { box-shadow: 0 0 0 3px rgba(0, 0, 0, 0.45), 0 0 34px 10px rgba(255, 214, 10, 0.6); } }
  .tour-link { position: fixed; inset: 0; width: 100vw; height: 100vh; z-index: 499; pointer-events: none; }
  .tour-link line { stroke: #ffd60a; stroke-width: 2.5; stroke-dasharray: 6 4; }
  .tour-link circle { fill: #ffd60a; }
  .tour-window { position: fixed; z-index: 501; display: flex; flex-direction: column; border-radius: 12px; background: linear-gradient(var(--panel-solid), var(--panel-solid)), var(--bg); border: 2px solid #ffd60a; box-shadow: 0 18px 50px var(--shadow); color: var(--fg); font-size: 15.5px; max-height: calc(100vh - 24px); }
  .tour-window.centred { font-size: 17px; }
  .tour-window.centred .tour-body { padding: 16px 24px 8px; }
  .tour-window.centred .tour-body h3 { font-size: 22px; }
  .tour-image { display: block; width: 100%; height: auto; border-radius: 8px; border: 1px solid var(--line); margin: 4px 0 10px; }
  .tour-dim { position: fixed; inset: 0; z-index: 498; background: rgba(0, 0, 0, 0.35); pointer-events: none; }
  .tour-head { display: flex; align-items: center; gap: 10px; padding: 8px 10px 6px 10px; cursor: move; border-bottom: 1px solid var(--line); }
  .tour-tabs { display: inline-flex; }
  .tour-tabs button { font: inherit; font-size: 12.5px; white-space: nowrap; font-weight: 600; padding: 3px 10px; border: 1px solid var(--line); background: var(--panel); color: var(--fg-quiet); cursor: pointer; }
  .tour-tabs button:first-child { border-radius: 7px 0 0 7px; } .tour-tabs button:last-child { border-radius: 0 7px 7px 0; border-left: 0; }
  .tour-tabs button.on { background: #ffd60a; color: #1a1a1a; border-color: transparent; }
  .tour-where { font-size: 11.5px; color: var(--fg-quiet); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
  .tour-body { padding: 10px 14px 4px; overflow-y: auto; overflow-x: hidden; min-height: 0; }
  .tour-body h3 { margin: 0 0 8px; font-size: 19px; text-transform: none; letter-spacing: 0; color: var(--fg); }
  .tour-body p { margin: 0 0 8px; line-height: 1.5; }
  .tour-more { border: 0; background: none; padding: 0; margin: 0 0 6px; font: inherit; font-size: 0.93em; color: var(--accent); cursor: pointer; }
  .tour-more-text { color: var(--fg-quiet); font-size: 0.93em; margin: 0 0 8px; padding-left: 20px; line-height: 1.5; }
  .tour-more-text li { margin-bottom: 5px; }
  .tour-keys { display: grid; grid-template-columns: auto 1fr; gap: 10px 14px; align-items: center; margin: 4px 0 12px; font-size: 15px; }
  .keycap { display: inline-grid; place-items: center; min-width: 38px; height: 38px; padding: 0 8px; box-sizing: border-box; border-radius: 8px; border: 1px solid var(--line); border-bottom-width: 3px; background: var(--panel-strong); font-size: 18px; font-weight: 600; color: var(--fg); justify-self: start; }
  .keycap.wide { font-size: 14px; }
  .tour-absent, .tour-later { color: var(--fg-quiet); font-size: 12px; }
  .tour-calm { padding: 6px 10px; border-radius: 8px; background: color-mix(in srgb, #3ecf6e 14%, transparent); color: var(--fg); }
  .tour-stage { padding: 7px 10px; margin-bottom: 6px; border-radius: 8px; background: var(--panel); border: 1px solid var(--line); }
  .tour-stage.here { border-color: #ffd60a; }
  .tour-stage p { margin: 3px 0 6px; font-size: 12.5px; color: var(--fg-quiet); }
  .tour-open { font: inherit; font-size: 12px; padding: 2px 10px; border-radius: 6px; border: 1px solid var(--line); background: var(--panel-strong); color: var(--fg); cursor: pointer; }
  .tour-note { font-size: 12.5px; color: var(--fg-quiet); }
  .tour-buttons { display: flex; gap: 6px; padding: 8px 10px 10px; border-top: 1px solid var(--line); }
  .tour-buttons .spacer { flex: 1; }
  .tour-buttons button { display: inline-flex; align-items: center; gap: 6px; font: inherit; font-size: 13.5px; padding: 4px 10px; border-radius: 7px; border: 1px solid var(--line); background: var(--panel); color: var(--fg); cursor: pointer; }
  .tour-buttons button:disabled { opacity: 0.4; cursor: default; }
  .tour-buttons .tour-next { background: #ffd60a; color: #1a1a1a; border-color: transparent; font-weight: 600; }
  .tour-buttons kbd { font-size: 10.5px; }
</style>
