<script lang="ts">
  import { untrack } from 'svelte'
  import { app } from '../state.svelte'
  // Input level check (SPEC.md › Recording): a peak bar per channel with peak hold, and a clip light that stays lit until clicked
  const floorDb = -60
  let held = $state<{ db: number; at: number }[]>([])
  let display = $state<number[]>([])
  let clipsSeen = $state(0)
  const toDb = (level: number) => level <= 0 ? floorDb : Math.max(floorDb, 20 * Math.log10(level))
  const fraction = (db: number) => (db - floorDb) / -floorDb
  $effect(() => {
    const peaks = app.levels.peaks, now = performance.now()
    const previousDisplay = untrack(() => display), previousHeld = untrack(() => held)
    // Bars fall back smoothly; holds stay 1.5 s
    display = peaks.map((peak, channel) => Math.max(toDb(peak), (previousDisplay[channel] ?? floorDb) - 1.6))
    held = peaks.map((peak, channel) => { const db = toDb(peak), old = previousHeld[channel]; return !old || db >= old.db || now - old.at > 1500 ? { db, at: now } : old })
  })
  const clipped = $derived(app.levels.clipped > clipsSeen)
  const labels = [-60, -40, -30, -20, -12, -6, -3, 0]
  const names = $derived(display.length === 2 ? ['L', 'R'] : display.map((_, index) => String(index + 1)))
</script>

<div class="meter">
  {#each display as db, channel}
    <div class="row">
      <span class="name">{names[channel]}</span>
      <div class="bar">
        <div class="fill" style:clip-path="inset(0 {100 - fraction(db) * 100}% 0 0)"></div>
        {#if held[channel]}<div class="hold" class:hot={held[channel].db > -1} style:left="calc({fraction(held[channel].db) * 100}% - 2px)"></div>{/if}
      </div>
      <span class="value">{held[channel] && held[channel].db > floorDb ? held[channel].db.toFixed(1) : '−∞'}</span>
    </div>
  {:else}
    <div class="row"><span class="name"></span><div class="bar"></div><span class="value"></span></div>
  {/each}
  <div class="scale"><span class="name"></span><div class="ticks">{#each labels as label}<span style:left="{fraction(label) * 100}%">{label}</span>{/each}</div><span class="value"></span></div>
  <button class="clip" class:on={clipped} onclick={() => clipsSeen = app.levels.clipped} title={clipped ? `${app.levels.clipped - clipsSeen} samples at full scale. Click to reset.` : 'No clipping'}>CLIP{#if clipped}<span class="count">{app.levels.clipped - clipsSeen}</span>{/if}</button>
</div>

<style>
  .meter { position: relative; display: flex; flex-direction: column; gap: 6px; padding-right: 70px; }
  .row, .scale { display: flex; align-items: center; gap: 8px; }
  .name { width: 14px; color: var(--fg-quiet); font-size: 11px; font-weight: 600; text-align: center; }
  .bar { position: relative; flex: 1; height: 12px; border-radius: 3px; background: var(--track); overflow: hidden; }
  .fill { position: absolute; inset: 0; background: linear-gradient(90deg, #3ecf6e 0%, #3ecf6e 75%, #ffd166 85%, #ff8c42 93%, #ff4a3d 100%); }
  .hold { position: absolute; top: 0; bottom: 0; width: 3px; background: var(--fg); opacity: 0.8; }
  .hold.hot { background: #ff4a3d; opacity: 1; }
  .value { width: 46px; text-align: right; font-family: var(--font-display); font-size: 12px; color: var(--fg-quiet); }
  .ticks { position: relative; flex: 1; height: 14px; font-size: 10px; color: var(--fg-quiet); }
  .ticks span { position: absolute; transform: translateX(-50%); }
  .ticks span:first-child { transform: none; } .ticks span:last-child { transform: translateX(-100%); }
  .clip { position: absolute; right: 0; top: 0; height: 28px; display: flex; align-items: center; gap: 4px; padding: 0 8px; border-radius: 6px; border: 1px solid var(--line); background: var(--panel); color: var(--fg-quiet); font: inherit; font-size: 11px; font-weight: 700; letter-spacing: 0.08em; cursor: pointer; }
  .clip.on { background: #ff4a3d; color: #fff; border-color: transparent; box-shadow: 0 0 12px rgba(255, 74, 61, 0.6); }
  .count { font-weight: 600; letter-spacing: 0; font-size: 10px; }
</style>
