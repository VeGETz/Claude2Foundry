import type { FieldProps } from './types'

export function FieldEnum({ schema, value, path, onChange, errors }: FieldProps) {
  const error = errors[path]
  const strVal = typeof value === 'string' ? value : String(schema.default ?? '')
  return (
    <div class="field-group">
      {schema.description && <small>{schema.description}</small>}
      <select
        value={strVal}
        aria-invalid={error ? 'true' : undefined}
        onChange={e => onChange(path, (e.target as HTMLSelectElement).value)}
      >
        {(schema.enum as string[]).map(opt => (
          <option key={opt} value={opt}>{opt}</option>
        ))}
      </select>
      {error && <small class="error">{error}</small>}
    </div>
  )
}
