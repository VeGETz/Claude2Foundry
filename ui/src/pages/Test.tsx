import { useState, useEffect } from 'preact/hooks'
import { adminFetch } from '../api/client'
import type { ConfigResponse, TestRequest, TestResponse } from '../api/contracts'
import { estimateTokens } from '../lib/tokenEstimate'
import { useApp } from '../App'

interface Message {
  role: 'user' | 'assistant'
  content: string
}

const THINKING_BUDGETS = [1000, 4000, 16000] as const

export function Test() {
  const { addToast } = useApp()
  const [models, setModels] = useState<string[]>([])
  const [defaultModel, setDefaultModel] = useState('')
  const [selectedModel, setSelectedModel] = useState('')
  const [messages, setMessages] = useState<Message[]>([{ role: 'user', content: '' }])
  const [thinkingEnabled, setThinkingEnabled] = useState(false)
  const [thinkingBudget, setThinkingBudget] = useState<number>(4000)
  const [streamEnabled, setStreamEnabled] = useState(false)
  const [tokenEstimate, setTokenEstimate] = useState<number | null>(null)
  const [estimating, setEstimating] = useState(false)
  const [submitting, setSubmitting] = useState(false)
  const [result, setResult] = useState<TestResponse | null>(null)
  const [sessionTokensIn, setSessionTokensIn] = useState(0)
  const [sessionTokensOut, setSessionTokensOut] = useState(0)
  const [resultTab, setResultTab] = useState<'anthropic' | 'openai-req' | 'openai-resp'>('anthropic')

  useEffect(() => {
    adminFetch<ConfigResponse>('/config').then(c => {
      const aliases = Object.keys(c.proxy.ModelAliases ?? {})
      setModels(aliases)
      setDefaultModel(c.proxy.DefaultModel)
      setSelectedModel(aliases[0] ?? c.proxy.DefaultModel)
    }).catch(() => {})
  }, [])

  const handleEstimate = async () => {
    if (!messages.some(m => m.content)) return
    setEstimating(true)
    try {
      const r = await estimateTokens({
        model: selectedModel || defaultModel,
        messages,
        thinking: thinkingEnabled ? { type: 'enabled', budget_tokens: thinkingBudget } : null,
      })
      setTokenEstimate(r.input_tokens)
    } catch {
      setTokenEstimate(null)
    } finally {
      setEstimating(false)
    }
  }

  const handleSubmit = async () => {
    setSubmitting(true)
    setResult(null)
    const req: TestRequest = {
      model: selectedModel || defaultModel,
      messages,
      thinking: thinkingEnabled ? { type: 'enabled', budget_tokens: thinkingBudget } : null,
      stream: streamEnabled,
    }
    try {
      const res = await adminFetch<TestResponse>('/test-request', {
        method: 'POST',
        body: JSON.stringify(req),
      })
      setResult(res)
      // update session tally from response usage if available
      const usage = (res.anthropicResponse as { usage?: { input_tokens: number; output_tokens: number } })?.usage
      if (usage) {
        setSessionTokensIn(t => t + (usage.input_tokens ?? 0))
        setSessionTokensOut(t => t + (usage.output_tokens ?? 0))
      }
    } catch (e) {
      addToast(`Test failed: ${e instanceof Error ? e.message : e}`, 'error')
    } finally {
      setSubmitting(false)
    }
  }

  const updateMessage = (i: number, field: keyof Message, value: string) => {
    setMessages(prev => {
      const next = [...prev]
      next[i] = { ...next[i], [field]: value }
      return next
    })
  }

  const addTurn = () => {
    setMessages(prev => {
      const last = prev[prev.length - 1]
      const nextRole: 'user' | 'assistant' = last.role === 'user' ? 'assistant' : 'user'
      return [...prev, { role: nextRole, content: '' }]
    })
  }

  return (
    <div>
      <h2>Test</h2>

      <div class="test-tally">
        <small>Session: {sessionTokensIn} in / {sessionTokensOut} out</small>
      </div>

      <div class="test-form">
        <label>
          Model
          <select value={selectedModel} onChange={e => setSelectedModel((e.target as HTMLSelectElement).value)}>
            {models.map(m => <option key={m} value={m}>{m}</option>)}
            {models.length === 0 && <option value={defaultModel}>{defaultModel}</option>}
            <option value="">other (uses DefaultModel)</option>
          </select>
        </label>

        <h4>Messages</h4>
        {messages.map((msg, i) => (
          <div key={i} class="test-message">
            <select
              value={msg.role}
              onChange={e => updateMessage(i, 'role', (e.target as HTMLSelectElement).value)}
            >
              <option value="user">user</option>
              <option value="assistant">assistant</option>
            </select>
            <textarea
              value={msg.content}
              rows={3}
              onInput={e => updateMessage(i, 'content', (e.target as HTMLTextAreaElement).value)}
              placeholder="Message content…"
            />
          </div>
        ))}
        <button type="button" class="outline small" onClick={addTurn}>+ Turn</button>

        <div class="test-options">
          <label>
            <input type="checkbox" checked={thinkingEnabled} onChange={e => setThinkingEnabled((e.target as HTMLInputElement).checked)} />
            Thinking
          </label>
          {thinkingEnabled && (
            <fieldset>
              <legend>Budget</legend>
              {THINKING_BUDGETS.map(b => (
                <label key={b}>
                  <input
                    type="radio"
                    name="budget"
                    value={b}
                    checked={thinkingBudget === b}
                    onChange={() => setThinkingBudget(b)}
                  />
                  {b === 1000 ? 'Low (1k)' : b === 4000 ? 'Med (4k)' : 'High (16k)'}
                </label>
              ))}
            </fieldset>
          )}
          <label>
            <input type="checkbox" checked={streamEnabled} onChange={e => setStreamEnabled((e.target as HTMLInputElement).checked)} />
            Stream
          </label>
        </div>

        <div class="test-estimate">
          <button type="button" class="outline small" onClick={handleEstimate} disabled={estimating}>
            {estimating ? 'Estimating…' : 'Estimate tokens'}
          </button>
          {tokenEstimate !== null && (
            <span>
              ~{tokenEstimate} input tokens &nbsp;
              <small class="pico-color-secondary">(Estimated cost: 0 — not billing-accurate)</small>
            </span>
          )}
        </div>

        <button onClick={handleSubmit} disabled={submitting}>
          {submitting ? 'Sending…' : 'Send'}
        </button>
      </div>

      {result && (
        <div class="test-result">
          <h3>Trace ({result.elapsedMs}ms)</h3>
          <nav>
            {(['anthropic', 'openai-req', 'openai-resp'] as const).map(t => (
              <a
                key={t}
                href="#"
                class={resultTab === t ? 'contrast' : ''}
                onClick={e => { e.preventDefault(); setResultTab(t) }}
              >
                {t}
              </a>
            ))}
          </nav>
          <pre>
            {resultTab === 'anthropic' && JSON.stringify(result.anthropicResponse, null, 2)}
            {resultTab === 'openai-req' && JSON.stringify(result.openaiRequest, null, 2)}
            {resultTab === 'openai-resp' && JSON.stringify(result.openaiResponse, null, 2)}
          </pre>
        </div>
      )}
    </div>
  )
}
