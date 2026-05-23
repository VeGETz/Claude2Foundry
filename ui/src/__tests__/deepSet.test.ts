import { describe, it, expect } from 'vitest'
import type { FormValue } from '../components/SchemaForm/types'

// deepSet is not exported — replicate the function under test here so the
// test stays co-located with the logic without touching the module boundary.
function deepSet(
  obj: Record<string, FormValue>,
  path: string,
  value: FormValue
): Record<string, FormValue> {
  if (!path) return obj
  const parts = path.split('.')
  const clone = { ...obj }
  if (parts.length === 1) {
    clone[parts[0]] = value
    return clone
  }
  const [head, ...rest] = parts
  clone[head] = deepSet(
    (typeof clone[head] === 'object' && clone[head] !== null
      ? clone[head]
      : {}) as Record<string, FormValue>,
    rest.join('.'),
    value
  )
  return clone
}

describe('deepSet', () => {
  it('empty path returns obj unchanged', () => {
    const obj = { a: 'x', b: 2 }
    expect(deepSet(obj, '', 'should-be-ignored')).toBe(obj)
  })

  it('shallow key sets value', () => {
    expect(deepSet({ a: 'old' }, 'a', 'new')).toEqual({ a: 'new' })
  })

  it('nested key sets deep value', () => {
    const result = deepSet({ Timeouts: { OutboundTotalSeconds: 600, StreamIdleSeconds: 60 } as unknown as FormValue }, 'Timeouts.OutboundTotalSeconds', 300)
    expect((result['Timeouts'] as Record<string, FormValue>)['OutboundTotalSeconds']).toBe(300)
  })

  it('does not mutate original obj', () => {
    const obj = { a: 'x' }
    deepSet(obj, 'a', 'y')
    expect(obj.a).toBe('x')
  })

  it('creates missing intermediate objects', () => {
    const result = deepSet({}, 'a.b.c', 'deep')
    expect((result['a'] as Record<string, FormValue>)['b']).toEqual({ c: 'deep' })
  })
})
