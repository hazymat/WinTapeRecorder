<script lang="ts">
  // "Spectrum stops at …": the highest frequency with real sound in it, and what that says about how the source was encoded.
  // Click it for a short explanation with a scale (SPEC.md › Recording › Source quality)
  import { describeCutoff } from '../state.svelte'
  let { hz, text = '', capital = false }: { hz: number; text?: string; capital?: boolean } = $props()
  let open = $state(false)
  let at = $state({ left: 0, top: 0 })
  function toggle(event: MouseEvent) { const rect = (event.currentTarget as HTMLElement).getBoundingClientRect(); at = { left: Math.max(8, Math.min(rect.left, innerWidth - 470)), top: rect.bottom + 6 }; open = !open }
  let box = $state<HTMLElement>()
  const top = 24000, width = 440, kHz = (value: number) => (value / 1000).toFixed(1)
  const x = (value: number) => Math.max(0, Math.min(width, value / top * width))
  // Typical places the top is cut, from lossy encoders at common bitrates up to lossless
  const marks = [
    { hz: 16000, label: '128 kbps' },
    { hz: 19000, label: '256 kbps' },
    { hz: 20500, label: 'lossless / CD' },
  ]
  let pop = $state<HTMLElement>()
  function outside(event: PointerEvent) { if (open && box && !box.contains(event.target as Node) && !pop?.contains(event.target as Node)) open = false }
  // The explanation sits on the page itself, above everything, whatever panel the phrase is in
  function toBody(node: HTMLElement) { document.body.appendChild(node); return { destroy() { node.remove() } } }
</script>

<svelte:window onpointerdown={outside} onkeydown={(event) => { if (event.key === 'Escape') open = false }} />

<span class="cutoff" bind:this={box}>
  <button class="cutoff-button" onclick={toggle} title="What does this mean?" aria-expanded={open}>{capital ? 'Spectrum' : 'spectrum'} stops at {kHz(hz)} kHz<span class="what">?</span></button>
  {#if open}
    <div class="cutoff-pop" use:toBody bind:this={pop} style:left="{at.left}px" style:top="{at.top}px" role="dialog" aria-label="What the spectrum cutoff means">
      <p><strong>Above {kHz(hz)} kHz there's almost no sound in what's being recorded.</strong> It's measured from the audio itself, every couple of seconds.</p>
      <svg viewBox="0 0 {width} 92" class="scale" aria-hidden="true">
        <rect x="0" y="4" width={x(hz)} height="22" rx="4" class="heard" />
        <rect x={x(hz)} y="4" width={width - x(hz)} height="22" rx="4" class="empty" />
        {#each marks as mark, row}<line x1={x(mark.hz)} x2={x(mark.hz)} y1="2" y2={40 + row * 14} class="mark" /><text x={x(mark.hz) - 4} y={44 + row * 14} class="mark-text" text-anchor="end">{mark.label}</text>{/each}
        <line x1={x(hz)} x2={x(hz)} y1="0" y2="30" class="cut" />
        <text x="0" y="90" class="axis">0</text><text x={x(10000)} y="90" class="axis" text-anchor="middle">10 kHz</text><text x={x(20000)} y="90" class="axis" text-anchor="middle">20 kHz</text><text x={width} y="90" class="axis" text-anchor="end">24</text>
      </svg>
      <p>Music naturally goes up to about 20 kHz. Lossy encoders (MP3, AAC, Opus, the formats streaming services use) save space by cutting off the top, and the lower the bitrate, the lower the cut. So where the sound stops is a fingerprint of the source's encoding, not of your PC or this recording.</p>
      <p class="verdict">{kHz(hz)} kHz suggests <strong>{text || describeCutoff(hz)}</strong>. The recording keeps exactly what arrives; it can't add back what the source cut.</p>
    </div>
  {/if}
</span>

<style>
  .cutoff { position: relative; display: inline-block; }
  .cutoff-button { border: 0; background: transparent; padding: 0; font: inherit; font-weight: 600; color: inherit; cursor: pointer; text-decoration: underline dotted; text-underline-offset: 3px; }
  .cutoff-button:hover { color: var(--accent); }
  .what { display: inline-grid; place-items: center; width: 14px; height: 14px; margin-left: 4px; border-radius: 50%; border: 1px solid currentColor; font-size: 10px; text-decoration: none; vertical-align: 1px; }
  .cutoff-pop { position: fixed; z-index: 300; width: 440px; box-sizing: border-box; width: 462px; padding: 12px 14px; border-radius: 10px; background: linear-gradient(var(--panel-solid), var(--panel-solid)), var(--bg); border: 1px solid var(--line); box-shadow: 0 16px 40px var(--shadow); font-size: 12.5px; font-weight: 400; color: var(--fg); white-space: normal; line-height: 1.45; }
  .cutoff-pop p { margin: 0 0 8px; }
  .cutoff-pop .verdict { margin: 0; }
  .scale { width: 100%; height: auto; margin: 2px 0 8px; overflow: visible; }
  .heard { fill: var(--accent); opacity: 0.75; }
  .empty { fill: var(--line); }
  .mark { stroke: var(--fg-quiet); stroke-width: 1; stroke-dasharray: 3 3; }
  .mark-text, .axis { font-size: 11px; fill: var(--fg-quiet); }
  .cut { stroke: var(--warn); stroke-width: 3; }
</style>
