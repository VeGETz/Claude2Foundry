import type { FieldProps, FormValue, JsonSchema } from './types'
import { renderField } from './SchemaForm'

export function FieldUnion({ schema, value, path, onChange, errors, rootSchema }: FieldProps) {
  const obj = (typeof value === 'object' && value !== null && !Array.isArray(value)
    ? value
    : {}) as Record<string, FormValue>

  const discriminator = schema.discriminator?.propertyName ?? 'Source'
  const variants = schema.oneOf ?? []
  const currentDiscVal = (obj[discriminator] as string | undefined) ?? ''

  const getDiscValues = (s: JsonSchema): string[] => {
    const prop = s.properties?.[discriminator]
    if (!prop) return []
    if (prop.const !== undefined) return [String(prop.const)]
    if (prop.enum) return prop.enum as string[]
    return []
  }

  const activeVariant = variants.find(v => getDiscValues(v).includes(currentDiscVal))

  const handleDiscChange = (val: string) => {
    // reset to minimal object with new discriminator value
    onChange(path, { [discriminator]: val })
  }

  const allDiscValues = variants.flatMap(getDiscValues)

  return (
    <div class="field-union">
      <div class="field-group">
        <label>Type</label>
        <select
          value={currentDiscVal}
          onChange={e => handleDiscChange((e.target as HTMLSelectElement).value)}
        >
          {allDiscValues.map(v => <option key={v} value={v}>{v}</option>)}
        </select>
      </div>
      {activeVariant &&
        Object.entries(activeVariant.properties ?? {})
          .filter(([k]) => k !== discriminator)
          .map(([k, subSchema]) =>
            renderField(subSchema, obj[k] ?? null, `${path}.${k}`, onChange, errors, rootSchema)
          )
      }
    </div>
  )
}
