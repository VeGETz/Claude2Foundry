import { useState, useEffect } from 'preact/hooks'
import { adminFetch } from '../api/client'
import type { RequestFullRecordResult, RequestSummaryFull } from '../api/contracts'
import { redactHeaders } from '../lib/redaction'

interface Props {
  record: RequestSummaryFull
  onClose: () => void
}

type Tab = 'anthropic-req' | 'openai-req' | 'foundry-resp' | 'anthropic-resp' | 'raw'

export function RequestDrawer({ record, onClose }: Props) {
  const [tab, setTab] = useState<Tab>('anthropic-req')
  const [full, setFull] = useState<RequestFullRecordResult | null>(null)
  const [loading, setLoading] = useState(false)

  useEffect(() => {
    setFull(null)
    setLoading(true)
    adminFetch<RequestFullRecordResult>(`/events/full/${encodeURIComponent(record.id)}`)
      .then(r => setFull(r))
      .catch(() => setFull(null))
      .finally(() => setLoading(false))
  }, [record.id])

  const isInFlight = record.status === 'streaming' || record.status === 'received' || record.status === 'foundry-sent'

  const renderBody = (bodyFn: () => unknown) => {
    if (isInFlight) return <pre>streaming…</pre>
    if (loading) return <pre>loading…</pre>
    if (!full) return <pre>unavailable</pre>
    if (full.expired) return <pre>body expired</pre>
    return <pre>{JSON.stringify(bodyFn(), null, 2)}</pre>
  }

  const tabContent = () => {
    switch (tab) {
      case 'anthropic-req':
        return renderBody(() => full && !full.expired ? full.anthropicBody : null)
      case 'openai-req':
        return renderBody(() => full && !full.expired ? full.openaiBody : null)
      case 'foundry-resp':
        return renderBody(() => full && !full.expired ? full.responseBody : null)
      case 'anthropic-resp':
        return renderBody(() => null)
      case 'raw':
        return (
          <pre>{JSON.stringify({
            id: record.id,
            headers: full && !full.expired ? redactHeaders(full.headers) : {},
            phase: record.phase,
            status: record.status,
            elapsedMs: record.elapsedMs,
            usage: record.usage,
            error: record.error,
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
