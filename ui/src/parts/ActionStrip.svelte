<script lang="ts">
  import { ui, undoToast } from '../state.svelte'
  import { selection, clearSelection, runAction, actionKey, type SelectionAction, type MenuChoice } from '../lib/selection.svelte'
  // The multi-select bar (as WinSqueeze): one fixed place at the bottom for the selection's actions and for messages with Undo
  const count = $derived(selection.keys.length)
  const actions = $derived(selection.actions.filter(action => !(action.single && count > 1)))
  // Always there, always the same height (SPEC.md › Window and look): it never pushes the view up while you're clicking.
  // When nothing is selected and no message is showing, it keeps the last message, dimmed
  let lastText = $state('')
  $effect(() => { if (ui.toast?.text) lastText = ui.toast.text })
  let menu = $state<null | { label: string; choices: MenuChoice[] | null }>(null)
  async function press(action: SelectionAction) {
    if (!action.menu) { menu = null; runAction(action); return }
    if (menu?.label === action.label) { menu = null; return }
    const keys = selection.keys.slice()
    menu = { label: action.label, choices: null }
    const choices = await action.menu(keys)
    if (menu) menu = { label: action.label, choices }
  }
  const choose = async (choice: MenuChoice) => { menu = null; await choice.run() }
  $effect(() => { if (!count) menu = null })
  const keyLabel = (key: string) => key === 'Delete' ? 'Del' : key
</script>

<div class="wrap">
{#if menu}
  <div class="menu" role="menu">
    {#if !menu.choices}<div class="loading">Loading…</div>{:else}{#each menu.choices as choice (choice.label + (choice.detail ?? ''))}<button role="menuitem" onclick={() => choose(choice)}><span>{choice.label}</span>{#if choice.detail}<span class="detail">{choice.detail}</span>{/if}</button>{/each}{/if}
  </div>
{/if}
<div class="strip" role="status" aria-live="polite">
  {#if count}
    <span class="count">{count} {selection.noun}{count === 1 ? '' : 's'} selected</span>
    {#if ui.toast}<span class="message inline">{ui.toast.text}</span>{#if ui.toast.undo}<button class="undo" onclick={undoToast}>Undo<kbd>Ctrl+Z</kbd></button>{/if}{/if}
    <div class="actions">
      {#each actions as action (action.label)}<button class:danger={action.danger} class:open={menu?.label === action.label} onclick={() => press(action)}>{action.label}{#if actionKey(action)}<kbd>{keyLabel(actionKey(action) ?? '')}</kbd>{/if}</button>{/each}
      <button class="clear" onclick={clearSelection} aria-label="Clear selection">Done<kbd>Esc</kbd></button>
    </div>
  {:else if ui.toast}
    {#key ui.toast.id}<svg class="ring" viewBox="0 0 20 20" aria-hidden="true" style:--ms={`${ui.toast.ms}ms`}><circle class="track" cx="10" cy="10" r="8" /><circle class="left" cx="10" cy="10" r="8" pathLength="100" /></svg>{/key}
    <span class="message">{ui.toast.text}</span>
    <div class="actions">
      {#if ui.toast.action}<button onclick={() => { const action = ui.toast?.action; ui.toast = null; action?.run() }}>{ui.toast.action.label}<kbd>{ui.toast.action.key}</kbd></button>{/if}
      {#if ui.toast.undo}<button class="undo" onclick={undoToast}>Undo<kbd>Ctrl+Z</kbd></button>{/if}
      <button class="clear" onclick={() => ui.toast = null} aria-label="Dismiss">Close</button>
    </div>
  {:else}
    <span class="message idle">{lastText || 'Ready'}</span>
  {/if}
</div>
</div>

<style>
  .wrap { position: relative; }
  .strip { display: flex; align-items: center; gap: 12px; height: 46px; flex: none; overflow: hidden; padding: 0 16px; background: var(--glass); backdrop-filter: blur(var(--blur)) saturate(1.4); border-top: 1px solid var(--line); font-size: 13px; }
  .message.idle { color: var(--fg-quiet); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .count { font-weight: 600; white-space: nowrap; }
  .actions { display: flex; gap: 6px; margin-left: auto; overflow-x: auto; scrollbar-width: none; }
  button { display: flex; align-items: center; gap: 6px; white-space: nowrap; background: var(--panel); color: var(--fg); border: 1px solid var(--line); border-radius: 8px; padding: 5px 12px; font: inherit; cursor: pointer; min-height: 32px; }
  button:hover { background: var(--hover); }
  button.danger { color: var(--danger); }
  .clear { background: none; }
  button.open { background: var(--accent-soft); color: var(--accent); }
  .menu { position: absolute; right: 16px; bottom: calc(100% + 6px); z-index: 45; min-width: 240px; max-height: 50vh; overflow-y: auto; padding: 6px; display: flex; flex-direction: column; gap: 2px; background: var(--panel-solid); border: 1px solid var(--line); border-radius: 12px; box-shadow: 0 16px 40px var(--shadow); }
  .menu button { justify-content: space-between; border: 0; background: none; min-height: 34px; }
  .menu button:hover { background: var(--hover); }
  .detail { color: var(--fg-quiet); font-size: 11px; }
  .loading { padding: 8px 12px; color: var(--fg-quiet); }
  .undo { color: var(--accent); font-weight: 600; background: none; }
  .message { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; min-width: 0; }
  .message.inline { color: var(--fg-quiet); }
  .ring { flex: none; width: 18px; height: 18px; transform: rotate(-90deg); }
  .ring circle { fill: none; stroke-width: 2.6; }
  .ring .track { stroke: var(--track); }
  .ring .left { stroke: var(--accent); stroke-linecap: round; stroke-dasharray: 100; animation: run-down var(--ms, 6s) linear forwards; }
  @keyframes run-down { to { stroke-dashoffset: -100; } }
</style>
