import type { ReplayEvent, AppendEvent } from './contracts'

export type SseEventMap = {
  'replay': ReplayEvent
  'append': AppendEvent
}

type SseCallbacks = {
  [K in keyof SseEventMap]?: (payload: SseEventMap[K]) => void
}

export interface SseClientOptions {
  onConnectionLost?: () => void
  onReconnected?: () => void
}

const PAUSE_QUEUE_MAX = 100

export class SseClient {
  private es: EventSource | null = null
  private lastEventId: string | null = null
  private callbacks: SseCallbacks = {}
  private paused = false
  private pauseQueue: Array<{ type: string; data: unknown }> = []
  private closed = false
  private onConnectionLost: (() => void) | undefined
  private onReconnected: (() => void) | undefined

  constructor(options: SseClientOptions = {}) {
    this.onConnectionLost = options.onConnectionLost
    this.onReconnected = options.onReconnected
    this.connect()
  }

  on<K extends keyof SseEventMap>(type: K, cb: (payload: SseEventMap[K]) => void): this {
    this.callbacks[type] = cb as SseCallbacks[K]
    return this
  }

  pause(): void {
    this.paused = true
  }

  resume(): void {
    this.paused = false
    const queued = this.pauseQueue.splice(0)
    for (const { type, data } of queued) {
      this.dispatch(type, data)
    }
  }

  close(): void {
    this.closed = true
    this.es?.close()
    this.es = null
  }

  private buildUrl(): string {
    return '/api/admin/monitor/events'
  }

  private connect(): void {
    if (this.closed) return
    this.es = new EventSource(this.buildUrl())

    this.es.onopen = () => {
      if (this.lastEventId !== null) {
        this.onReconnected?.()
      }
    }

    this.es.onerror = () => {
      if (this.closed) return
      this.onConnectionLost?.()
      this.es?.close()
      this.es = null
      setTimeout(() => this.connect(), 3000)
    }

    const eventTypes: (keyof SseEventMap)[] = ['replay', 'append']

    for (const type of eventTypes) {
      this.es.addEventListener(type, (e: MessageEvent) => {
        if (e.lastEventId) this.lastEventId = e.lastEventId
        try {
          const data = JSON.parse(e.data) as unknown
          this.handleEvent(type, data)
        } catch {
          // malformed SSE data — ignore
        }
      })
    }
  }

  private isTerminal(type: string): boolean {
    return type === 'replay'
  }

  private handleEvent(type: string, data: unknown): void {
    if (this.paused) {
      if (!this.isTerminal(type) && this.pauseQueue.length >= PAUSE_QUEUE_MAX) {
        const idx = this.pauseQueue.findIndex(e => !this.isTerminal(e.type))
        if (idx !== -1) this.pauseQueue.splice(idx, 1)
      }
      this.pauseQueue.push({ type, data })
      return
    }
    this.dispatch(type, data)
  }

  private dispatch(type: string, data: unknown): void {
    const cb = this.callbacks[type as keyof SseEventMap]
    if (cb) (cb as (d: unknown) => void)(data)
  }
}
