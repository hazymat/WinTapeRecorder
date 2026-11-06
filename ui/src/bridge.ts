// WebView2 message bridge to the C# shell. Outside WebView2 (vite dev) messages are only logged.
export type ShellMessage = { type: string; [key: string]: any }
const webview = (window as any).chrome?.webview
let nextRequestId = 1
const pendingRequests = new Map<number, (data: any) => void>()
const handlersByType = new Map<string, ((message: ShellMessage) => void)[]>()

webview?.addEventListener('message', (event: MessageEvent) => {
  const message = event.data as ShellMessage
  if (message.type === 'result') { pendingRequests.get(message.id)?.(message.data); pendingRequests.delete(message.id); return }
  for (const handler of handlersByType.get(message.type) ?? []) handler(message)
})

export function sendToShell(message: ShellMessage) { if (webview) webview.postMessage(message); else console.log('[bridge]', message) }
export function onShellMessage(type: string, handler: (message: ShellMessage) => void) { handlersByType.set(type, (handlersByType.get(type) ?? []).concat([handler])) }
export function request<T = any>(message: ShellMessage): Promise<T> {
  const requestId = nextRequestId++
  return new Promise((resolve, reject) => {
    pendingRequests.set(requestId, data => { if (data && typeof data === 'object' && 'error' in data && Object.keys(data).length === 1) reject(new Error(data.error)); else resolve(data) })
    sendToShell(Object.assign({ requestId }, message))
  })
}
