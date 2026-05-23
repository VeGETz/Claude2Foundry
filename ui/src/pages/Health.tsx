import { useState, useEffect, useRef } from 'preact/hooks'
import { adminFetch } from '../api/client'
import type { HealthResponse } from '../api/contracts'

export function Health() {
  const [data, setData] = useState<HealthResponse | null>(null)
  const [error, setError] = useState<string | null>(null)
  const timerRef = useRef<ReturnType<typeof setInterval> | null>(null)

  const poll = () => {
    adminFetch<HealthResponse>('/health')
      .then(d => { setData(d); setError(null) })
      .catch(e => setError(String(e)))
  }

  useEffect(() => {
    poll()
    timerRef.current = setInterval(() => {
      if (document.visibilityState === 'visible') poll()
    }, 5000)
    const onVisible = () => { if (document.visibilityState === 'visible') poll() }
    document.addEventListener('visibilitychange', onVisible)
    return () => {
      if (timerRef.current) clearInterval(timerRef.current)
      document.removeEventListener('visibilitychange', onVisible)
    }
  }, [])

  if (error) return <p class="pico-color-red">Error: {error}</p>
  if (!data) return <p aria-busy="true">Loading health…</p>

  const fmt = (n: number) => n >= 3600
    ? `${Math.floor(n / 3600)}h ${Math.floor((n % 3600) / 60)}m`
    : n >= 60 ? `${Math.floor(n / 60)}m ${n % 60}s` : `${n}s`

  const fmtBytes = (n: number) => n > 1_048_576
    ? `${(n / 1_048_576).toFixed(1)} MB`
    : `${(n / 1024).toFixed(0)} KB`

  return (
    <div>
      <h2>Health</h2>

      <article>
        <h3>Foundry</h3>
        <p>Reachable: <strong>{data.foundry.reachable ? '✓' : '✗'}</strong></p>
        {data.foundry.lastProbeMs != null && <p>Last probe: {data.foundry.lastProbeMs}ms at {data.foundry.lastProbeAt}</p>}
        {data.foundry.lastFailureAt && (
          <p class="pico-color-red">Last failure: {data.foundry.lastFailureAt} — {data.foundry.lastFailureMessage}</p>
        )}
      </article>

      <article>
        <h3>Adapter</h3>
        <dl>
          <dt>Uptime</dt><dd>{fmt(data.uptimeSec)}</dd>
          <dt>Version</dt><dd>{data.version}</dd>
          <dt>.NET</dt><dd>{data.dotnetVersion}</dd>
          <dt>Data dir</dt><dd><code>{data.dataDir}</code></dd>
          <dt>Wrapper</dt><dd>{data.wrapperPresent ? '✓ present' : '✗ absent'}</dd>
        </dl>
      </article>

      <article>
        <h3>Traffic</h3>
        <dl>
          <dt>In-flight</dt><dd>{data.inFlight}</dd>
          <dt>Capture mode</dt><dd>{data.capture.mode} ({data.capture.scope})</dd>
          <dt>Ring buffer</dt><dd>
            {data.ringBuffer.occupancy}/{data.ringBuffer.capacity}
            <progress
              value={data.ringBuffer.occupancy}
              max={data.ringBuffer.capacity}
              style="margin-left:0.5rem;width:8rem"
            />
          </dd>
        </dl>
      </article>

      {data.logFile && (
        <article>
          <h3>Logs</h3>
          <p><code>{data.logFile.path}</code> ({fmtBytes(data.logFile.sizeBytes)})</p>
        </article>
      )}

      <details>
        <summary>Effective config</summary>
        <pre>{JSON.stringify(data.config, null, 2)}</pre>
      </details>
    </div>
  )
}
