// Shared UI state: what the shell reports, the open view, messages with Undo
export type Track = { note?: string; id: string; start: number; end: number; detectedStart?: number; detectedEnd?: number; artist: string; title: string; album: string; albumArtist?: string; genre?: string; year?: string; composer?: string; comment?: string; trackNumber?: number; trackCount?: number; artPath?: string; fadeInMs: number; fadeOutMs: number; exported: boolean; exportedPath: string; startNote?: string; endNote?: string; startSure?: boolean; endSure?: boolean; startChecked?: boolean; endChecked?: boolean; skip?: boolean; skipReason?: string; verified?: boolean; offAlbum?: boolean; outputProfileId?: string; discNumber?: number; playerArtPath?: string; suggestion?: TagSuggestion | null; suggestionDismissed?: boolean; suggestionLooked?: boolean; verifyError?: string }
export type NameChange = { frame: number; artist: string; title: string; album: string; source: string; manual: boolean }
export type SessionSummary = { id: string; name: string; created: string; seconds: number; tracks: number; exported: number; verified: number; recording: boolean; crashed: boolean }
export type SessionDetail = { id: string; name: string; created: string; device: string; loopback: boolean; sampleRate: number; channels: number; bitDepth: number; totalFrames: number; recordingProfileId: string; outputProfileId: string; clippedSamples: number; changes: NameChange[]; dropouts: { frame: number; ms: number }[]; clips: { frame: number; samples: number; changeId?: string }[]; quality: { frame: number; hz: number }[]; albumCheck?: AlbumCheck | null; cutOff?: boolean; otherSounds?: { changeId?: string; frame: number; ms: number; app: string; peakDb: number }[]; albums?: AlbumMatch[]; albumLooked?: boolean; tracks: Track[]; folder: string }
export type Profile = { id: string; name: string; loopback?: boolean; deviceName?: string; bitDepth?: number; format?: string; template?: string; trimSilence?: boolean; endsMeet?: boolean; bitrateKbps?: number }
export type TagSuggestion = { source: string; title: string; artist: string; album: string; albumArtist: string; year: string; genre: string; trackNumber: number; trackCount: number; artUrl: string; releaseId: string }
export type AlbumMatch = { source: string; releaseId: string; album: string; albumArtist: string; year: string; genre: string; artUrl: string; detail: string; trackTitles: string[]; kept: string[]; trackIds: string[] }
export type AlbumCheck = { source: string; album: string; artist: string; total: number; have: number; missing: string[] }
export type NameSource = { key: string; kind: string; app?: string; label: string; artist: string; title: string; album?: string; albumArtist?: string; trackNumber?: number; trackCount?: number; subtitle?: string; genres?: string; picture?: boolean; playing: boolean; rule: string }

export const app = $state({
  status: null as null | { appendFrom?: number; diskDrive?: string; diskFreeBytes?: number; recording: boolean; capturing: boolean; sessionId: string; sessionName: string; device: string; deviceVolume: number; dropouts: number; cutoffHz: number | null; cutoffText: string; flacRatio: number; bitDepth: number; channels: number; sampleRate: number; changes: { id?: string; frame: number; detectedFrame?: number; artist: string; title: string; album: string; year?: string; genre?: string; composer?: string; confirmed?: boolean; manual: boolean; problem: string; note?: string }[]; dropoutList: { frame: number; ms: number; changeId?: string }[]; gapList?: { frame: number; ms: number; changeId?: string }[]; otherSounds?: { changeId?: string; frame: number; ms: number; app: string; peakDb: number }[]; clips: { frame: number; samples: number; changeId?: string }[]; current: null | { key?: string; label: string; artist: string; title: string; album?: string; year?: string }; source: string; playing: boolean; playingSession: string; exporting: boolean; exportingSession: string; exportDone: number; exportTotal: number; exportQueue?: string[]; waitingForSound?: boolean; paused?: boolean; version: string; updateReady: string },
  levels: { peaks: [] as number[], clipped: 0, seconds: 0, bytes: 0, minutesLeft: -1, silentSeconds: 0, silenceStopIn: -1 },
  hints: { hidden: false, dismissed: [] as string[] },
  recordingProfiles: [] as Profile[], outputProfiles: [] as Profile[], activeRecording: '', activeOutput: '', auditionSeconds: 5,
  sources: [] as NameSource[],
  sessions: [] as SessionSummary[],
  window: { glass: false, frameless: false, maximised: false },
  playhead: -1,
})

