import { useState, useEffect } from 'preact/hooks'
import { adminFetch } from '../api/client'
import type { MonitorDetail, MonitorRow } from '../api/contracts'

interface Props {
  record: MonitorRow
  onClose: () => void
}

type Tab = 'anthropic-req' | 'openai-req' | 'foundry-resp' | 'anthropic-resp' | 'raw'

export function RequestDrawer({ record, onClose }: Props) {
  const [tab, setTab] = useState<Tab>('anthropic-req')
  const [detail, setDetail] = useState<MonitorDetail | null>(null)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    if (record.status === 'running') return
    setLoading(true)
    setError(null)
    adminFetch<MonitorDetail>(`/monitor/${encodeURIComponent(record.id)}`)
      .then(d => setDetail(d))
      .catch(e => setError(String(e)))
      .finally(() => setLoading(false))
  }, [record.id, record.status])

  const renderJson = (val: unknown, label: string) => {
    if (record.status === 'running') return <pre>{label}: in progress…</pre>
    if (loading) return <pre>loading…</pre>
    if (error) return <pre>unavailable</pre>
    return <pre>{val !== null && val !== undefined ? JSON.stringify(val, null, 2) : `${label}: not captured`}</pre>
  }

  const tabContent = () => {
    switch (tab) {
      case 'anthropic-req':
        return renderJson(detail?.anthropicBody, 'Anthropic request')
      case 'openai-req':
        return renderJson(detail?.openaiBody, 'OpenAI request')
      case 'foundry-resp':
        return renderJson(detail?.openaiResponse, 'Foundry response')
      case 'anthropic-resp':
        return renderJson(detail?.anthropicResponse, 'Anthropic response')
      case 'raw':
        return (
          <pre>{JSON.stringify({
            id: record.id,
            status: record.status,
            latencyMs: record.latencyMs,
            promptTokens: record.promptTokens,
            completionTokens: record.completionTokens,
            error: detail?.error ?? record.error,
            headers: detail?.headers,
          }, null, 2)}</pre>
        )
    }
  }

  return (
    <div class="request-drawer" onClick={onClose}>
      <article onClick={e => e.stopPropagation()}>
        <header>
          <button aria-label="Close" onClick={onClose}>✕</button>
          <h3>
            Request <code>{record.id}</code>
          </h3>
        </header>
        <nav>
          {(['anthropic-req', 'openai-req', 'foundry-resp', 'anthropic-resp', 'raw'] as Tab[]).map(t => (
            <a
              key={t}
              href="#"
              class={tab === t ? 'contrast' : ''}
              onClick={e => { e.preventDefault(); setTab(t) }}
            >
              {t}
            </a>
          ))}
        </nav>
        <div class="drawer-body">{tabContent()}</div>
      </article>
    </div>
  )
}
