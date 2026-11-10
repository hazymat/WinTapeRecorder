<script lang="ts" module>
  export type IconName = 'record' | 'stop' | 'pause' | 'play' | 'marker' | 'settings' | 'minimise' | 'maximise' | 'restore' | 'close' | 'folder' | 'export' | 'zoom-in' | 'zoom-out' | 'fit' | 'split' | 'tape' | 'trash' | 'starts' | 'ends' | 'redetect' | 'undo' | 'redo' | 'edit' | 'tour'
</script>
<script lang="ts">
  // SVG icons: text glyphs render off-centre with colour fringes in WebView2
  let { name, size = 18 }: { name: IconName; size?: number } = $props()
</script>

<svg width={size} height={size} viewBox="0 0 24 24" aria-hidden="true" class="icon">
  {#if name === 'record'}<circle cx="12" cy="12" r="6.5" />
  {:else if name === 'stop'}<rect x="6.5" y="6.5" width="11" height="11" rx="1.6" />
  {:else if name === 'pause'}<rect x="6.5" y="6" width="4" height="12" rx="1.2" /><rect x="13.5" y="6" width="4" height="12" rx="1.2" />
  {:else if name === 'play'}<path d="M8 5.14v13.72a1 1 0 0 0 1.52.85l10.6-6.86a1 1 0 0 0 0-1.7L9.52 4.29A1 1 0 0 0 8 5.14z" />
  {:else if name === 'marker'}<path d="M12 3.5v17M7 6.5h8.5l-2 2.5 2 2.5H7" class="stroke" />
  {:else if name === 'settings'}<circle cx="12" cy="12" r="3" class="stroke" /><path d="M12 3.5v2.2M12 18.3v2.2M3.5 12h2.2M18.3 12h2.2M6 6l1.6 1.6M16.4 16.4L18 18M6 18l1.6-1.6M16.4 7.6L18 6" class="stroke" />
  {:else if name === 'minimise'}<path d="M6 12h12" class="stroke thin" />
  {:else if name === 'maximise'}<rect x="6.5" y="6.5" width="11" height="11" rx="1.5" class="stroke thin" />
  {:else if name === 'restore'}<rect x="6" y="8.5" width="9.5" height="9.5" rx="1.5" class="stroke thin" /><path d="M9 8.5V7.5A1.5 1.5 0 0 1 10.5 6h6A1.5 1.5 0 0 1 18 7.5v6a1.5 1.5 0 0 1-1.5 1.5h-1" class="stroke thin" />
  {:else if name === 'close'}<path d="M7 7l10 10M17 7L7 17" class="stroke thin" />
  {:else if name === 'folder'}<path d="M3.5 7.5A1.5 1.5 0 0 1 5 6h4.5l2 2H19a1.5 1.5 0 0 1 1.5 1.5v8A1.5 1.5 0 0 1 19 19H5a1.5 1.5 0 0 1-1.5-1.5z" class="stroke" />
  {:else if name === 'export'}<path d="M12 15V4.5M7.5 9L12 4.5 16.5 9M5 14v4.5A1.5 1.5 0 0 0 6.5 20h11a1.5 1.5 0 0 0 1.5-1.5V14" class="stroke" />
  {:else if name === 'zoom-in'}<circle cx="10.5" cy="10.5" r="6" class="stroke" /><path d="M15 15l5 5M10.5 7.8v5.4M7.8 10.5h5.4" class="stroke" />
  {:else if name === 'zoom-out'}<circle cx="10.5" cy="10.5" r="6" class="stroke" /><path d="M15 15l5 5M7.8 10.5h5.4" class="stroke" />
  {:else if name === 'fit'}<path d="M4 9V5h4M20 9V5h-4M4 15v4h4M20 15v4h-4" class="stroke" /><path d="M7 12h10" class="stroke" />
  {:else if name === 'split'}<path d="M12 3.5v17" class="stroke" /><path d="M4 9l3 3-3 3M20 9l-3 3 3 3" class="stroke" />
  {:else if name === 'tape'}<rect x="3" y="6" width="18" height="12" rx="2" class="stroke" /><circle cx="8.5" cy="11.5" r="2" class="stroke" /><circle cx="15.5" cy="11.5" r="2" class="stroke" /><path d="M7 18l1.5-2.5h7L17 18" class="stroke" />
  {:else if name === 'tour'}<circle cx="12" cy="12" r="8.5" class="stroke" /><path d="M9.6 9.4a2.5 2.5 0 1 1 3.4 2.3c-.6.3-1 .8-1 1.5v.6 M12 16.6v.2" class="stroke" />
  {:else if name === 'edit'}<path d="M5 19l1-4.5L15.5 5l3.5 3.5L9.5 18z M13.5 7l3.5 3.5" class="stroke" />
  {:else if name === 'trash'}<path d="M5 7h14M10 7V5h4v2M7 7l1 12.5h8L17 7" class="stroke" />
  {:else if name === 'starts'}<path d="M6 4.5v15" class="stroke" /><path d="M10 7.5v9l7-4.5z" />
  {:else if name === 'ends'}<path d="M18 4.5v15" class="stroke" /><path d="M6 7.5v9l7-4.5z" />
  {:else if name === 'redetect'}<path d="M19.5 12a7.5 7.5 0 1 1-2.2-5.3M19.5 4.5v3.2h-3.2" class="stroke" /><path d="M12 8.5v7M9 10.5v3M15 10.5v3" class="stroke" />
  {:else if name === 'undo'}<path d="M9 7L5 11l4 4M5 11h9a5 5 0 0 1 0 10h-2" class="stroke" />
  {:else if name === 'redo'}<path d="M15 7l4 4-4 4M19 11h-9a5 5 0 0 0 0 10h2" class="stroke" />{/if}
</svg>

<style>
  .icon { display: block; fill: currentColor; flex: none; }
  .thin { stroke-width: 1.3; }
  .stroke { fill: none; stroke: currentColor; stroke-width: 1.8; stroke-linecap: round; stroke-linejoin: round; }
</style>
