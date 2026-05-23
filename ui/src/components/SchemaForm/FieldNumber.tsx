import type { FieldProps } from './types'

export function FieldNumber({ schema, value, path, onChange, errors }: FieldProps) {
  const error = errors[path]
  const numVal = typeof value === 'number' ? value : (schema.default as number | undefined) ?? 0
  return (
    <div class="field-group">
      {schema.description && <small>{schema.description}</small>}
      <input
        type="number"
        value={numVal}
        min={schema.minimum}
        max={schema.maximum}
        aria-invalid={error ? 'true' : undefined}
        onInput={e => onChange(path, Number((e.target as HTMLInputElement).value))}
      />
      {error && <small class="error">{error}</small>}
    </div>
  )
}
