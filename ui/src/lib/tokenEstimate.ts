export interface CountTokensResult {
  input_tokens: number
}

export async function estimateTokens(request: {
  model: string
  messages: Array<{ role: string; content: string }>
  thinking?: { type: 'enabled'; budget_tokens: number } | null
}): Promise<CountTokensResult> {
  const res = await fetch('/v1/messages/count_tokens', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'anthropic-version': '2023-06-01' },
    body: JSON.stringify(request),
  })
  if (!res.ok) throw new Error(`count_tokens failed: ${res.status}`)
  return res.json() as Promise<CountTokensResult>
}
