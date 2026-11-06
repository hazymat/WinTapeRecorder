// Selection for browse lists (artists, albums, folders, tracks…): press and hold a row ([data-select-key]) to start
// choosing; then clicks add or take away (Shift: a run, Ctrl: one). The actions show in the action strip.
import { selection, selectOnHold, rowClick, type SelectionAction } from './selection.svelte'
export type SelectableOptions = { scope: string; noun: string; actions: SelectionAction[]; enabled?: boolean; holdMs?: number }

export function selectable(listElement: HTMLElement, initialOptions: SelectableOptions) {
  let options = initialOptions, pressTimer = 0, pressRow: HTMLElement | null = null, startX = 0, startY = 0, swallowClick = false
  const rows = () => Array.from(listElement.querySelectorAll('[data-select-key]')) as HTMLElement[]
  const keyOf = (row: HTMLElement) => row.dataset.selectKey ?? ''
  // Selected neighbours merge into one block (tokens.css), but only in a single-column list: side-by-side tiles each keep their own outline
  const markColumns = (allRows: HTMLElement[]) => { if (allRows.length > 1 && allRows[0].offsetLeft !== allRows[1].offsetLeft) delete listElement.dataset.rowList; else listElement.dataset.rowList = '' }
  const paint = () => { const allRows = rows(); markColumns(allRows); for (const row of allRows) row.classList.toggle('selected', selection.scope === options.scope && selection.keys.includes(keyOf(row))) }
  const stopEffect = $effect.root(() => { $effect(() => { selection.scope; selection.keys; paint() }) })
  const observer = new MutationObserver(paint); observer.observe(listElement, { childList: true, subtree: true })
  const resizeObserver = new ResizeObserver(() => markColumns(rows())); resizeObserver.observe(listElement)

  function cancelPress() { clearTimeout(pressTimer); pressRow?.classList.remove('pressing'); pressRow = null }
  function onPointerDown(event: PointerEvent) {
    swallowClick = false
    if (options.enabled === false || event.button !== 0) return
    const row = (event.target as HTMLElement).closest('[data-select-key]') as HTMLElement | null
    if (!row || !listElement.contains(row) || (event.target as HTMLElement).closest('input, select, textarea, .no-hold')) return
    pressRow = row; startX = event.clientX; startY = event.clientY; row.classList.add('pressing')
    pressTimer = window.setTimeout(() => { if (!pressRow) return; const key = keyOf(pressRow); cancelPress(); swallowClick = true; selectOnHold(options.scope, key, options.actions, options.noun); navigator.vibrate?.(15) }, options.holdMs ?? 450)
  }
  function onPointerMove(event: PointerEvent) { if (pressRow && Math.hypot(event.clientX - startX, event.clientY - startY) > 6) cancelPress() }
  function onClick(event: MouseEvent) {
    if (swallowClick) { swallowClick = false; event.stopPropagation(); event.preventDefault(); return }
    const row = (event.target as HTMLElement).closest('[data-select-key]') as HTMLElement | null
    if (!row || !listElement.contains(row) || (event.target as HTMLElement).closest('.no-hold')) return
    if (rowClick(event, options.scope, keyOf(row), rows().map(keyOf), options.actions, options.noun)) { event.stopPropagation(); event.preventDefault() }
  }
  // Right-click (or the context-menu key) also starts choosing, as on Windows
  function onContextMenu(event: MouseEvent) { const row = (event.target as HTMLElement).closest('[data-select-key]') as HTMLElement | null; if (!row || !listElement.contains(row)) return; event.preventDefault(); selectOnHold(options.scope, keyOf(row), options.actions, options.noun) }
  listElement.addEventListener('pointerdown', onPointerDown)
  listElement.addEventListener('click', onClick, true)
  listElement.addEventListener('contextmenu', onContextMenu)
  window.addEventListener('pointermove', onPointerMove)
  window.addEventListener('pointerup', cancelPress)
  window.addEventListener('pointercancel', cancelPress)
  return {
    update(newOptions: SelectableOptions) { options = newOptions; paint() },
    destroy() { stopEffect(); observer.disconnect(); resizeObserver.disconnect(); listElement.removeEventListener('pointerdown', onPointerDown); listElement.removeEventListener('click', onClick, true); listElement.removeEventListener('contextmenu', onContextMenu); window.removeEventListener('pointermove', onPointerMove); window.removeEventListener('pointerup', cancelPress); window.removeEventListener('pointercancel', cancelPress) }
  }
}
