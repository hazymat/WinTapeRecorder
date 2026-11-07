<script lang="ts" module>
  export type TagFields = { artist: string; title: string; album: string; albumArtist: string; genre: string; year: string; composer: string; comment: string; trackNumber: string; trackCount: string; artPath: string }
  export const tagFieldNames: [keyof TagFields, string][] = [['title', 'Title'], ['artist', 'Artist'], ['album', 'Album'], ['albumArtist', 'Album artist'], ['genre', 'Genre'], ['year', 'Year'], ['composer', 'Composer'], ['trackNumber', 'Track no.'], ['trackCount', 'Tracks on album'], ['comment', 'Comment']]
</script>
<script lang="ts">
  import { untrack } from 'svelte'
  import { request } from '../bridge'
  import { toast, type Track } from '../state.svelte'
  import Dialog from './Dialog.svelte'
  // Tags for one track or many (SPEC.md › Tags): with several, a field left empty stays as each track has it
  let { sessionId, tracks, thumbs, onapply, onlookup, oncancel }: { sessionId: string; tracks: Track[]; thumbs: Record<string, string>; onapply: (fields: Partial<TagFields>, single: boolean) => void; onlookup: (fields: TagFields) => void; oncancel: () => void } = $props()
  const initial = untrack(() => tracks) // the fields start from the tracks as they are now
  const single = initial.length === 1
  const common = (read: (track: Track) => string) => initial.every(track => read(track) === read(initial[0])) ? read(initial[0]) : ''
  let fields = $state<TagFields>({
    title: common(track => track.title), artist: common(track => track.artist), album: common(track => track.album), albumArtist: common(track => track.albumArtist ?? ''), genre: common(track => track.genre ?? ''), year: common(track => track.year ?? ''),
    composer: common(track => track.composer ?? ''), comment: common(track => track.comment ?? ''), trackNumber: single && initial[0].trackNumber ? String(initial[0].trackNumber) : '', trackCount: common(track => track.trackCount ? String(track.trackCount) : ''), artPath: common(track => track.artPath ?? ''),
  })
  let artThumb = $state('')
  $effect(() => { artThumb = fields.artPath ? thumbs[fields.artPath] ?? '' : '' })
  const shown = tagFieldNames.filter(([name]) => single || (name !== 'title' && name !== 'trackNumber'))
  async function chooseArt() {
    try { const result = await request<{ path: string; thumb: string }>({ type: 'chooseArt', id: sessionId }); if (result.path) { fields.artPath = result.path; thumbs[result.path] = result.thumb; artThumb = result.thumb } }
    catch (error) { toast('Could not use that picture: ' + (error as Error).message) }
  }
  function apply() {
    if (single) { onapply(Object.assign({}, fields), true); return }
    const changed: Partial<TagFields> = {}
    for (const [name, value] of Object.entries(fields) as [keyof TagFields, string][]) if (value.trim() !== '') changed[name] = value.trim()
    onapply(changed, false)
  }
</script>

<Dialog wide title={single ? `Tags: ${tracks[0].title || 'track'}` : `Tags for ${tracks.length} tracks`} text={single ? '' : 'Fields left empty stay as each track has them.'} confirmLabel="Set" onconfirm={apply} {oncancel}>
  <div class="grid">
    {#each shown as [name, label]}
      <label class:short={name === 'year' || name === 'trackNumber' || name === 'trackCount'}>{label}<input bind:value={fields[name]} placeholder={!single && name !== 'trackCount' && !fields[name] ? '(as each track)' : ''} /></label>
    {/each}
  </div>
  <div class="art">
    {#if artThumb}<img src={artThumb} alt="Album art" />{:else}<div class="no-art">No picture</div>{/if}
    <div class="art-buttons">
      <button class="ghost" onclick={chooseArt}>Choose picture…</button>
      {#if fields.artPath}<button class="ghost" onclick={() => { fields.artPath = ''; artThumb = '' }}>Remove picture</button>{/if}
      <button class="ghost" onclick={() => onlookup(Object.assign({}, fields))}>Look up online…</button>
    </div>
  </div>
</Dialog>

<style>
  .grid { display: grid; grid-template-columns: 1fr 1fr; gap: 8px 12px; }
  label { display: flex; flex-direction: column; gap: 3px; font-size: 12px; color: var(--fg-quiet); }
  label.short input { width: 90px; }
  .art { display: flex; gap: 12px; align-items: center; margin-top: 6px; }
  img, .no-art { width: 84px; height: 84px; border-radius: 6px; object-fit: cover; flex: none; }
  .no-art { display: grid; place-items: center; background: var(--panel); border: 1px dashed var(--line); color: var(--fg-quiet); font-size: 11px; }
  .art-buttons { display: flex; flex-wrap: wrap; gap: 6px; }
</style>
