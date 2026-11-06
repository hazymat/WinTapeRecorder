<script lang="ts">
  import type { Snippet } from 'svelte'
  // A small glass dialog: a question with OK/Cancel, optionally with fields (children)
  let { title, text = '', confirmLabel = 'OK', danger = false, onconfirm, oncancel, children }: { title: string; text?: string; confirmLabel?: string; danger?: boolean; onconfirm: () => void; oncancel: () => void; children?: Snippet } = $props()
  let box = $state<HTMLElement>()
  $effect(() => { (box?.querySelector('input') as HTMLInputElement | null ?? box?.querySelector('button.confirm') as HTMLButtonElement | null)?.focus() })
  function onKey(event: KeyboardEvent) {
    if (event.key === 'Escape') { event.preventDefault(); event.stopImmediatePropagation(); oncancel() }
    else if (event.key === 'Enter') { event.preventDefault(); event.stopImmediatePropagation(); onconfirm() }
    else event.stopPropagation() // typing here never reaches the waveform's shortcuts
  }
</script>

<div class="backdrop" role="presentation" onpointerdown={(event) => { if (event.target === event.currentTarget) oncancel() }}>
  <div class="dialog" role="dialog" aria-label={title} tabindex="-1" bind:this={box} onkeydown={onKey}>
    <h3>{title}</h3>
    {#if text}<p>{text}</p>{/if}
    {#if children}<div class="fields">{@render children()}</div>{/if}
    <div class="buttons"><button class="ghost" onclick={oncancel}>Cancel</button><button class="confirm" class:primary={!danger} class:danger onclick={onconfirm}>{confirmLabel}</button></div>
  </div>
</div>

<style>
  .backdrop { position: fixed; inset: 0; z-index: 120; display: grid; place-items: center; background: rgba(0, 0, 0, 0.25); }
  .dialog { width: min(440px, calc(100vw - 40px)); padding: 16px 18px 14px; border-radius: 14px; background: var(--panel-solid); border: 1px solid var(--line); box-shadow: 0 24px 60px var(--shadow); outline: none; }
  h3 { margin: 0 0 8px; font-size: 16px; color: var(--fg); text-transform: none; letter-spacing: 0; }
  p { margin: 0 0 12px; color: var(--fg-quiet); line-height: 1.45; }
  .fields { display: flex; flex-direction: column; gap: 10px; margin-bottom: 14px; }
  .buttons { display: flex; justify-content: flex-end; gap: 8px; }
  .danger { background: var(--danger); color: #fff; border: 0; border-radius: 8px; padding: 6px 14px; font: inherit; font-weight: 600; cursor: pointer; }
</style>
