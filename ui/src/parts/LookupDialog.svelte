<script lang="ts" module>
  export type TagMatch = { source: string; title: string; artist: string; album: string; albumArtist: string; year: string; genre: string; trackNumber: number; trackCount: number; artUrl: string; detail: string }
</script>
<script lang="ts">
  import { untrack } from 'svelte'
  import { request } from '../bridge'
  import { readSetting, writeSetting } from '../state.svelte'
  // Online tag lookup (SPEC.md › Tags): search a source, pick a result, choose what to take from it
  let { sessionId, trackCount, artist: startArtist, title: startTitle, album: startAlbum, onapply, oncancel }: { sessionId: string; trackCount: number; artist: string; title: string; album: string; onapply: (match: TagMatch, take: Record<string, boolean>, artPath: string, artThumb: string) => void; oncancel: () => void } = $props()
  let source = $state(readSetting('lookupSource', 'musicbrainz'))
  const start = untrack(() => ({ artist: startArtist, title: startTitle, album: startAlbum, single: trackCount === 1 })) // the search starts from the tracks as they are
  let artist = $state(start.artist), title = $state(start.single ? start.title : ''), album = $state(start.album)
  let results = $state<TagMatch[] | null>(null), searching = $state(false), error = $state('')
  let chosen = $state<TagMatch | null>(null), applying = $state(false)
  // With several tracks, the album-wide fields; with one, its title and artist too
  let take = $state<Record<string, boolean>>({ title: start.single, artist: start.single, album: true, albumArtist: true, year: true, genre: true, trackNumber: start.single, art: true })
  const sources: [string, string][] = [['musicbrainz', 'MusicBrainz'], ['itunes', 'iTunes'], ['discogs', 'Discogs']]
  async function search() {
    searching = true; error = ''; results = null; chosen = null; writeSetting('lookupSource', source)
    try { results = await request<TagMatch[]>({ type: 'lookup', source, artist: artist.trim(), title: title.trim(), album: album.trim() }) }
    catch (lookupError) { error = (lookupError as Error).message }
    searching = false
  }
  $effect(() => { untrack(() => { if (artist || title || album) search() }) }) // once, with what the tracks already say
  async function apply() {
    if (!chosen) return
    applying = true; error = ''
    let artPath = '', artThumb = ''
    if (take.art && chosen.artUrl) {
      try { const saved = await request<{ path: string; thumb: string }>({ type: 'fetchArt', id: sessionId, url: chosen.artUrl }); artPath = saved.path; artThumb = saved.thumb }
      catch (artError) { error = (artError as Error).message + ' The other tags were still applied.' }
    }
    applying = false
    onapply(chosen, take, artPath, artThumb)
  }
  function onKey(event: KeyboardEvent) { event.stopPropagation(); if (event.key === 'Escape') oncancel(); if (event.key === 'Enter' && (event.target as HTMLElement).matches('input')) search() }
  const labels: [string, string][] = [['title', 'Title'], ['artist', 'Artist'], ['album', 'Album'], ['albumArtist', 'Album artist'], ['year', 'Year'], ['genre', 'Genre'], ['trackNumber', 'Track no.'], ['art', 'Cover picture']]
</script>

