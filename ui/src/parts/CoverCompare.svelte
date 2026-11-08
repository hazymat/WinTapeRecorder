<script lang="ts">
  // Covers side by side at full size (SPEC.md › Fill gaps & covers): what each one really is, and a look at the detail before choosing
  import { request } from '../bridge'
  export type CompareItem = { key: string; label: string; path?: string; url?: string; inUse: boolean }
  let { sessionId, title, items, onuse, onclose }: { sessionId: string; title: string; items: CompareItem[]; onuse: (item: CompareItem) => void; onclose: () => void } = $props()

  type Loaded = { width: number; height: number; bytes: number; data: string }
  let loaded = $state<Record<string, Loaded | 'failed'>>({})
  let actual = $state(false)
  $effect(() => {
    for (const item of items) request<Loaded>({ type: 'artInfo', id: sessionId, path: item.path ?? '', url: item.url ?? '', full: true })
      .then(found => { loaded = Object.assign({}, loaded, { [item.key]: found }) })
      .catch(() => { loaded = Object.assign({}, loaded, { [item.key]: 'failed' }) })
  })
  const size = (bytes: number) => bytes >= 1048576 ? `${(bytes / 1048576).toFixed(1)} MB` : `${Math.round(bytes / 1024)} KB`
  // The picture on the page itself, above everything
  function toBody(node: HTMLElement) { document.body.appendChild(node); return { destroy() { node.remove() } } }
  // At actual size, the pictures scroll together so the same part can be compared
  let syncing = false
  function scrolled(event: Event) {
    if (syncing || !actual) return
    const from = event.currentTarget as HTMLElement, x = from.scrollLeft / Math.max(1, from.scrollWidth - from.clientWidth), y = from.scrollTop / Math.max(1, from.scrollHeight - from.clientHeight)
    syncing = true
    for (const box of Array.from(document.querySelectorAll<HTMLElement>('.compare-picture'))) if (box !== from) { box.scrollLeft = x * (box.scrollWidth - box.clientWidth); box.scrollTop = y * (box.scrollHeight - box.clientHeight) }
    requestAnimationFrame(() => syncing = false)
  }
</script>

<svelte:window onkeydown={(event) => { if (event.key === 'Escape') { event.preventDefault(); event.stopImmediatePropagation(); onclose() } }} />

<div class="compare" use:toBody role="dialog" aria-label="Compare covers">
  <header>
    <strong>{title}</strong>
    <span class="spacer"></span>
    <span class="zoom" role="group" aria-label="Picture size"><button class:on={!actual} onclick={() => actual = false}>Fit</button><button class:on={actual} onclick={() => actual = true}>Actual pixels</button></span>
    <button class="close" onclick={onclose}>Close<kbd>Esc</kbd></button>
  </header>
  <div class="columns" style:--count={items.length}>
    {#each items as item (item.key)}
      {@const info = loaded[item.key]}
      <section>
        <div class="caption">
          <strong>{item.label}</strong>
          {#if info && info !== 'failed'}<span class:low={info.width < 500}>{info.width} × {info.height}</span><span>{size(info.bytes)}</span>{:else if info === 'failed'}<span>Not available</span>{:else}<span>Loading…</span>{/if}
          <span class="spacer"></span>
          {#if item.inUse}<span class="in-use">✓ In use</span>{:else if info && info !== 'failed'}<button class="use" onclick={() => onuse(item)}>Use this</button>{/if}
        </div>
        <div class="compare-picture" class:actual onscroll={scrolled}>
          {#if info && info !== 'failed'}<img src={info.data} alt={item.label} />{/if}
        </div>
      </section>
    {/each}
  </div>
</div>

<style>
  .compare { position: fixed; inset: 0; z-index: 400; display: flex; flex-direction: column; gap: 10px; padding: 14px 18px 18px; background: var(--bg); color: var(--fg); font-size: 13px; }
  header { display: flex; align-items: center; gap: 10px; flex: none; }
  header strong { font-size: 15px; }
  .spacer { flex: 1; }
  .zoom { display: inline-flex; }
  .zoom button, .close, .use { font: inherit; color: var(--fg); background: var(--panel); border: 1px solid var(--line); padding: 3px 12px; cursor: pointer; height: 28px; }
  .zoom button:first-child { border-radius: 7px 0 0 7px; } .zoom button:last-child { border-radius: 0 7px 7px 0; border-left: 0; }
  .zoom button.on { background: var(--accent); color: var(--on-accent); border-color: transparent; }
  .close, .use { border-radius: 7px; display: inline-flex; align-items: center; gap: 6px; }
  .use { background: var(--accent); color: var(--on-accent); border-color: transparent; font-weight: 600; }
  .columns { flex: 1; min-height: 0; display: grid; grid-template-columns: repeat(var(--count), minmax(0, 1fr)); gap: 12px; }
  section { min-width: 0; min-height: 0; display: flex; flex-direction: column; gap: 6px; }
  .caption { flex: none; display: flex; align-items: center; gap: 12px; min-height: 30px; white-space: nowrap; color: var(--fg-quiet); }
  .caption strong { color: var(--fg); }
  .caption .low { color: var(--warn); font-weight: 600; }
  .in-use { color: #3ecf6e; font-weight: 600; }
  .compare-picture { flex: 1; min-height: 0; border-radius: 8px; background: var(--panel); display: grid; place-items: center; overflow: hidden; }
  .compare-picture img { width: 100%; height: 100%; object-fit: contain; display: block; min-width: 0; min-height: 0; } /* small pictures are enlarged too: the point is to see how they hold up */
  .compare-picture.actual { overflow: auto; display: block; }
  .compare-picture.actual img { width: auto; height: auto; max-width: none; max-height: none; object-fit: none; image-rendering: pixelated; }
</style>
