export interface JsonSchema {
  type?: string | string[]
  title?: string
  description?: string
  properties?: Record<string, JsonSchema>
  required?: string[]
  additionalProperties?: JsonSchema
  items?: JsonSchema
  enum?: unknown[]
  oneOf?: JsonSchema[]
  discriminator?: { propertyName: string }
  $ref?: string
  $defs?: Record<string, JsonSchema>
  minimum?: number
  maximum?: number
  minLength?: number
  pattern?: string
  default?: unknown
  const?: unknown
  not?: unknown
}

export type FormPrimitive = string | number | boolean | null
export type FormValue = FormPrimitive | FormValue[] | { [key: string]: FormValue }

export interface FieldProps {
  schema: JsonSchema
  value: FormValue
  path: string
  onChange: (path: string, value: FormValue) => void
  errors: Record<string, string>
  rootSchema?: JsonSchema
}