<div class="backdrop" role="presentation" onpointerdown={(event) => { if (event.target === event.currentTarget) oncancel() }}>
  <div class="dialog" role="dialog" aria-label="Look up tags" tabindex="-1" onkeydown={onKey}>
    <h3>Look up tags{trackCount > 1 ? ` for ${trackCount} tracks` : ''}</h3>
    <div class="query">
      <div class="sources">{#each sources as [key, label]}<button class:on={source === key} onclick={() => { source = key; search() }}>{label}</button>{/each}</div>
      <input bind:value={artist} placeholder="Artist" />
      <input bind:value={title} placeholder="Title" />
      <input bind:value={album} placeholder="Album" />
      <button class="primary" onclick={search} disabled={searching}>{searching ? 'Searching…' : 'Search'}</button>
    </div>
    {#if error}<p class="error">{error}</p>{/if}
    <div class="results">
      {#if results}
        {#each results as result}
          <button class="result" class:on={chosen === result} onclick={() => chosen = result} ondblclick={() => { chosen = result; apply() }}>
            {#if result.artUrl && result.source !== 'MusicBrainz'}<img src={result.artUrl} alt="" loading="lazy" />{:else}<span class="art-slot">{result.artUrl ? '♪' : ''}</span>{/if}
            <span class="text"><strong>{result.title || result.album}</strong> · {result.artist || result.albumArtist}<span class="quiet">{result.album ? ` · ${result.album}` : ''}{result.year ? ` · ${result.year}` : ''}{result.genre ? ` · ${result.genre}` : ''}{result.trackNumber ? ` · track ${result.trackNumber}${result.trackCount ? '/' + result.trackCount : ''}` : ''}{result.detail ? ` · ${result.detail}` : ''}</span></span>
          </button>
        {:else}<p class="quiet">Nothing found. Try fewer words, or another source.</p>{/each}
      {:else if searching}<p class="quiet">Searching {sources.find(([key]) => key === source)?.[1]}…</p>{/if}
    </div>
    <div class="take">
      <span class="quiet">Take:</span>
      {#each labels as [key, label]}<label><input type="checkbox" bind:checked={take[key]} />{label}</label>{/each}
    </div>
    <div class="buttons"><span class="quiet note">Amazon has no public catalogue to search, so it isn't offered.</span><button class="ghost" onclick={oncancel}>Cancel</button><button class="primary" disabled={!chosen || applying} onclick={apply}>{applying ? 'Fetching the picture…' : `Apply to ${trackCount} track${trackCount === 1 ? '' : 's'}`}</button></div>
  </div>
</div>

<style>
  .backdrop { position: fixed; inset: 0; z-index: 120; display: grid; place-items: center; background: rgba(0, 0, 0, 0.25); }
  .dialog { width: min(760px, calc(100vw - 40px)); max-height: calc(100vh - 60px); display: flex; flex-direction: column; gap: 10px; padding: 16px 18px 14px; border-radius: 14px; background: var(--panel-solid); border: 1px solid var(--line); box-shadow: 0 24px 60px var(--shadow); outline: none; }
  h3 { margin: 0; font-size: 16px; color: var(--fg); text-transform: none; letter-spacing: 0; }
  .query { display: flex; gap: 6px; flex-wrap: wrap; align-items: center; }
  .query input { flex: 1; min-width: 120px; }
  .sources { display: flex; gap: 2px; background: var(--panel); border-radius: 8px; padding: 2px; }
  .sources button { background: none; border: 0; color: var(--fg-quiet); font: inherit; font-size: 12px; padding: 4px 10px; border-radius: 6px; cursor: pointer; }
  .sources button.on { background: var(--accent-soft); color: var(--accent); }
  .results { flex: 1; min-height: 160px; max-height: 46vh; overflow-y: auto; display: flex; flex-direction: column; gap: 2px; }
  .result { display: flex; align-items: center; gap: 10px; text-align: left; background: none; border: 1px solid transparent; border-radius: 8px; padding: 5px 8px; color: var(--fg); font: inherit; font-size: 13px; cursor: pointer; }
  .result:hover { background: var(--hover); }
  .result.on { background: var(--accent-soft); border-color: color-mix(in srgb, var(--accent) 45%, transparent); }
  .result img, .art-slot { width: 40px; height: 40px; border-radius: 4px; object-fit: cover; flex: none; display: grid; place-items: center; background: var(--panel); color: var(--fg-quiet); }
  .text { min-width: 0; }
  .take { display: flex; flex-wrap: wrap; gap: 4px 14px; align-items: center; font-size: 12.5px; }
  .take label { display: flex; align-items: center; gap: 4px; }
  .take input { accent-color: var(--accent); }
  .buttons { display: flex; gap: 8px; align-items: center; justify-content: flex-end; }
  .note { margin-right: auto; font-size: 11.5px; }
  .error { color: var(--danger); margin: 0; font-size: 12.5px; }
</style>
