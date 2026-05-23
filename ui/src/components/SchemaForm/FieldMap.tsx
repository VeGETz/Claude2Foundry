import { useState } from 'preact/hooks'
import type { FieldProps, FormValue, JsonSchema } from './types'
import { renderField } from './SchemaForm'

export function FieldMap({ schema, value, path, onChange, errors, rootSchema }: FieldProps) {
  const [newKey, setNewKey] = useState('')
  const map = (typeof value === 'object' && value !== null && !Array.isArray(value)
    ? value
    : {}) as Record<string, FormValue>
  const subSchema = schema.additionalProperties as JsonSchema

  const handleValueChange = (k: string, v: FormValue) => {
    onChange(path, { ...map, [k]: v })
  }

  const addRow = () => {
    const key = newKey.trim()
    if (!key || key in map) return
    onChange(path, { ...map, [key]: '' })
    setNewKey('')
  }

  const removeRow = (k: string) => {
    const next = { ...map }
    delete next[k]
    onChange(path, next)
  }

  return (
    <div class="field-map">
      {Object.entries(map).map(([k, v]) => (
        <div key={k} class="field-map-row">
          <span class="map-key">{k}</span>
          {renderField(subSchema, v, `${path}.${k}`, onChange, errors, rootSchema)}
          <button type="button" class="outline secondary small" onClick={() => removeRow(k)}>
            ✕
          </button>
        </div>
      ))}
      <div class="field-map-add">
        <input
          type="text"
          placeholder="new key"
          value={newKey}
          onInput={e => setNewKey((e.target as HTMLInputElement).value)}
          onKeyDown={e => { if (e.key === 'Enter') { e.preventDefault(); addRow() } }}
        />
        <button type="button" class="outline small" onClick={addRow}>Add</button>
      </div>
    </div>
  )
}
