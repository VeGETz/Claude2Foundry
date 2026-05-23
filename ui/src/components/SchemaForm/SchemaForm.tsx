import type { FieldProps, FormValue, JsonSchema } from './types'
import { FieldString } from './FieldString'
import { FieldNumber } from './FieldNumber'
import { FieldBoolean } from './FieldBoolean'
import { FieldEnum } from './FieldEnum'
import { FieldMap } from './FieldMap'
import { FieldUnion } from './FieldUnion'

export function renderField(
  schema: JsonSchema,
  value: FormValue,
  path: string,
  onChange: (path: string, value: FormValue) => void,
  errors: Record<string, string>,
  rootSchema?: JsonSchema
): preact.JSX.Element {
  if (schema.oneOf) {
    return (
      <FieldUnion
        schema={schema}
        value={value}
        path={path}
        onChange={onChange}
        errors={errors}
        rootSchema={rootSchema}
      />
    )
  }
  if (schema.enum) {
    return (
      <FieldEnum
        schema={schema}
        value={value}
        path={path}
        onChange={onChange}
        errors={errors}
        rootSchema={rootSchema}
      />
    )
  }
  if (schema.additionalProperties && schema.type === 'object') {
    return (
      <FieldMap
        schema={schema}
        value={value}
        path={path}
        onChange={onChange}
        errors={errors}
        rootSchema={rootSchema}
      />
    )
  }

  const type = Array.isArray(schema.type) ? schema.type[0] : schema.type

  switch (type) {
    case 'object':
      return (
        <ObjectFields
          schema={schema}
          value={value}
          path={path}
          onChange={onChange}
          errors={errors}
          rootSchema={rootSchema ?? schema}
        />
      )
    case 'boolean':
      return (
        <FieldBoolean
          schema={schema}
          value={value}
          path={path}
          onChange={onChange}
          errors={errors}
          rootSchema={rootSchema}
        />
      )
    case 'number':
    case 'integer':
      return (
        <FieldNumber
          schema={schema}
          value={value}
          path={path}
          onChange={onChange}
          errors={errors}
          rootSchema={rootSchema}
        />
      )
    default:
      return (
        <FieldString
          schema={schema}
          value={value}
          path={path}
          onChange={onChange}
          errors={errors}
          rootSchema={rootSchema}
        />
      )
  }
}

function ObjectFields({
  schema,
  value,
  path,
  onChange,
  errors,
  rootSchema,
}: FieldProps & { rootSchema: JsonSchema }) {
  const obj = (typeof value === 'object' && value !== null && !Array.isArray(value)
    ? value
    : {}) as Record<string, FormValue>
  const props = schema.properties ?? {}
  return (
    <fieldset>
      {schema.title && <legend>{schema.title}</legend>}
      {Object.entries(props).map(([key, sub]) => {
        const fieldPath = path ? `${path}.${key}` : key
        return (
          <div key={key} class="schema-field">
            <label for={fieldPath}>{key}</label>
            {renderField(sub, obj[key] ?? null, fieldPath, onChange, errors, rootSchema)}
          </div>
        )
      })}
    </fieldset>
  )
}

interface SchemaFormProps {
  schema: JsonSchema
  value: Record<string, FormValue>
  onChange: (path: string, value: FormValue) => void
  errors: Record<string, string>
}

export function SchemaForm({ schema, value, onChange, errors }: SchemaFormProps) {
  return (
    <form class="schema-form" onSubmit={e => e.preventDefault()}>
      {renderField(schema, value as FormValue, '', onChange, errors, schema)}
    </form>
  )
}
