// ---- Config (PascalCase — mirrors appsettings.json / json-schema.md) -------

export interface ProxyConfig {
  BackendUrl: string
  ApiKeyEnv: string
  DefaultModel: string
  ModelAliases: Record<string, string>
  ReasoningPolicies: Record<string, 'none' | 'passthrough' | 'effort'>
  Tokenizers: Record<string, TokenizerConfig>
  Timeouts: TimeoutsConfig
  Monitor: MonitorConfig
}

export interface TokenizerConfig {
  Source: 'TiktokenCl100k' | 'TiktokenO200k' | 'HuggingFace'
  Path?: string
}

export interface TimeoutsConfig {
  OutboundTotalSeconds: number
  StreamIdleSeconds: number
}

export interface MonitorConfig {
  Enabled: boolean
  MaxBodyBytes: number
}

// ---- API responses (camelCase — mirrors http-routes.md) --------------------

export interface ConfigResponse {
  proxy: ProxyConfig
}

export interface SaveConfigResponse {
  ok: boolean
  restartRequired: boolean
  fields: string[]
}

export interface TestConnectionResponse {
  ok: boolean
  latencyMs: number
  modelCount: number
}

export interface RestartResponse {
  draining: boolean
  graceMs: number
}

export interface HealthResponse {
  uptimeSec: number
  version: string
  dotnetVersion: string
  dataDir: string
  foundry: FoundryHealth
  config: ConfigResponse
  inFlight: number
  monitorEnabled: boolean
  logFile: LogFileInfo | null
  wrapperPresent: boolean
}

export interface FoundryHealth {
  reachable: boolean
  lastProbeMs: number
  lastProbeAt: string | null
  lastFailureAt: string | null
  lastFailureMessage: string | null
}

export interface LogFileInfo {
  path: string
  sizeBytes: number
}

// ---- Error -----------------------------------------------------------------

export interface AdminError {
  error: {
    type: string
    message?: string
    issues?: Array<{ path: string; message: string }>
  }
}

// ---- Monitor list / detail -------------------------------------------------

export interface MonitorListItem {
  id: string
  ts: string
  model: string | null
  originalModel: string | null
  mappedModel: string | null
  status: 'ok' | 'error' | 'running'
  latencyMs: number | null
  promptTokens: number | null
  completionTokens: number | null
}

export interface MonitorListResponse {
  items: MonitorListItem[]
}

export interface MonitorDetail {
  id: string
  ts: string
  model: string | null
  status: string
  latencyMs: number | null
  anthropicBody: unknown | null
  openaiBody: unknown | null
  openaiResponse: unknown | null
  anthropicResponse: unknown | null
  headers: unknown | null
  error: unknown | null
}

// ---- UI row (live + historical) --------------------------------------------

export interface MonitorRow {
  id: string
  ts: string
  model: string | null
  originalModel: string | null
  mappedModel: string | null
  status: 'running' | 'ok' | 'error'
  latencyMs: number | null
  promptTokens: number | null
  completionTokens: number | null
  error: string | null
}

// ---- SSE event payloads ----------------------------------------------------

export interface ReplayEvent {
  items: MonitorListItem[]
}

export interface AppendEvent {
  id: string
  ts: string
  kind: string
  model?: string
  data?: Record<string, unknown>
}

// ---- Test page -------------------------------------------------------------

export interface TestRequest {
  model: string
  messages: Array<{ role: 'user' | 'assistant'; content: string }>
  thinking: { type: 'enabled'; budget_tokens: number } | null
  stream: boolean
}

export interface TestResponse {
  anthropicResponse: unknown
  openaiRequest: unknown
  openaiResponse: unknown
  elapsedMs: number
}
