import type { FieldProps } from './types'

export function FieldString({ schema, value, path, onChange, errors }: FieldProps) {
  const error = errors[path]
  const strVal = typeof value === 'string' ? value : ''
  return (
    <div class="field-group">
      {schema.description && <small>{schema.description}</small>}
      <input
        type="text"
        value={strVal}
        placeholder={schema.default !== undefined ? String(schema.default) : undefined}
        aria-invalid={error ? 'true' : undefined}
        onInput={e => onChange(path, (e.target as HTMLInputElement).value)}
      />
      {error && <small class="error">{error}</small>}
    </div>
  )
}
