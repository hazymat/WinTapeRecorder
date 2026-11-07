// Shared UI state: what the shell reports, the open view, messages with Undo
export type Track = { id: string; start: number; end: number; artist: string; title: string; album: string; albumArtist?: string; genre?: string; year?: string; composer?: string; comment?: string; trackNumber?: number; trackCount?: number; artPath?: string; fadeInMs: number; fadeOutMs: number; exported: boolean; exportedPath: string }
export type NameChange = { frame: number; artist: string; title: string; album: string; source: string; manual: boolean }
export type SessionSummary = { id: string; name: string; created: string; seconds: number; tracks: number; exported: number; recording: boolean; crashed: boolean }
export type SessionDetail = { id: string; name: string; created: string; device: string; loopback: boolean; sampleRate: number; channels: number; bitDepth: number; totalFrames: number; recordingProfileId: string; outputProfileId: string; clippedSamples: number; changes: NameChange[]; dropouts: { frame: number; ms: number }[]; tracks: Track[]; folder: string }
export type Profile = { id: string; name: string; loopback?: boolean; deviceName?: string; bitDepth?: number; format?: string; template?: string }
export type NameSource = { key: string; kind: string; label: string; artist: string; title: string; playing: boolean; rule: string }

export const app = $state({
  status: null as null | { recording: boolean; capturing: boolean; sessionId: string; sessionName: string; device: string; deviceVolume: number; dropouts: number; sampleRate: number; changes: { frame: number; artist: string; title: string; manual: boolean }[]; current: null | { label: string; artist: string; title: string }; source: string; playing: boolean; playingSession: string; exporting: boolean; version: string; updateReady: string },
  levels: { peaks: [] as number[], clipped: 0, seconds: 0, bytes: 0, minutesLeft: -1 },
  hints: { hidden: false, dismissed: [] as string[] },
  recordingProfiles: [] as Profile[], outputProfiles: [] as Profile[], activeRecording: '', activeOutput: '', auditionSeconds: 5,
  sources: [] as NameSource[],
  sessions: [] as SessionSummary[],
  window: { glass: false, frameless: false, maximised: false },
  playhead: -1,
})

// The open view: the recorder, or one session
export const nav = $state({ view: 'record' as 'record' | 'session', sessionId: readSetting('session', '') })

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
export function formatTime(frames: number, rate: number, withMs = true) {
  if (!rate || frames < 0) return withMs ? '0:00.000' : '0:00'
  const totalMs = Math.round(frames / rate * 1000)
  const hours = Math.floor(totalMs / 3600000), minutes = Math.floor(totalMs / 60000) % 60, seconds = Math.floor(totalMs / 1000) % 60, ms = totalMs % 1000
  const base = (hours ? `${hours}:${String(minutes).padStart(2, '0')}` : `${minutes}`) + `:${String(seconds).padStart(2, '0')}`
  return withMs ? `${base}.${String(ms).padStart(3, '0')}` : base
}
export function formatDuration(seconds: number) { const whole = Math.round(seconds), hours = Math.floor(whole / 3600), minutes = Math.floor(whole / 60) % 60; return hours ? `${hours} h ${minutes} min` : `${minutes}:${String(whole % 60).padStart(2, '0')}` }
