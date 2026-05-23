import { useState } from 'preact/hooks'
import { adminFetch } from '../api/client'
import type { RequestFullRecordResult, RequestSummaryFull } from '../api/contracts'
import { redactHeaders } from '../lib/redaction'

interface Props {
  record: RequestSummaryFull
  /** anthropicAssembled buffered from the live response.sent SSE event for this id.
   *  Null for historical entries until the contract amendment lands in phase 1 backend. */
  anthropicAssembledCache: unknown | null
  onClose: () => void
}

type Tab = 'anthropic-req' | 'openai-req' | 'foundry-resp' | 'anthropic-resp' | 'raw'

export function RequestDrawer({ record, anthropicAssembledCache, onClose }: Props) {
  const [tab, setTab] = useState<Tab>('anthropic-req')
  const [full, setFull] = useState<RequestFullRecordResult | null>(null)
  const [loading, setLoading] = useState(false)
  const [loadRequested, setLoadRequested] = useState(false)

  const loadFullBodies = () => {
    setLoadRequested(true)
    setLoading(true)
    adminFetch<RequestFullRecordResult>(`/events/full/${encodeURIComponent(record.id)}`)
      .then(r => setFull(r))
      .catch(() => setFull(null))
      .finally(() => setLoading(false))
  }

  const isInFlight =
    record.status === 'streaming' ||
    record.status === 'received' ||
    record.status === 'foundry-sent'

  const renderBody = (bodyFn: () => unknown, label?: string) => {
    if (isInFlight) return <pre>streaming…</pre>
    if (!loadRequested) {
      return (
        <div>
          <p><em>Bodies not loaded.</em></p>
          <button type="button" class="outline small" onClick={loadFullBodies}>
            Load full bodies
          </button>
        </div>
      )
    }
    if (loading) return <pre>loading…</pre>
    if (!full) return <pre>unavailable</pre>
    if (full.expired) return <pre>Bodies expired</pre>
    const val = bodyFn()
    return <pre>{val !== null ? JSON.stringify(val, null, 2) : `${label ?? 'Body'}: not captured`}</pre>
  }

  const renderAnthropicResp = () => {
    if (isInFlight) return <pre>streaming…</pre>
    // Use live SSE cache first (always populated for requests seen in this session)
    if (anthropicAssembledCache !== null && anthropicAssembledCache !== undefined) {
      return <pre>{JSON.stringify(anthropicAssembledCache, null, 2)}</pre>
    }
    // For historical entries: try the full-body endpoint if already loaded
    // TODO: once Engineer A surfaces anthropicAssembled in GET /api/admin/events/full/{id}
    //       (contract amendment authorized by Tech Lead, pending phase 1 backend PR),
    //       replace this fallback with full.anthropicAssembled.
    if (loadRequested && full && !full.expired && full.anthropicAssembled !== null) {
      return <pre>{JSON.stringify(full.anthropicAssembled, null, 2)}</pre>
    }
    return <p><em>Not available for historical entries — pending contract amendment.</em></p>
  }

  const tabContent = () => {
    switch (tab) {
      case 'anthropic-req':
        return renderBody(() => full && !full.expired ? full.anthropicBody : null, 'Anthropic request')
      case 'openai-req':
        return renderBody(() => full && !full.expired ? full.openaiBody : null, 'OpenAI request')
      case 'foundry-resp':
        return renderBody(() => full && !full.expired ? full.responseBody : null, 'Foundry response')
      case 'anthropic-resp':
        return renderAnthropicResp()
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
