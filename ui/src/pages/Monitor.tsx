import { useState, useEffect, useRef } from 'preact/hooks'
import { SseClient } from '../api/events'
import type { RequestSummaryFull, RequestSnapshotRecord, CaptureModeResponse } from '../api/contracts'
import { adminFetch } from '../api/client'
import { RequestRow } from '../components/RequestRow'
import { RequestDrawer } from '../components/RequestDrawer'
import { useApp } from '../App'

type StatusFilter = 'any' | 'ok' | 'error' | 'streaming'

function blankRecord(): RequestSummaryFull {
  return {
    id: '',
    ts: new Date().toISOString(),
    originalModel: '',
    resolvedModel: '',
    status: 'received',
    elapsedMs: null,
    usage: null,
    error: null,
    phase: 'received',
    stream: false,
  }
}

export function Monitor() {
  const { addToast } = useApp()
  const [records, setRecords] = useState<RequestSummaryFull[]>([])
  const [anthropicCache, setAnthropicCache] = useState<Record<string, unknown>>({})
  const [selected, setSelected] = useState<RequestSummaryFull | null>(null)
  const [paused, setPaused] = useState(false)
  const [statusFilter, setStatusFilter] = useState<StatusFilter>('any')
  const [modelFilter, setModelFilter] = useState<string[]>([])
  const [errorOnly, setErrorOnly] = useState(false)
  const [captureMode, setCaptureMode] = useState<'hybrid' | 'full'>('hybrid')
  const clientRef = useRef<SseClient | null>(null)
  const lastIdKey = 'c2f-monitor-last-id'

  const upsert = (update: Partial<RequestSummaryFull> & { id: string }) => {
    setRecords(prev => {
      const idx = prev.findIndex(r => r.id === update.id)
      if (idx === -1) {
        return [{ ...blankRecord(), ...update } as RequestSummaryFull, ...prev]
      }
      const next = [...prev]
      next[idx] = { ...next[idx], ...update }
      return next
    })
  }

  useEffect(() => {
    const since = sessionStorage.getItem(lastIdKey) ?? undefined
    const client = new SseClient({
      since,
      onConnectionLost: () => addToast('Monitor disconnected, reconnecting…', 'error'),
      onReconnected: () => addToast('Monitor reconnected', 'success'),
    })

    client
      .on('replay.snapshot', e => {
        setRecords(
          (e.records as RequestSnapshotRecord[]).map(r => ({ ...blankRecord(), ...r }))
        )
      })
      .on('request.received', e => {
        upsert({
          id: e.id,
          ts: e.ts,
          originalModel: e.originalModel,
          stream: e.stream,
          status: 'received',
          resolvedModel: '',
          elapsedMs: null,
          usage: null,
          error: null,
          phase: 'received',
        })
      })
      .on('request.translated', e => {
        upsert({ id: e.id, resolvedModel: e.resolvedModel, status: 'translated', phase: 'translated' })
      })
      .on('foundry.request.sent', e => {
        upsert({ id: e.id, status: 'foundry-sent' as RequestSummaryFull['status'], phase: 'foundry-sent' })
      })
      .on('foundry.chunk', e => {
        upsert({ id: e.id, status: 'streaming' })
      })
      .on('foundry.complete', e => {
        upsert({ id: e.id, usage: e.usage, status: 'complete', phase: 'complete' })
      })
      .on('response.sent', e => {
        upsert({ id: e.id, elapsedMs: e.elapsedMs, status: 'complete', phase: 'complete' })
        if (e.anthropicAssembled != null) {
          setAnthropicCache(prev => ({ ...prev, [e.id]: e.anthropicAssembled }))
        }
        sessionStorage.setItem(lastIdKey, Date.now().toString())
      })
      .on('error', e => {
        upsert({ id: e.id, status: 'error', error: e.message, phase: 'error' })
      })

    clientRef.current = client
    return () => client.close()
  }, [])

  useEffect(() => {
    if (paused) clientRef.current?.pause()
    else clientRef.current?.resume()
  }, [paused])

  const toggleCaptureMode = async (persist: boolean) => {
    const next = captureMode === 'hybrid' ? 'full' : 'hybrid'
    try {
      await adminFetch<CaptureModeResponse>('/capture-mode', {
        method: 'POST',
        body: JSON.stringify({ mode: next, scope: persist ? 'persistent' : 'session' }),
      })
      setCaptureMode(next)
    } catch {
      addToast('Failed to change capture mode', 'error')
    }
  }

  const allModels = [...new Set(records.map(r => r.originalModel))]

  const filtered = records.filter(r => {
    if (statusFilter !== 'any' && r.status !== statusFilter) return false
    if (modelFilter.length > 0 && !modelFilter.includes(r.originalModel)) return false
    if (errorOnly && r.error === null && r.status !== 'error') return false
    return true
  })

  const handleModelFilterChange = (e: Event) => {
    const sel = Array.from((e.target as HTMLSelectElement).selectedOptions).map(o => o.value)
    setModelFilter(sel)
  }

  return (
    <div>
      <h2>Monitor</h2>
      <div class="monitor-toolbar">
        <div style="display:flex;gap:1rem;align-items:flex-end">
          <label>
            Status
            <select value={statusFilter} onChange={e => setStatusFilter((e.target as HTMLSelectElement).value as StatusFilter)}>
              <option value="any">Any</option>
              <option value="ok">OK</option>
              <option value="error">Error</option>
              <option value="streaming">Streaming</option>
            </select>
          </label>
          {allModels.length > 0 && (
            <label>
              Model
              <select multiple size={Math.min(4, allModels.length)} onChange={handleModelFilterChange}>
                {allModels.map(m => <option key={m} value={m}>{m}</option>)}
              </select>
            </label>
          )}
          <label style="white-space:nowrap">
            <input
              type="checkbox"
              checked={errorOnly}
              onChange={e => setErrorOnly((e.target as HTMLInputElement).checked)}
            />
            Errors only
          </label>
        </div>
        <div class="monitor-actions">
          <button class="outline small" onClick={() => setPaused(p => !p)}>
            {paused ? 'Resume' : 'Pause'}
          </button>
          <button class="outline small secondary" onClick={() => setRecords([])}>
            Clear
          </button>
          <button class="outline small" onClick={() => toggleCaptureMode(false)}>
            Capture: {captureMode}
          </button>
          <a href="#" onClick={e => { e.preventDefault(); void toggleCaptureMode(true) }}>
            Make persistent
          </a>
        </div>
      </div>
      {paused && <p><em>Paused — buffering incoming events</em></p>}
      <div class="table-container">
        <table>
          <thead>
            <tr>
              <th>Time</th>
              <th>ID</th>
              <th>Model</th>
              <th>Status</th>
              <th>Elapsed</th>
              <th>In</th>
              <th>Out</th>
              <th>Error</th>
            </tr>
          </thead>
          <tbody>
            {filtered.map(r => (
              <RequestRow key={r.id} record={r} onClick={() => setSelected(r)} />
            ))}
          </tbody>
        </table>
      </div>
      {selected && (
        <RequestDrawer
          record={selected}
          anthropicAssembledCache={anthropicCache[selected.id] ?? null}
          onClose={() => setSelected(null)}
        />
      )}
    </div>
  )
}
