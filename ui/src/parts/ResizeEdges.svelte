<script lang="ts">
  import { sendToShell } from '../bridge'
  // Frameless windows: the page reaches the window's edge (so Windows' rounded corners clip it cleanly), and these thin strips hand
  // resizing back to Windows, as the native frame would
  const edges = ['top', 'bottom', 'left', 'right', 'topleft', 'topright', 'bottomleft', 'bottomright']
  const start = (event: PointerEvent, edge: string) => { if (event.button === 0) { event.preventDefault(); sendToShell({ type: 'resizeWindow', edge }) } }
</script>

{#each edges as edge}<div class="edge {edge}" role="presentation" onpointerdown={(event) => start(event, edge)}></div>{/each}

<style>
  .edge { position: fixed; z-index: 200; }
  .top, .bottom { left: 10px; right: 10px; height: 5px; cursor: ns-resize; } .top { top: 0; } .bottom { bottom: 0; }
  .left, .right { top: 10px; bottom: 10px; width: 5px; cursor: ew-resize; } .left { left: 0; } .right { right: 0; }
  .topleft, .topright, .bottomleft, .bottomright { width: 10px; height: 10px; }
  .topleft { top: 0; left: 0; cursor: nwse-resize; } .bottomright { bottom: 0; right: 0; cursor: nwse-resize; }
  .topright { top: 0; right: 0; cursor: nesw-resize; } .bottomleft { bottom: 0; left: 0; cursor: nesw-resize; }
</style>
