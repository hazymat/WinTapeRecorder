<script lang="ts">
  import { app, ui } from '../state.svelte'
  import { selection } from '../lib/selection.svelte'
  import { sendToShell } from '../bridge'
  // A dismissable tip (as WinSqueeze): gone for good once closed; Settings › General can hide them all or bring them back
  let { id, text, bubble = false }: { id: string; text: string; bubble?: boolean } = $props()
  const shown = $derived(!app.hints.hidden && !app.hints.dismissed.includes(id) && !(bubble && (ui.toast || selection.keys.length)))
  function dismiss() { app.hints = { hidden: app.hints.hidden, dismissed: app.hints.dismissed.concat([id]) }; sendToShell({ type: 'dismissHint', id }) }
</script>

{#if shown}
  <div class="hint" class:bubble role="note"><span class="bulb" aria-hidden="true">i</span><span class="text">{text}</span><button class="close" onclick={dismiss} aria-label="Dismiss hint" title="Got it">✕</button></div>
{/if}

<style>
  .hint { display: flex; align-items: center; gap: 8px; margin: 0 0 8px; padding: 6px 6px 6px 8px; border-radius: 8px; background: var(--accent-soft); color: var(--fg); font-size: 12px; line-height: 1.35; }
  .bubble { position: absolute; left: 50%; bottom: calc(100% + 6px); transform: translateX(-50%); z-index: 20; margin: 0; padding: 3px 3px 3px 8px; gap: 4px; white-space: nowrap; background: var(--panel-solid); border: 1px solid var(--accent); box-shadow: 0 6px 16px var(--shadow); font-size: 11.5px; }
  .bubble::after { content: ""; position: absolute; left: 50%; top: 100%; transform: translateX(-50%); border: 5px solid transparent; border-top-color: var(--accent); }
  .bubble .bulb { display: none; } .bubble .close { width: 18px; height: 18px; font-size: 10px; }
  .bulb { display: grid; place-items: center; flex: none; width: 16px; height: 16px; border-radius: 50%; background: var(--accent); color: var(--on-accent); font-size: 10px; font-weight: 700; font-style: italic; font-family: Georgia, serif; }
  .text { flex: 1; min-width: 0; }
  .close { flex: none; width: 22px; height: 22px; padding: 0; background: none; border: 0; border-radius: 6px; color: var(--fg-quiet); cursor: pointer; font-size: 11px; }
  .close:hover { background: var(--hover); color: var(--fg); }
</style>
