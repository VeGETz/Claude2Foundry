const BASE = '/api/admin'

export async function adminFetch<T>(
  path: string,
  init: RequestInit = {}
): Promise<T> {
  const method = (init.method ?? 'GET').toUpperCase()
  const isMutating = ['POST', 'PUT', 'DELETE', 'PATCH'].includes(method)

  const headers = new Headers(init.headers)
  if (isMutating) {
    headers.set('X-C2F-Admin', '1')
    if (!headers.has('Content-Type')) {
      headers.set('Content-Type', 'application/json')
    }
  }

  const res = await fetch(`${BASE}${path}`, { ...init, headers })
  if (!res.ok) {
    const text = await res.text().catch(() => '')
    throw new Error(`${res.status} ${res.statusText}: ${text}`)
  }
  return res.json() as Promise<T>
}
