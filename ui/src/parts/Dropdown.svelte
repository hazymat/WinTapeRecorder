<script lang="ts">
  // Glass dropdown with its label always visible ("Sort  Album artist"), replacing native selects whose Windows popup ignores the skin
  let { label, value, options, onchange, align = 'right', shortcut = '' }: { label: string; value: string; options: [string, string][]; onchange: (value: string) => void; align?: 'left' | 'right'; shortcut?: string } = $props()
  let open = $state(false)
  let root: HTMLElement
  const current = $derived(options.find(([optionValue]) => optionValue === value)?.[1] ?? value)
  const choose = (optionValue: string) => { open = false; if (optionValue !== value) onchange(optionValue) }
</script>

<svelte:window onmousedown={(event) => { if (open && !root.contains(event.target as Node)) open = false }} onkeydown={(event) => { if (open && event.key === 'Escape') { event.stopPropagation(); open = false } }} />

<div class="dropdown" bind:this={root}>
  <button class="current" class:open onclick={() => open = !open} aria-haspopup="listbox" aria-expanded={open} aria-keyshortcuts={shortcut || undefined}>
    <span class="text"><span class="label">{label}</span><span class="value">{current}</span></span>{#if shortcut}<kbd>{shortcut}</kbd>{/if}<svg class="chevron" viewBox="0 0 10 6" width="9" height="6" aria-hidden="true"><path d="M1 1l4 4 4-4" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round" /></svg>
  </button>
  {#if open}
    <div class="menu {align}" role="listbox" aria-label={label}>
      <div class="menu-title">{label}</div>
      {#each options as [optionValue, optionLabel] (optionValue)}
        <button role="option" aria-selected={optionValue === value} class:on={optionValue === value} onclick={() => choose(optionValue)}><span>{optionLabel}</span>{#if optionValue === value}<span class="tick">✓</span>{/if}</button>
      {/each}
    </div>
  {/if}
</div>

<style>
  .dropdown { position: relative; }
  .current { display: flex; align-items: center; gap: 7px; height: 30px; padding: 0 10px 0 11px; background: var(--panel); color: var(--fg); border: 1px solid var(--line); border-radius: 999px; font: inherit; font-size: 12px; cursor: pointer; white-space: nowrap; }
  .current:hover, .current.open { background: var(--hover); }
  .text { display: inline-flex; align-items: baseline; gap: 7px; } /* label and value share a baseline; the pair is centred */ .label, .value { line-height: 1; } .label { color: var(--fg-quiet); font-size: 10.5px; text-transform: uppercase; letter-spacing: 0.06em; font-weight: 600; }
  .value { font-weight: 600; }
  .chevron { color: var(--fg-quiet); transition: transform 150ms; } .open .chevron { transform: rotate(180deg); }
  .menu { position: absolute; top: calc(100% + 6px); min-width: 180px; max-height: 60vh; overflow-y: auto; z-index: 40; padding: 6px; background: var(--panel-solid); backdrop-filter: blur(24px) saturate(1.5); border: 1px solid var(--line); border-radius: 12px; box-shadow: 0 16px 40px var(--shadow); }
  .menu.right { right: 0; } .menu.left { left: 0; }
  .menu-title { padding: 4px 10px 6px; color: var(--fg-quiet); font-size: 10.5px; text-transform: uppercase; letter-spacing: 0.06em; font-weight: 600; }
  .menu button { display: flex; align-items: center; justify-content: space-between; gap: 12px; width: 100%; background: none; border: 0; color: var(--fg); font: inherit; font-size: 13px; text-align: left; padding: 7px 10px; border-radius: 8px; cursor: pointer; }
  .menu button:hover { background: var(--hover); }
  .menu button.on { background: var(--accent-soft); }
  .tick { color: var(--accent); font-weight: 700; }
</style>
