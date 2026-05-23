import type { FieldProps } from './types'

export function FieldBoolean({ schema, value, path, onChange }: FieldProps) {
  const checked = typeof value === 'boolean' ? value : false
  return (
    <div class="field-group">
      <label>
        <input
          type="checkbox"
          role="switch"
          checked={checked}
          onChange={e => onChange(path, (e.target as HTMLInputElement).checked)}
        />
        {schema.description && <span>{schema.description}</span>}
      </label>
    </div>
  )
}
