// Choosing several rows (as WinSqueeze): press and hold a row, or Ctrl/Shift+click, and the selection bar offers what can be done with them.
// One list at a time owns the selection (its scope). An action's key runs it; Delete runs the action keyed 'Delete'; Esc clears.
export type MenuChoice = { label: string; detail?: string; run: () => void | Promise<void> }
export type SelectionAction = { label: string; key?: string; danger?: boolean; single?: boolean; run?: (keys: string[]) => void | Promise<void>; menu?: (keys: string[]) => Promise<MenuChoice[]> }
export const selection = $state({ scope: '', keys: [] as string[], anchor: '', actions: [] as SelectionAction[], noun: 'item' })

export const actionKey = (action: SelectionAction) => action.key
export const isSelected = (scope: string, key: string) => selection.scope === scope && selection.keys.includes(key)
export function clearSelection() { selection.scope = ''; selection.keys = []; selection.anchor = ''; selection.actions = []; const focused = document.activeElement as HTMLElement | null; if (focused?.closest?.('[data-select-key]')) focused.blur() }
export function selectKeys(scope: string, keys: string[], actions: SelectionAction[], noun = 'item') { selection.scope = scope; selection.keys = keys; selection.anchor = keys[keys.length - 1] ?? ''; selection.actions = actions; selection.noun = noun }

export function selectOnHold(scope: string, key: string, actions: SelectionAction[], noun = 'item') {
  if (selection.scope !== scope) selectKeys(scope, [key], actions, noun)
  else if (!selection.keys.includes(key)) { selection.keys = selection.keys.concat([key]); selection.anchor = key }
}
// A tick box in a list: Shift+click runs from the box last clicked to this one and sets them all as this box now is. For an album's box, clicked is all its tracks
export function tickKeys(event: MouseEvent, scope: string, clicked: string[], allKeys: string[], on: boolean, actions: SelectionAction[], noun = 'item') {
  const inScope = selection.scope === scope, anchorIndex = inScope ? allKeys.indexOf(selection.anchor) : -1
  const positions = clicked.map(key => allKeys.indexOf(key)).filter(position => position >= 0)
  const extending = event.shiftKey && anchorIndex >= 0 && positions.length > 0, anchor = selection.anchor
  const range = extending ? allKeys.slice(Math.min(anchorIndex, ...positions), Math.max(anchorIndex, ...positions) + 1) : clicked
  const next = new Set(inScope ? selection.keys : [])
  for (const key of range) { if (on) next.add(key); else next.delete(key) }
  if (!next.size) { clearSelection(); return }
  selectKeys(scope, allKeys.filter(key => next.has(key)).concat(Array.from(next).filter(key => !allKeys.includes(key))), actions, noun)
  selection.anchor = extending ? anchor : clicked[clicked.length - 1]
}
// A click on a row: true when it was a selection gesture (so the row's own action doesn't run)
export function rowClick(event: MouseEvent, scope: string, key: string, allKeys: string[], actions: SelectionAction[], noun = 'item') {
  const inScope = selection.scope === scope && selection.keys.length > 0
  if (event.shiftKey) {
    const anchorIndex = Math.max(0, allKeys.indexOf(inScope ? selection.anchor : key)), clickedIndex = allKeys.indexOf(key)
    const range = allKeys.slice(Math.min(anchorIndex, clickedIndex), Math.max(anchorIndex, clickedIndex) + 1)
    selectKeys(scope, inScope ? Array.from(new Set(selection.keys.concat(range))) : range, actions, noun); selection.anchor = allKeys[anchorIndex]
    return true
  }
  if (event.ctrlKey || event.metaKey || inScope) {
    if (selection.scope !== scope) selectKeys(scope, [], actions, noun)
    selection.keys = selection.keys.includes(key) ? selection.keys.filter(selectedKey => selectedKey !== key) : selection.keys.concat([key])
    selection.anchor = key; selection.actions = actions
    if (!selection.keys.length) clearSelection()
    return true
  }
  return false
}
export async function runAction(action: SelectionAction) { const keys = selection.keys.slice(); await action.run?.(keys) }

addEventListener('keydown', (event: KeyboardEvent) => {
  if (!selection.keys.length || (event.target as HTMLElement)?.matches?.('input:not([type=checkbox]), textarea, select')) return // a tick box keeps the focus after a click: Esc, Y and the rest still work from it
  if (event.key === 'Escape') { event.preventDefault(); clearSelection(); return }
  if (event.ctrlKey || event.altKey || event.metaKey) return
  const action = selection.actions.find(candidate => { const key = actionKey(candidate); return key === event.key || (key?.length === 1 && key === event.key.toUpperCase()) || (event.key === 'Backspace' && key === 'Delete') })
  if (action?.run && !(action.single && selection.keys.length > 1)) { event.preventDefault(); event.stopImmediatePropagation(); runAction(action) }
}, true)