// The open view: the recorder, or one session
export const nav = $state({ view: 'record' as 'record' | 'session', sessionId: readSetting('session', ''), step: 'cuts' as 'cuts' | 'tags' | 'export', wantStep: '' as '' | 'cuts' | 'tags' | 'export' })
// The tour (docs/TOUR.md): open or paused, which tab, and how far it got on each screen
export const tour = $state({ open: false, tab: 'concept' as 'concept' | 'starts' | 'show' | 'explore', index: {} as Record<string, number> })

export function readSetting(name: string, fallback: string) { try { return localStorage.getItem('wtr.' + name) ?? fallback } catch { return fallback } }
export function writeSetting(name: string, value: string) { try { localStorage.setItem('wtr.' + name, value) } catch { } }

export const ui = $state({ skin: readSetting('skin', 'auto'), accent: readSetting('accent', ''), glassTransparency: Number(readSetting('glassTransparency', '-1')), glassTintHue: Number(readSetting('glassTintHue', '0')), lookVersion: 0, toast: null as null | Toast })
type Toast = { text: string; undo?: () => unknown; action?: { label: string; key: string; run: () => unknown }; ms: number; id: number }
let toastTimer = 0, toastId = 0
export function toast(text: string, options: { undo?: () => unknown; action?: Toast['action']; ms?: number } = {}) {
  const ms = options.ms ?? (options.undo || options.action ? 7000 : 3500)
  ui.toast = { text, undo: options.undo, action: options.action, ms, id: ++toastId }
  clearTimeout(toastTimer); toastTimer = window.setTimeout(() => ui.toast = null, ms)
}
export function undoToast() { const undo = ui.toast?.undo; ui.toast = null; undo?.() }

// Time as h:mm:ss.mmm (or m:ss.mmm), from frames
// A track length; under a second it's almost certainly a blip, so it shows ! and milliseconds
export function formatLength(frames: number, rate: number) {
  return frames < rate ? `! ${Math.round(frames / rate * 1000)} ms` : formatTime(frames, rate, false)
}

export function formatTime(frames: number, rate: number, withMs = true) {
  if (!rate || frames < 0) return withMs ? '0:00.000' : '0:00'
  const totalMs = Math.round(frames / rate * 1000)
  const hours = Math.floor(totalMs / 3600000), minutes = Math.floor(totalMs / 60000) % 60, seconds = Math.floor(totalMs / 1000) % 60, ms = totalMs % 1000
  const base = (hours ? `${hours}:${String(minutes).padStart(2, '0')}` : `${minutes}`) + `:${String(seconds).padStart(2, '0')}`
  return withMs ? `${base}.${String(ms).padStart(3, '0')}` : base
}
// Source quality (SPEC.md › Recording): what a spectral cutoff usually means (the same bands as the shell's SpectralCutoff.Describe)
export function describeCutoff(hz: number | null | undefined) {
  if (!hz) return ''
  return hz >= 20500 ? 'full bandwidth: lossless, or a very high bitrate' : hz >= 19000 ? 'about 256–320 kbps, or lossless with a gentle top end' : hz >= 16500 ? 'about 192 kbps' : hz >= 14000 ? 'about 128 kbps' : hz >= 11000 ? 'about 96 kbps' : '64 kbps or lower'
}
export const shortCutoff = (hz: number | null | undefined) => hz ? `${(hz / 1000).toFixed(1)}k` : ''
// The typical cutoff over a stretch of readings: the median of the ones that heard anything
export function medianCutoff(samples: { frame: number; hz: number }[], start: number, end: number) {
  const heard = samples.filter(sample => sample.frame >= start && sample.frame < end && sample.hz > 0).map(sample => sample.hz).sort((first, second) => first - second)
  return heard.length ? heard[Math.floor(heard.length / 2)] : null
}
export function formatDuration(seconds: number) { const whole = Math.round(seconds), hours = Math.floor(whole / 3600), minutes = Math.floor(whole / 60) % 60; return hours ? `${hours} h ${minutes} min` : `${minutes}:${String(whole % 60).padStart(2, '0')}` }
