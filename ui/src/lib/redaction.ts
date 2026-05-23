const SENSITIVE_HEADER_PATTERNS = [
  /^authorization$/i,
  /^x-api-key$/i,
  /^api-key$/i,
  /^x-foundry-key$/i,
]

export function redactHeaders(headers: Record<string, string>): Record<string, string> {
  const out: Record<string, string> = {}
  for (const [k, v] of Object.entries(headers)) {
    out[k] = SENSITIVE_HEADER_PATTERNS.some(p => p.test(k)) ? '[REDACTED]' : v
  }
  return out
}
