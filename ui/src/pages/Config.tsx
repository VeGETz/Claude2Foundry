import { useState, useEffect, useCallback } from 'preact/hooks'
import { adminFetch } from '../api/client'
import type {
  ConfigResponse,
  SaveConfigResponse,
  TestConnectionResponse,
  ProxyConfig,
} from '../api/contracts'
import type { JsonSchema, FormValue } from '../components/SchemaForm/types'
import { SchemaForm } from '../components/SchemaForm/SchemaForm'
import { useApp } from '../App'

function deepSet(obj: Record<string, FormValue>, path: string, value: FormValue): Record<string, FormValue> {
  if (!path) return obj
  const parts = path.split('.')
  const clone = { ...obj }
  if (parts.length === 1) {
    clone[parts[0]] = value
    return clone
  }
  const [head, ...rest] = parts
  clone[head] = deepSet(
    (typeof clone[head] === 'object' && clone[head] !== null ? clone[head] : {}) as Record<string, FormValue>,
    rest.join('.'),
    value
  )
  return clone
}

function validate(schema: JsonSchema, value: Record<string, FormValue>): Record<string, string> {
  const errors: Record<string, string> = {}
  const props = schema.properties ?? {}
  const required = schema.required ?? []
  for (const key of required) {
    const v = value[key]
    if (v === null || v === undefined || v === '') {
      errors[key] = 'Required'
    }
  }
  // BackendUrl pattern
  const buProp = props['BackendUrl']
  if (buProp?.pattern && typeof value['BackendUrl'] === 'string') {
    if (!new RegExp(buProp.pattern).test(value['BackendUrl'] as string)) {
      errors['BackendUrl'] = `Must match: ${buProp.pattern}`
    }
  }
  return errors
}

export function Config() {
  const { setRestartRequired, addToast } = useApp()
  const [schema, setSchema] = useState<JsonSchema | null>(null)
  const [formValue, setFormValue] = useState<Record<string, FormValue>>({})
  const [dirty, setDirty] = useState(false)
  const [errors, setErrors] = useState<Record<string, string>>({})
  const [saving, setSaving] = useState(false)
  const [testing, setTesting] = useState(false)
  const [testResult, setTestResult] = useState<{ ok: boolean; message: string } | null>(null)

  useEffect(() => {
    Promise.all([
      adminFetch<JsonSchema>('/config/schema'),
      adminFetch<ConfigResponse>('/config'),
    ]).then(([s, c]) => {
      setSchema(s)
      setFormValue(c.proxy as unknown as Record<string, FormValue>)
    }).catch(() => addToast('Failed to load config', 'error'))
  }, [])

  const handleChange = useCallback((path: string, value: FormValue) => {
    setFormValue(prev => deepSet(prev, path, value))
    setDirty(true)
  }, [])

  const handleSave = async () => {
    if (!schema) return
    const errs = validate(schema, formValue)
    if (Object.keys(errs).length > 0) {
      setErrors(errs)
      return
    }
    setSaving(true)
    try {
      const res = await adminFetch<SaveConfigResponse>('/config', {
        method: 'POST',
        body: JSON.stringify({ proxy: formValue }),
      })
      setErrors({})
      setDirty(false)
      if (res.restartRequired) {
        setRestartRequired(res.fields)
      } else {
        addToast('Config saved', 'success')
      }
    } catch (e: unknown) {
      const msg = e instanceof Error ? e.message : String(e)
      // try to parse server issues
      try {
        const parsed = JSON.parse(msg.slice(msg.indexOf('{'))) as { error: { issues: Array<{ path: string; message: string }> } }
        const serverErrs: Record<string, string> = {}
        for (const issue of parsed.error.issues ?? []) {
          serverErrs[issue.path] = issue.message
        }
        setErrors(serverErrs)
      } catch {
        addToast(`Save failed: ${msg}`, 'error')
      }
    } finally {
      setSaving(false)
    }
  }

  const handleTestConnection = async () => {
    setTesting(true)
    setTestResult(null)
    try {
      const res = await adminFetch<TestConnectionResponse>('/config/test-connection', {
        method: 'POST',
        body: JSON.stringify({ proxy: formValue }),
      })
      setTestResult({ ok: true, message: `✓ ${res.latencyMs}ms · ${res.modelCount} models` })
    } catch (e: unknown) {
      const msg = e instanceof Error ? e.message : String(e)
      setTestResult({ ok: false, message: `✗ ${msg}` })
    } finally {
      setTesting(false)
    }
  }

  if (!schema) return <p aria-busy="true">Loading config…</p>

  return (
    <div>
      <h2>Configuration</h2>
      <SchemaForm schema={schema} value={formValue} onChange={handleChange} errors={errors} />
      <div class="config-actions">
        <button
          onClick={handleTestConnection}
          disabled={testing}
          class="outline secondary"
        >
          {testing ? 'Testing…' : 'Test connection'}
        </button>
        {testResult && (
          <span class={testResult.ok ? 'pico-color-green' : 'pico-color-red'}>
            {testResult.message}
          </span>
        )}
        <button onClick={handleSave} disabled={!dirty || saving}>
          {saving ? 'Saving…' : 'Save'}
        </button>
      </div>
    </div>
  )
}
