<script lang="ts" module>
  export type WaveView = { start: number; framesPerPixel: number }
  export type SectionAudition = 'join' | 'section' | 'from-start' | 'to-end'
  export type Grab = { trackId: string; edge: 'start' | 'end' } | null
</script>
<script lang="ts">
  import { untrack } from 'svelte'
  import { request } from '../bridge'
  import Dropdown from './Dropdown.svelte'
  import { formatTime, formatLength, type Track, type NameChange, type Exclusion } from '../state.svelte'
  // The session's waveform (SPEC.md › Editing): an overview of the whole session, and a detail view that zooms with the wheel down to single samples
  let { exclusions = [], showHint = false, onAuditionSection, onExclude, onExclusionMove, onExclusionDone, onExclusionFades, onExclusionRemove, linked = false, sessionId, sampleRate, channels, totalFrames, tracks, changes, dropouts = [], mode = 'wave', autoHeight = true, selectedIds, currentId, cursor, playhead, grab, view = $bindable(), onCursor, onEdge, onEdgeDone, onRelease, onPickTrack, onZoomTrack }: {
    exclusions?: Exclusion[]; showHint?: boolean; onAuditionSection?: (id: string, choice: SectionAudition) => void; onExclude?: (start: number, end: number) => void; onExclusionMove?: (id: string, start: number, end: number) => void; onExclusionDone?: (id: string) => void
    onExclusionFades?: (id: string, fadeOutMs: number, fadeInMs: number) => void; onExclusionRemove?: (id: string) => void
    linked?: boolean; sessionId: string; sampleRate: number; channels: number; totalFrames: number; tracks: Track[]; changes: NameChange[]; dropouts?: { frame: number; ms: number }[]; mode?: 'wave' | 'spectrogram'; autoHeight?: boolean; selectedIds: string[]; currentId: string; cursor: number; playhead: number; grab: Grab
    view: WaveView; onCursor: (frame: number) => void; onEdge: (grab: NonNullable<Grab>, frame: number) => void; onEdgeDone: (grab: NonNullable<Grab>) => void; onRelease?: () => void; onPickTrack: (trackId: string, additive: boolean) => void; onZoomTrack: (trackId: string) => void
  } = $props()

  let detailCanvas = $state<HTMLCanvasElement>(), overviewCanvas = $state<HTMLCanvasElement>()
  let width = $state(800), detailHeight = $state(300)
  const overviewHeight = 54, rulerHeight = 20, flagHeight = 16
  type PeakData = { mode: 'peaks' | 'samples' | 'none'; start: number; end: number; channels: number[][] }
  let detailData = $state<PeakData | null>(null), overviewData = $state<PeakData | null>(null)
  // Ctrl+wheel makes the waveform taller (quiet passages, fades); it stays while zooming
  let verticalScale = $state(1), manualHeight = $state(false)
  // Ctrl+wheel sets the height and it stays through horizontal zooming; switching Auto height on or off goes back to automatic
  $effect(() => { autoHeight; untrack(() => { manualHeight = false; verticalScale = 1 }) })
  const minVerticalScale = 1, maxVerticalScale = 10000 // effectively no cap: very quiet starts and fades can fill the lane
  let css = $state({ wave: '#5ccfff', waveDim: '#5ccfff', shade: 'rgba(92,207,255,0.08)', shadeOn: 'rgba(92,207,255,0.16)', start: '#4cd964', end: '#ff7a6b', playhead: '#fff', fg: '#eceef3', quiet: '#8e94a0', line: 'rgba(255,255,255,0.08)', accent: '#5ccfff', panel: 'rgba(255,255,255,0.06)', warn: '#ffb74d' })
  function readColours() { const style = getComputedStyle(document.documentElement), previous = untrack(() => css), read = (name: string, fallback: string) => style.getPropertyValue(name).trim() || fallback; css = { wave: read('--wave', previous.wave), waveDim: read('--wave-rms', previous.waveDim), shade: read('--track-shade', previous.shade), shadeOn: read('--track-shade-on', previous.shadeOn), start: read('--start', previous.start), end: read('--end', previous.end), playhead: read('--playhead', previous.playhead), fg: read('--fg', previous.fg), quiet: read('--fg-quiet', previous.quiet), line: read('--line', previous.line), accent: read('--accent', previous.accent), panel: read('--panel', previous.panel), warn: read('--warn', previous.warn) } }
  $effect(() => { untrack(readColours); const observer = new MutationObserver(readColours); observer.observe(document.documentElement, { attributes: true, attributeFilter: ['data-skin', 'class', 'style'] }); return () => observer.disconnect() })

  // ---- View limits ----
  const minFramesPerPixel = 1 / 64 // 64 pixels per sample at the closest
  const fitFramesPerPixel = $derived(Math.max(minFramesPerPixel, totalFrames / Math.max(1, width)))
  export function clampView(next: WaveView): WaveView {
    const framesPerPixel = Math.min(fitFramesPerPixel, Math.max(minFramesPerPixel, next.framesPerPixel))
    const span = framesPerPixel * width
    return { framesPerPixel, start: Math.max(0, Math.min(Math.max(0, totalFrames - span), next.start)) }
  }
  const frameToX = (frame: number) => (frame - view.start) / view.framesPerPixel
  const xToFrame = (x: number) => view.start + x * view.framesPerPixel

  // ---- Data: requested for the visible range; the last answer is drawn (stretched) until the next arrives ----
  let requesting = false, wanted = ''
  async function fetchDetail() {
    const key = `${sessionId}|${view.start}|${view.framesPerPixel}|${width}`
    if (requesting) { wanted = key; return }
    requesting = true; wanted = ''
    try {
      const columns = Math.min(4000, Math.ceil(width * devicePixelRatio))
      const data = await request<PeakData>({ type: 'peaks', id: sessionId, start: view.start, end: view.start + width * view.framesPerPixel, columns })
      if (data?.mode && data.mode !== 'none') { detailData = data; fitHeight(data) }
    } catch { }
    requesting = false
    if (wanted && wanted !== '') fetchDetail()
  }
  $effect(() => { sessionId; view.start; view.framesPerPixel; width; fetchDetail() })
  // Spectrogram (SPEC.md › Editing): fetched for the visible range in the same way, drawn through a colour map
  type SpectroData = { columns: number; rows: number; start: number; end: number; nyquist: number; data: string }
  let spectroData = $state<SpectroData | null>(null), spectroImage: HTMLCanvasElement | null = null
  let spectroBusy = false, spectroWanted = false
  async function fetchSpectrogram() {
    if (mode !== 'spectrogram') return
    if (spectroBusy) { spectroWanted = true; return }
    spectroBusy = true; spectroWanted = false
    try {
      const data = await request<SpectroData>({ type: 'spectrogram', id: sessionId, start: view.start, end: view.start + width * view.framesPerPixel, columns: Math.min(2000, Math.ceil(width)), rows: 256 })
      if (data?.rows) { spectroData = data; spectroImage = paintSpectrogram(data) }
    } catch { }
    spectroBusy = false
    if (spectroWanted) fetchSpectrogram()
  }
  $effect(() => { sessionId; view.start; view.framesPerPixel; width; mode; fetchSpectrogram() })
  // Colour map: near-black, then blue, then warm, then white at full scale
  const palette = (() => { const table: [number, number, number][] = []; for (let value = 0; value < 256; value++) { const t = value / 255; const mix = (from: number[], to: number[], at: number) => from.map((start, index) => Math.round(start + (to[index] - start) * at)) as [number, number, number]; table.push(t < 0.45 ? mix([10, 12, 24], [28, 110, 200], t / 0.45) : t < 0.8 ? mix([28, 110, 200], [250, 205, 60], (t - 0.45) / 0.35) : mix([250, 205, 60], [255, 255, 255], (t - 0.8) / 0.2)) } return table })()
  function paintSpectrogram(data: SpectroData) {
    const bytes = Uint8Array.from(atob(data.data), character => character.charCodeAt(0))
    const image = document.createElement('canvas'); image.width = data.columns; image.height = data.rows
    const context = image.getContext('2d')!, pixels = context.createImageData(data.columns, data.rows)
    for (let column = 0; column < data.columns; column++) for (let row = 0; row < data.rows; row++) { const [red, green, blue] = palette[bytes[column * data.rows + row]]; const offset = (row * data.columns + column) * 4; pixels.data[offset] = red; pixels.data[offset + 1] = green; pixels.data[offset + 2] = blue; pixels.data[offset + 3] = 255 }
    context.putImageData(pixels, 0, 0)
    return image
  }
  function drawSpectrogram(context: CanvasRenderingContext2D, top: number, height: number) {
    if (!spectroData || !spectroImage) { context.fillStyle = css.quiet; context.font = '12px "Segoe UI Variable Text", "Segoe UI", sans-serif'; context.fillText('Computing the spectrogram…', 12, top + 20); return }
    const left = frameToX(spectroData.start), right = frameToX(spectroData.end)
    context.imageSmoothingEnabled = true
    context.drawImage(spectroImage, left, top, right - left, height)
    // Frequency scale down the right edge
    context.font = '10px "Segoe UI Variable Text", "Segoe UI", sans-serif'; context.textBaseline = 'middle'; context.textAlign = 'right'
    for (let kilohertz = 0; kilohertz <= spectroData.nyquist / 1000; kilohertz += 5) { const y = top + height - kilohertz * 1000 / spectroData.nyquist * height; context.fillStyle = 'rgba(0,0,0,0.5)'; context.fillRect(width - 34, y - 7, 32, 14); context.fillStyle = '#fff'; context.fillText(`${kilohertz} kHz`, width - 4, y) }
    context.textAlign = 'left'
  }
  $effect(() => { sessionId; totalFrames; width; request<PeakData>({ type: 'peaks', id: sessionId, start: 0, end: Math.max(1, totalFrames), columns: Math.min(4000, Math.ceil(width * devicePixelRatio)) }).then(data => { if (data?.mode === 'peaks') overviewData = data }).catch(() => { }) })

  // Auto height (SPEC.md › Editing): zoomed in, the wave grows to fill the lanes, so quiet starts and fades are easy to see
  function fitHeight(data: PeakData) {
    if (!autoHeight || manualHeight || view.framesPerPixel >= fitFramesPerPixel * 0.98) { if (!manualHeight) verticalScale = 1; return }
    let loudest = 0
    for (const values of data.channels) for (const value of values) { const level = Math.abs(value); if (level > loudest) loudest = level }
    verticalScale = loudest > 0 ? Math.max(minVerticalScale, Math.min(maxVerticalScale, 0.92 / loudest)) : 1
  }
  // Re-fit when the view or data changes; Ctrl+wheel (manualHeight) is left alone (it used to be reset here, so it never stuck)
  $effect(() => { const data = detailData; view.framesPerPixel; autoHeight; untrack(() => { if (data) fitHeight(data) }) })

  // ---- Drawing ----
  function prepare(canvas: HTMLCanvasElement, height: number) {
    const ratio = devicePixelRatio, context = canvas.getContext('2d')!
    if (canvas.width !== Math.round(width * ratio) || canvas.height !== Math.round(height * ratio)) { canvas.width = Math.round(width * ratio); canvas.height = Math.round(height * ratio) }
    context.setTransform(ratio, 0, 0, ratio, 0, 0); context.clearRect(0, 0, width, height)
    return context
  }

  function drawPeaks(context: CanvasRenderingContext2D, data: PeakData, top: number, laneHeight: number, toX: (frame: number) => number, colour: string, scale = 1) {
    const lanes = data.channels.length
    context.fillStyle = colour
    for (let lane = 0; lane < lanes; lane++) {
      const values = data.channels[lane], columns = values.length / 2, middle = top + laneHeight * (lane + 0.5), half = laneHeight / 2 - 1
      const framesPerColumn = (data.end - data.start) / columns
      for (let column = 0; column < columns; column++) {
        const left = toX(data.start + column * framesPerColumn), right = toX(data.start + (column + 1) * framesPerColumn)
        if (right < 0 || left > width) continue
        const low = values[column * 2] * scale, high = values[column * 2 + 1] * scale
        const y1 = middle - Math.max(-1, Math.min(1, high)) * half, y2 = middle - Math.max(-1, Math.min(1, low)) * half
        context.fillRect(left, y1, Math.max(right - left, 1 / devicePixelRatio), Math.max(y2 - y1, 1 / devicePixelRatio))
      }
    }
  }

  function drawSamples(context: CanvasRenderingContext2D, data: PeakData, top: number, laneHeight: number) {
    const pixelsPerSample = 1 / view.framesPerPixel
    for (let lane = 0; lane < data.channels.length; lane++) {
      const samples = data.channels[lane], middle = top + laneHeight * (lane + 0.5), half = laneHeight / 2 - 4
      context.strokeStyle = css.line; context.lineWidth = 1; context.beginPath(); context.moveTo(0, middle); context.lineTo(width, middle); context.stroke() // zero line
      context.strokeStyle = css.wave; context.lineWidth = 1.4; context.beginPath()
      const yOf = (sample: number) => middle - Math.max(-1, Math.min(1, sample * verticalScale)) * half
      samples.forEach((sample, index) => { const x = frameToX(data.start + index), y = yOf(sample); if (index === 0) context.moveTo(x, y); else context.lineTo(x, y) })
      context.stroke()
      // Zero crossings: where the line passes through zero between two samples
      if (pixelsPerSample >= 3) {
        context.fillStyle = css.warn
        for (let index = 1; index < samples.length; index++) {
          const previous = samples[index - 1], current = samples[index]
          if ((previous < 0 && current >= 0) || (previous > 0 && current <= 0)) { const crossing = index - 1 + previous / (previous - current); context.fillRect(frameToX(data.start + crossing) - 0.5, middle - 4, 1, 8) }
        }
      }
      if (pixelsPerSample >= 6) {
        context.fillStyle = css.wave
        samples.forEach((sample, index) => { context.beginPath(); context.arc(frameToX(data.start + index), yOf(sample), Math.min(3.5, pixelsPerSample / 6), 0, Math.PI * 2); context.fill() })
      }
    }
  }

  function drawDetail() {
    if (!detailCanvas) return
    const context = prepare(detailCanvas, detailHeight)
    const waveTop = flagHeight, waveHeight = detailHeight - flagHeight - rulerHeight
    const laneCount = Math.max(1, channels), laneHeight = waveHeight / laneCount
    // The spectrogram goes underneath, so the start and end lines stay on top of it
    if (mode === 'spectrogram') drawSpectrogram(context, waveTop, waveHeight)
    // Tracks: shaded regions, numbered flags, start and end lines
    tracks.forEach((track, index) => {
      const left = frameToX(track.start), right = frameToX(track.end)
      if (right < 0 || left > width) return
      const on = selectedIds.includes(track.id) || track.id === currentId
      if (mode !== 'spectrogram') { context.fillStyle = on ? css.shadeOn : css.shade; context.fillRect(left, waveTop, right - left, waveHeight) }
      context.font = '11px "Segoe UI Variable Text", "Segoe UI", sans-serif'; context.textBaseline = 'middle'
      const label = `${index + 1}${track.title ? '  ' + track.title : ''}`
      context.save(); context.beginPath(); context.rect(Math.max(0, left), 0, Math.max(0, Math.min(width, right) - Math.max(0, left)), flagHeight); context.clip()
      context.fillStyle = on ? css.accent : css.quiet; context.fillText(label, Math.max(0, left) + flagWidth + 4, flagHeight / 2)
      context.restore()
      for (const [edge, x, colour] of [['start', left, css.start], ['end', right, css.end]] as const) {
        if (x < -2 || x > width + 2) continue
        const grabbed = grab?.trackId === track.id && grab.edge === edge
        if (grabbed) { context.fillStyle = colour; context.globalAlpha = 0.1 + 0.3 * (0.5 + 0.5 * Math.sin(performance.now() / 1200 * Math.PI * 2)); context.fillRect(x - 6, 0, 12, waveTop + waveHeight); context.globalAlpha = 1 }
        context.fillStyle = colour; context.fillRect(x - (grabbed ? 1.5 : 0.75), 0, grabbed ? 3 : 1.5, waveTop + waveHeight)
        // The flag: a start's to the right of its line, an end's to the left (grab either by its flag)
        // Linked: a start that meets the previous end is one cut, marked with a small link
        if (linked && edge === 'start' && tracks.some(other => other !== track && other.end === track.start)) { context.save(); context.font = '9px "Segoe UI Emoji", sans-serif'; context.textBaseline = 'top'; context.fillText('🔗', x - 6, flagHeight + 1); context.restore() }
        context.beginPath(); if (edge === 'start') { context.moveTo(x, 0); context.lineTo(x + flagWidth, 0); context.lineTo(x + flagWidth - 4, flagHeight / 2); context.lineTo(x + flagWidth, flagHeight); context.lineTo(x, flagHeight) } else { context.moveTo(x, 0); context.lineTo(x - flagWidth, 0); context.lineTo(x - flagWidth + 4, flagHeight / 2); context.lineTo(x - flagWidth, flagHeight); context.lineTo(x, flagHeight) } context.closePath(); context.fill()
      }
    })
    // Name changes noted while recording
    context.strokeStyle = css.warn; context.setLineDash([2, 4]); context.lineWidth = 1
    for (const change of changes) { const x = frameToX(change.frame); if (x >= 0 && x <= width) { context.beginPath(); context.moveTo(x, waveTop); context.lineTo(x, waveTop + waveHeight); context.stroke() } }
    context.setLineDash([])
    // …each labelled with the name that started there, where there's room
    context.font = '10.5px "Segoe UI Variable Text", "Segoe UI", sans-serif'; context.textBaseline = 'bottom'; context.fillStyle = css.warn
    let labelEnd = -Infinity
    for (const change of changes) {
      const x = frameToX(change.frame); if (x < -200 || x > width) continue
      const label = '♪ ' + (change.title || (change.manual ? 'marked by hand' : 'name changed'))
      if (x + 4 < labelEnd) continue
      context.fillText(label, x + 4, waveTop + waveHeight - 3); labelEnd = x + 4 + context.measureText(label).width + 12
    }
    // Lane dividers and the audio
    context.fillStyle = css.line
    for (let lane = 1; lane < laneCount; lane++) context.fillRect(0, waveTop + lane * laneHeight, width, 1)
    if (mode === 'spectrogram') { }
    else if (detailData?.mode === 'samples' && view.framesPerPixel < 1.5) drawSamples(context, detailData, waveTop, laneHeight)
    else if (detailData) drawPeaks(context, detailData.mode === 'samples' ? toPeaks(detailData) : detailData, waveTop, laneHeight, frameToX, css.wave, verticalScale)
    drawExclusions(context, waveTop, waveHeight)
    if (verticalScale > 1) { context.font = '10.5px "Segoe UI Variable Text", "Segoe UI", sans-serif'; context.textBaseline = 'top'; context.fillStyle = css.quiet; context.fillText(`Height ×${verticalScale < 10 ? verticalScale.toFixed(1) : Math.round(verticalScale)}${manualHeight ? ' (Ctrl+wheel; Auto height resets)' : ' auto'}`, width - 130, waveTop + 4) }
    // Ruler, with any dropouts (samples lost while recording) as red marks on it
    drawRuler(context, detailHeight - rulerHeight)
    context.fillStyle = css.end
    for (const dropout of dropouts) { const x = frameToX(dropout.frame); if (x >= 0 && x <= width) { context.fillRect(x - 1, detailHeight - rulerHeight, Math.max(2, dropout.ms / 1000 * sampleRate / view.framesPerPixel), rulerHeight); } }
    // Cursor and playhead
    const cursorX = frameToX(cursor)
    if (cursorX >= 0 && cursorX <= width) { context.fillStyle = css.accent; context.fillRect(cursorX - 0.5, waveTop, 1, waveHeight); context.beginPath(); context.moveTo(cursorX - 5, detailHeight - rulerHeight); context.lineTo(cursorX + 5, detailHeight - rulerHeight); context.lineTo(cursorX, detailHeight - rulerHeight - 6); context.fill() }
    if (playhead >= 0) { const playX = frameToX(playhead); context.globalAlpha = exclusionAt(playhead) ? 0.35 : 1; if (playX >= 0 && playX <= width) { context.fillStyle = css.playhead; context.fillRect(playX - 1, 0, 2, detailHeight - rulerHeight) } }
  }

  // Left-out sections (SPEC.md › Editing › Leaving out a section): shaded and crossed out, with each fade drawn as its curve.
  // Neutral grey: amber means an unsure cut, and blue is the app's own colour
  const darkSkin = () => { const hex = css.fg.match(/^#([0-9a-f]{2})/i); return hex ? parseInt(hex[1], 16) > 128 : true } // light text means the dark skin
  function drawExclusions(context: CanvasRenderingContext2D, top: number, height: number) {
    for (const exclusion of exclusions) {
      const left = frameToX(exclusion.start), right = frameToX(exclusion.end)
      if (right < 0 || left > width) continue
      const open = exclusion.id === openExclusion
      context.save(); context.beginPath(); context.rect(left, top, right - left, height); context.clip()
      context.fillStyle = darkSkin() ? 'rgba(8,10,14,0.62)' : 'rgba(236,239,244,0.74)'; context.fillRect(left, top, right - left, height)
      context.strokeStyle = css.quiet; context.globalAlpha = open ? 0.75 : 0.5; context.lineWidth = 1
      context.beginPath(); for (let x = left - height; x < right; x += 9) { context.moveTo(x, top + height); context.lineTo(x + height, top) } context.stroke()
      context.restore()
      context.fillStyle = css.quiet; context.fillRect(left - 0.75, top, 1.5, height); context.fillRect(right - 0.75, top, 1.5, height)
      drawHandle(context, left, top, height, css.quiet); drawHandle(context, right, top, height, css.quiet)
      context.font = '10.5px "Segoe UI Variable Text", "Segoe UI", sans-serif'; context.textBaseline = 'top'
      const label = 'Left out', labelWidth = context.measureText(label).width
      if (right - left > labelWidth + 12) { context.fillStyle = darkSkin() ? 'rgba(8,10,14,0.85)' : 'rgba(255,255,255,0.85)'; const labelX = Math.max(left, 0) + Math.min(right - left, width) / 2 - labelWidth / 2; context.fillRect(labelX - 5, top + 4, labelWidth + 10, 15); context.fillStyle = css.fg; context.fillText(label, labelX, top + 6) }
      // Fades: the gain curve over the audio either side
      context.strokeStyle = css.fg; context.globalAlpha = 0.7; context.lineWidth = 1.2
      for (const [from, to, fadingOut] of [[exclusion.start - exclusion.fadeOutMs * sampleRate / 1000, exclusion.start, true], [exclusion.end, exclusion.end + exclusion.fadeInMs * sampleRate / 1000, false]] as const) {
        if (to - from <= 0) continue
        const fromX = frameToX(from), toX = frameToX(to); if (toX < 0 || fromX > width) continue
        context.beginPath()
        for (let step = 0; step <= 24; step++) { const position = step / 24, gain = Math.sin(Math.PI / 2 * (fadingOut ? 1 - position : position)); const x = fromX + (toX - fromX) * position, y = top + 3 + (1 - gain) * (height - 6); if (step === 0) context.moveTo(x, y); else context.lineTo(x, y) }
        context.stroke()
      }
      context.globalAlpha = 1
    }
    // The section being picked (click, then Shift+click), with a handle on each end
    if (selection) { const left = frameToX(selection.start), right = frameToX(selection.end); context.fillStyle = css.accent; context.globalAlpha = 0.16; context.fillRect(left, top, right - left, height); context.globalAlpha = 1; context.fillRect(left - 0.5, top, 1, height); context.fillRect(right - 0.5, top, 1, height); drawHandle(context, left, top, height, css.accent); drawHandle(context, right, top, height, css.accent) }
  }
  // A grip on a section's edge: a small rounded tab across the line, halfway down
  function drawHandle(context: CanvasRenderingContext2D, x: number, top: number, height: number, colour: string) {
    if (x < -6 || x > width + 6) return
    const tabHeight = Math.min(26, height * 0.4), y = top + height / 2 - tabHeight / 2
    context.fillStyle = colour; context.beginPath(); context.roundRect(x - 4, y, 8, tabHeight, 3); context.fill()
    context.fillStyle = darkSkin() ? 'rgba(8,10,14,0.7)' : 'rgba(255,255,255,0.85)'; for (const offset of [-4, 0, 4]) context.fillRect(x - 2, y + tabHeight / 2 + offset - 0.5, 4, 1)
  }

  // Sample data seen from further out (zooming out before new data arrives)
  function toPeaks(data: PeakData): PeakData { return { mode: 'peaks', start: data.start, end: data.start + data.channels[0].length, channels: data.channels.map(samples => samples.flatMap(sample => [sample, sample])) } }

  function drawRuler(context: CanvasRenderingContext2D, top: number) {
    context.fillStyle = css.line; context.fillRect(0, top, width, 1)
    const secondsPerPixel = view.framesPerPixel / sampleRate
    const steps = [0.0001, 0.0002, 0.0005, 0.001, 0.002, 0.005, 0.01, 0.02, 0.05, 0.1, 0.2, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600]
    const step = steps.find(candidate => candidate / secondsPerPixel >= 90) ?? 3600
    const firstSecond = view.start / sampleRate, lastSecond = (view.start + width * view.framesPerPixel) / sampleRate
    context.font = '10px "Segoe UI Variable Text", "Segoe UI", sans-serif'; context.textBaseline = 'top'; context.fillStyle = css.quiet
    for (let second = Math.ceil(firstSecond / step) * step; second <= lastSecond; second += step) {
      const x = frameToX(second * sampleRate)
      context.fillRect(x, top, 1, 5)
      const label = step < 0.001 ? `${formatTime(second * sampleRate, sampleRate)} · ${Math.round(second * sampleRate)}` : formatTime(second * sampleRate, sampleRate, step < 1)
      context.fillText(label, x + 3, top + 5)
    }
  }

  function drawOverview() {
    if (!overviewCanvas) return
    const context = prepare(overviewCanvas, overviewHeight)
    const toX = (frame: number) => frame / Math.max(1, totalFrames) * width
    context.fillStyle = css.panel; context.fillRect(0, 0, width, overviewHeight)
    tracks.forEach(track => { const on = selectedIds.includes(track.id) || track.id === currentId; context.fillStyle = on ? css.shadeOn : css.shade; context.fillRect(toX(track.start), 0, Math.max(1, toX(track.end) - toX(track.start)), overviewHeight); context.fillStyle = css.start; context.fillRect(toX(track.start), 0, 1, overviewHeight) })
    if (overviewData) drawPeaks(context, overviewData, 2, (overviewHeight - 4) / Math.max(1, overviewData.channels.length), toX, css.waveDim)
    for (const exclusion of exclusions) { const left = toX(exclusion.start); context.fillStyle = darkSkin() ? 'rgba(8,10,14,0.62)' : 'rgba(236,239,244,0.78)'; context.fillRect(left, 0, Math.max(2, toX(exclusion.end) - left), overviewHeight) }
    // The part shown below
    const left = toX(view.start), right = toX(view.start + width * view.framesPerPixel)
    context.strokeStyle = css.accent; context.lineWidth = 1.5; context.strokeRect(left + 0.75, 0.75, Math.max(3, right - left) - 1.5, overviewHeight - 1.5)
    if (playhead >= 0) { context.fillStyle = css.playhead; context.fillRect(toX(playhead) - 0.5, 0, 1, overviewHeight) }
  }

  // A grabbed edge pulses: redraw on every frame while one is grabbed
  $effect(() => { if (!grab) return; let frame = 0; const loop = () => { drawDetail(); frame = requestAnimationFrame(loop) }; frame = requestAnimationFrame(loop); return () => cancelAnimationFrame(frame) })
  const exclusionsKey = $derived(exclusions.map(exclusion => `${exclusion.start}-${exclusion.end}-${exclusion.fadeOutMs}-${exclusion.fadeInMs}`).join())
  $effect(() => { detailData; spectroData; mode; tracks; exclusionsKey; selection; openExclusion; changes; dropouts; selectedIds; currentId; cursor; playhead; grab; view.start; view.framesPerPixel; width; detailHeight; css; channels; verticalScale; requestAnimationFrame(drawDetail) })
  $effect(() => { overviewData; tracks; exclusionsKey; selectedIds; currentId; playhead; view.start; view.framesPerPixel; width; css; requestAnimationFrame(drawOverview) })

  // ---- Mouse ----
  export function zoomAt(factor: number, x: number) { const frame = xToFrame(x); view = clampView({ framesPerPixel: view.framesPerPixel * factor, start: frame - x * view.framesPerPixel * factor }) }
  function onWheel(event: WheelEvent) {
    event.preventDefault()
    const x = event.offsetX
    if (event.shiftKey || Math.abs(event.deltaX) > Math.abs(event.deltaY)) { const delta = (Math.abs(event.deltaX) > Math.abs(event.deltaY) ? event.deltaX : event.deltaY); view = clampView({ start: view.start + delta * view.framesPerPixel, framesPerPixel: view.framesPerPixel }); return }
    if (event.ctrlKey) { manualHeight = true; verticalScale = Math.max(minVerticalScale, Math.min(maxVerticalScale, verticalScale * Math.pow(1.0025, -event.deltaY))); return }
    zoomAt(Math.pow(1.0018, event.deltaY), x)
  }

  // A handle near the pointer (start/end lines, within 6 px; the flags on top are easier to catch)
  // In the flag row a start's flag sticks out to the right of its line and an end's to the left, so a start and an end that meet
  // can each be picked by their own flag (SPEC.md › Editing)
  const flagWidth = 16
  function handleAt(x: number, y: number): Grab {
    if (y < flagHeight) {
      let picked: Grab = null, nearest = Infinity
      for (const track of tracks) for (const edge of ['start', 'end'] as const) {
        const lineX = frameToX(track[edge]), offset = edge === 'start' ? x - lineX : lineX - x
        if (offset >= -1 && offset <= flagWidth && offset < nearest) { nearest = offset; picked = { trackId: track.id, edge } }
      }
      if (picked) return picked
    }
    let best: Grab = null, bestDistance = y < flagHeight ? 9 : 6
    for (const track of tracks) for (const edge of ['start', 'end'] as const) { const distance = Math.abs(frameToX(track[edge]) - x); if (distance < bestDistance) { bestDistance = distance; best = { trackId: track.id, edge } } }
    return best
  }
  // Left-out sections: click places the cursor, Shift+click picks from the cursor to there (hold and move to adjust; plain drag still moves
  // the wave); a handle on either end of the pick or of a left-out section drags it, scrolling past the wave's edges; a click opens one
  let selection = $state<{ start: number; end: number } | null>(null), openExclusion = $state('')
  export function cancelSelection() { const had = !!selection || !!openExclusion; selection = null; openExclusion = ''; return had }
  function exclusionEdgeAt(x: number, y: number): { id: string; edge: 'start' | 'end' } | null {
    if (y < flagHeight) return null
    let best: { id: string; edge: 'start' | 'end' } | null = null, bestDistance = 6
    for (const exclusion of exclusions) for (const edge of ['start', 'end'] as const) { const distance = Math.abs(frameToX(exclusion[edge]) - x); if (distance < bestDistance) { bestDistance = distance; best = { id: exclusion.id, edge } } }
    return best
  }
  const exclusionAt = (frame: number) => exclusions.find(exclusion => frame >= exclusion.start && frame < exclusion.end)
  function selectionEdgeAt(x: number, y: number) {
    if (!selection || y < flagHeight) return null
    const toStart = Math.abs(frameToX(selection.start) - x), toEnd = Math.abs(frameToX(selection.end) - x)
    return Math.min(toStart, toEnd) < 7 ? (toStart <= toEnd ? 'start' : 'end') : null
  }
  // Dragging a pick or a left-out section's edge follows the pointer, and past either side of the wave it scrolls
  let lastPointerX = 0, edgeScrollFrame = 0
  function dragTo(x: number) {
    if (!drag) return
    const frame = Math.round(Math.max(0, Math.min(totalFrames, xToFrame(Math.max(0, Math.min(width, x))))))
    if (drag.kind === 'select') { if (Math.abs(x - drag.startX) > 3) drag.moved = true; selection = frame === drag.anchor ? null : { start: Math.min(drag.anchor, frame), end: Math.max(drag.anchor, frame) } }
    else if (drag.kind === 'exclusion') {
      if (Math.abs(x - drag.startX) > 3) drag.moved = true
      const draggedId = drag.id, exclusion = exclusions.find(candidate => candidate.id === draggedId)
      if (drag.moved && exclusion) { if (drag.edge === 'start') onExclusionMove?.(exclusion.id, Math.min(frame, exclusion.end - 1), exclusion.end); else onExclusionMove?.(exclusion.id, exclusion.start, Math.max(frame, exclusion.start + 1)) }
    }
  }
  function edgeScroll() {
    cancelAnimationFrame(edgeScrollFrame)
    if (drag?.kind !== 'select' && drag?.kind !== 'exclusion') return
    const beyond = lastPointerX < 0 ? lastPointerX : lastPointerX > width ? lastPointerX - width : 0
    if (!beyond) return
    view = clampView({ start: view.start + Math.max(-60, Math.min(60, beyond)) * view.framesPerPixel * 0.3, framesPerPixel: view.framesPerPixel })
    dragTo(lastPointerX); edgeScrollFrame = requestAnimationFrame(edgeScroll)
  }
  let drag: null | { kind: 'select'; startX: number; anchor: number; moved: boolean } | { kind: 'exclusion'; id: string; edge: 'start' | 'end'; startX: number; moved: boolean } | { kind: 'edge'; grab: NonNullable<Grab>; startX: number; wasGrabbed: boolean } | { kind: 'pan'; startX: number; startView: number; moved: boolean } | { kind: 'pin'; grab: NonNullable<Grab>; pinX: number; startX: number; startView: number } = null
  let hoverCursor = $state('default'), hoverTitle = $state('')
  // Hovering a dotted line says what it is
  function changeTitle(x: number) { const dropout = dropouts.find(candidate => Math.abs(frameToX(candidate.frame) - x) < 6); if (dropout) return `Dropout: about ${dropout.ms} ms of audio was lost here because the PC was too busy`; const change = changes.find(candidate => Math.abs(frameToX(candidate.frame) - x) < 5); return change ? `Where the playing name changed while recording${change.title ? ` to “${change.title}”${change.artist ? ` by ${change.artist}` : ''}` : ''}. The track's green start snaps to the quietest point near it.` : '' }
  function onDown(event: PointerEvent) {
    if (event.button !== 0) return
    detailCanvas?.setPointerCapture(event.pointerId)
    const exclusionEdge = exclusionEdgeAt(event.offsetX, event.offsetY), selectionEdge = selectionEdgeAt(event.offsetX, event.offsetY)
    lastPointerX = event.offsetX
    // An end of the pick: drag it (the other end stays)
    if (selection && selectionEdge) { drag = { kind: 'select', startX: event.offsetX, anchor: selectionEdge === 'start' ? selection.end : selection.start, moved: false }; return }
    // Shift+click: from the cursor to here, backwards or forwards; keep holding and move to adjust
    if (event.shiftKey && event.offsetY >= flagHeight && !exclusionEdge) { openExclusion = ''; drag = { kind: 'select', startX: event.offsetX, anchor: cursor, moved: true }; dragTo(event.offsetX); return }
    if (exclusionEdge) { drag = { kind: 'exclusion', id: exclusionEdge.id, edge: exclusionEdge.edge, startX: event.offsetX, moved: false }; return }
    const handle = handleAt(event.offsetX, event.offsetY)
    if (handle) { drag = { kind: 'edge', grab: handle, startX: event.offsetX, wasGrabbed: grab?.trackId === handle.trackId && grab.edge === handle.edge }; onEdge(handle, tracks.find(track => track.id === handle.trackId)![handle.edge]); return }
    // A grabbed edge stays put on screen while the audio is dragged under it (SPEC.md › Editing): the easy way to place a cut roughly
    const pinnedTrack = grab ? tracks.find(track => track.id === grab!.trackId) : null
    if (grab && pinnedTrack && event.offsetY >= flagHeight) { const pinX = frameToX(pinnedTrack[grab.edge]); if (pinX >= 0 && pinX <= width) { drag = { kind: 'pin', grab, pinX, startX: event.offsetX, startView: view.start }; onEdge(grab, pinnedTrack[grab.edge]); return } }
    drag = { kind: 'pan', startX: event.offsetX, startView: view.start, moved: false }
  }
  function onMove(event: PointerEvent) {
    if (!drag) {
      const exclusionEdge = exclusionEdgeAt(event.offsetX, event.offsetY), inside = event.offsetY >= flagHeight ? exclusionAt(xToFrame(event.offsetX)) : undefined
      if (exclusionEdge) { hoverCursor = 'ew-resize'; hoverTitle = 'Edge of a left-out section: drag to move it'; return }
      if (selectionEdgeAt(event.offsetX, event.offsetY)) { hoverCursor = 'ew-resize'; hoverTitle = 'End of the selection: drag to move it'; return }
      if (event.shiftKey && event.offsetY >= flagHeight) { hoverCursor = 'crosshair'; hoverTitle = 'Shift+click: select from the cursor to here (hold and move to adjust)'; return }
      if (inside && !handleAt(event.offsetX, event.offsetY)) { hoverCursor = 'pointer'; hoverTitle = 'Left out: playing skips this and exports leave it out. Click it for fades, or to include it again'; return }
    }
    if (drag?.kind === 'select' || drag?.kind === 'exclusion') { lastPointerX = event.offsetX; dragTo(event.offsetX); edgeScroll(); return }
    if (!drag) { const handle = handleAt(event.offsetX, event.offsetY); hoverCursor = handle ? 'ew-resize' : grab && event.offsetY >= flagHeight ? 'grab' : 'text'; hoverTitle = handle ? (grab?.trackId === handle.trackId && grab.edge === handle.edge ? 'Grabbed: drag the audio under it, or click it to let go' : handle.edge === 'start' ? 'Track start: click to grab it, or drag to move' : 'Track end: click to grab it, or drag to move') : changeTitle(event.offsetX); return }
    if (drag.kind === 'edge') { if (Math.abs(event.offsetX - drag.startX) < 3) return; onEdge(drag.grab, Math.round(Math.max(0, Math.min(totalFrames, xToFrame(event.offsetX))))); return }
    if (drag.kind === 'pin') { hoverCursor = 'grabbing'; view = clampView({ start: drag.startView - (event.offsetX - drag.startX) * view.framesPerPixel, framesPerPixel: view.framesPerPixel }); onEdge(drag.grab, Math.round(Math.max(0, Math.min(totalFrames, xToFrame(drag.pinX))))); return }
    if (Math.abs(event.offsetX - drag.startX) > 3) drag.moved = true
    if (drag.moved) { hoverCursor = 'grabbing'; view = clampView({ start: drag.startView - (event.offsetX - drag.startX) * view.framesPerPixel, framesPerPixel: view.framesPerPixel }) }
  }
  function onUp(event: PointerEvent) {
    const finished = drag; drag = null
    if (!finished) return
    cancelAnimationFrame(edgeScrollFrame)
    if (finished.kind === 'select') return
    if (finished.kind === 'exclusion') { if (finished.moved) onExclusionDone?.(finished.id); else openExclusion = finished.id; return }
    // Clicking the grabbed line again (without moving it) lets go of it
    if (finished.kind === 'edge' && finished.wasGrabbed && Math.abs(event.offsetX - finished.startX) < 3 && onRelease) { hoverCursor = 'ew-resize'; onRelease(); return }
    if (finished.kind === 'edge' || finished.kind === 'pin') { hoverCursor = 'grab'; onEdgeDone(finished.grab); return }
    if (!finished.moved) {
      const frame = Math.round(Math.max(0, Math.min(totalFrames, xToFrame(event.offsetX))))
      selection = null; openExclusion = event.offsetY >= flagHeight ? exclusionAt(frame)?.id ?? '' : ''
      onCursor(frame)
      const track = tracks.find(candidate => frame >= candidate.start && frame < candidate.end)
      if (track && event.offsetY >= flagHeight) onPickTrack(track.id, event.ctrlKey)
    }
    hoverCursor = 'text'
  }
  function onDoubleClick(event: MouseEvent) { const frame = xToFrame(event.offsetX); const track = tracks.find(candidate => frame >= candidate.start && frame < candidate.end); if (track) onZoomTrack(track.id) }

  // The pop-up sits over the middle of its section, kept inside the wave
  const popLeft = (start: number, end: number) => Math.max(8, Math.min(width - 330, (frameToX(start) + frameToX(end)) / 2 - 100))

  // Overview: click or drag to move the view there
  let overviewDragging = false
  function centreOn(event: PointerEvent) { const frame = event.offsetX / width * totalFrames; view = clampView({ start: frame - width * view.framesPerPixel / 2, framesPerPixel: view.framesPerPixel }) }
</script>

<div class="waveform" bind:clientWidth={width}>
  <canvas class="overview" bind:this={overviewCanvas} style:height="{overviewHeight}px" onpointerdown={(event) => { overviewDragging = true; overviewCanvas?.setPointerCapture(event.pointerId); centreOn(event) }} onpointermove={(event) => { if (overviewDragging) centreOn(event) }} onpointerup={() => overviewDragging = false} onwheel={onWheel}></canvas>
  <div class="detail" bind:clientHeight={detailHeight}>
    <canvas bind:this={detailCanvas} style:cursor={hoverCursor} title={hoverTitle} onwheel={onWheel} onpointerdown={onDown} onpointermove={onMove} onpointerup={onUp} ondblclick={onDoubleClick}></canvas>
    {#if selection}
      <div class="wave-pop" style:left="{popLeft(selection.start, selection.end)}px" style:top="{flagHeight + 6}px">
        <span class="pop-length">{formatLength(selection.end - selection.start, sampleRate)}</span>
        <button class="primary pop-button" onclick={() => { if (selection) onExclude?.(selection.start, selection.end); selection = null }} title="Leave this section out: playing skips it and exports leave it out. Nothing is removed from the recording">Exclude</button>
        <button class="ghost pop-close" onclick={() => selection = null} aria-label="Cancel" title="Cancel (Esc)">×</button>
      </div>
    {:else if openExclusion && exclusions.find(exclusion => exclusion.id === openExclusion)}
      {@const exclusion = exclusions.find(candidate => candidate.id === openExclusion)!}
      <div class="wave-pop" style:left="{popLeft(exclusion.start, exclusion.end)}px" style:top="{flagHeight + 6}px">
        <span class="pop-length">Left out · {formatLength(exclusion.end - exclusion.start, sampleRate)}</span>
        <label title="Fades out over this long just before the left-out section">Fade out <input type="number" min="0" max="20000" step="50" value={exclusion.fadeOutMs} onchange={(event) => onExclusionFades?.(exclusion.id, Math.max(0, Math.round(Number((event.target as HTMLInputElement).value) || 0)), exclusion.fadeInMs)} /> ms</label>
        <label title="Fades in over this long just after the left-out section">Fade in <input type="number" min="0" max="20000" step="50" value={exclusion.fadeInMs} onchange={(event) => onExclusionFades?.(exclusion.id, exclusion.fadeOutMs, Math.max(0, Math.round(Number((event.target as HTMLInputElement).value) || 0)))} /> ms</label>
        <Dropdown label="Audition" value="▶" align="left" options={[['join', 'Hear the join'], ['section', 'Play the excluded section'], ['from-start', 'Play from start of excluded section'], ['to-end', 'Play to end of excluded section']]} onchange={(choice) => onAuditionSection?.(exclusion.id, choice as SectionAudition)} />
        <button class="ghost pop-button" onclick={() => { onExclusionRemove?.(exclusion.id); openExclusion = '' }} title="Put this section back: it plays and exports again">Include again</button>
        <button class="ghost pop-close" onclick={() => openExclusion = ''} aria-label="Close" title="Close (Esc)">×</button>
      </div>
    {/if}
    {#if showHint && !selection}<div class="wave-hint" style:bottom="{rulerHeight + 4}px">Click, then Shift+click to select a section to leave out</div>{/if}
  </div>
</div>

<style>
  .waveform { display: flex; flex-direction: column; gap: 6px; min-height: 0; height: 100%; }
  canvas { display: block; width: 100%; touch-action: none; }
  .overview { border-radius: 6px; cursor: pointer; flex: none; }
  .detail { flex: 1; min-height: 120px; position: relative; border-radius: 8px; background: var(--panel); overflow: hidden; }
  .detail canvas { position: absolute; inset: 0; height: 100%; }
  .detail:has(.wave-pop) { overflow: visible; }
  .wave-pop { position: absolute; display: flex; align-items: center; gap: 8px; padding: 5px 6px 5px 10px; border-radius: 8px; background: var(--panel-solid); border: 1px solid var(--line); box-shadow: 0 6px 20px var(--shadow); font-size: 12px; white-space: nowrap; z-index: 2; }
  .wave-pop label { display: flex; align-items: center; gap: 4px; color: var(--fg-quiet); }
  .wave-pop input { width: 58px; padding: 1px 4px; font: inherit; }
  .pop-length { color: var(--fg-quiet); font-variant-numeric: tabular-nums; }
  .pop-button { padding: 2px 10px; font-size: 12px; }
  .pop-close { padding: 0 6px; font-size: 15px; line-height: 1; }
  .wave-hint { position: absolute; right: 42px; font-size: 11px; color: var(--fg-quiet); opacity: 0.75; pointer-events: none; }
</style>
