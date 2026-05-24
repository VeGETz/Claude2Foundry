import { useState, useEffect, useRef } from 'preact/hooks'
import { SseClient } from '../api/events'
import type { MonitorRow, ReplayEvent, AppendEvent } from '../api/contracts'
import { RequestRow } from '../components/RequestRow'
import { RequestDrawer } from '../components/RequestDrawer'
import { useApp } from '../App'

type StatusFilter = 'any' | 'ok' | 'error' | 'running'

export function Monitor() {
  const { addToast } = useApp()
  const [records, setRecords] = useState<MonitorRow[]>([])
  const [selected, setSelected] = useState<MonitorRow | null>(null)
  const [paused, setPaused] = useState(false)
  const [statusFilter, setStatusFilter] = useState<StatusFilter>('any')
  const [modelFilter, setModelFilter] = useState<string[]>([])
  const [errorOnly, setErrorOnly] = useState(false)
  const clientRef = useRef<SseClient | null>(null)

  const upsert = (id: string, update: Partial<MonitorRow>) => {
    setRecords(prev => {
      const idx = prev.findIndex(r => r.id === id)
      if (idx === -1) {
        const blank: MonitorRow = {
          id,
          ts: new Date().toISOString(),
          model: null,
          originalModel: null,
          mappedModel: null,
          status: 'running',
          latencyMs: null,
          promptTokens: null,
          completionTokens: null,
          error: null,
        }
        return [{ ...blank, ...update }, ...prev]
      }
      const next = [...prev]
      next[idx] = { ...next[idx], ...update }
      return next
    })
  }

  useEffect(() => {
    const client = new SseClient({
      onConnectionLost: () => addToast('Monitor disconnected, reconnecting…', 'error'),
      onReconnected: () => addToast('Monitor reconnected', 'success'),
    })

    client
      .on('replay', (e: ReplayEvent) => {
        setRecords(e.items.map(item => ({
          id: item.id,
          ts: item.ts,
          model: item.model ?? null,
          originalModel: item.originalModel ?? null,
          mappedModel: item.mappedModel ?? null,
          status: item.status as 'running' | 'ok' | 'error',
          latencyMs: item.latencyMs ?? null,
          promptTokens: item.promptTokens ?? null,
          completionTokens: item.completionTokens ?? null,
          error: null,
        })))
      })
      .on('append', (e: AppendEvent) => {
        const d = e.data ?? {}
        switch (e.kind) {
          case 'request.received':
            upsert(e.id, { ts: e.ts, model: e.model ?? null, originalModel: e.model ?? null, status: 'running' })
            break
          case 'request.translated':
            upsert(e.id, { mappedModel: (d.mappedModel as string) ?? null })
            break
          case 'response.received':
            upsert(e.id, {
              promptTokens: (d.promptTokens as number) ?? null,
              completionTokens: (d.completionTokens as number) ?? null,
            })
            break
          case 'response.sent':
            upsert(e.id, { status: 'ok', latencyMs: (d.elapsedMs as number) ?? null })
            break
          case 'request.error':
            upsert(e.id, { status: 'error', error: (d.message as string) ?? null })
            break
        }
      })

    clientRef.current = client
    return () => client.close()
  }, [])

  useEffect(() => {
    if (paused) clientRef.current?.pause()
    else clientRef.current?.resume()
  }, [paused])

  const allModels = [...new Set(records.map(r => r.originalModel ?? r.model ?? ''))]

  const filtered = records.filter(r => {
    if (statusFilter !== 'any' && r.status !== statusFilter) return false
    const model = r.originalModel ?? r.model ?? ''
    if (modelFilter.length > 0 && !modelFilter.includes(model)) return false
    if (errorOnly && r.status !== 'error') return false
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
              <option value="running">Running</option>
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
          onClose={() => setSelected(null)}
        />
      )}
    </div>
  )
}
