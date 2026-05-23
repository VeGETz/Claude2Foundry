// Frozen phase-0 contract types — extend only, never delete or rename.

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
  CaptureMode: 'hybrid' | 'full'
  LogMaxBytes: number
  LogRetentionDays: number
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
  ringBuffer: RingBufferInfo
  capture: CaptureInfo
  logFile: LogFileInfo
  wrapperPresent: boolean
}

export interface FoundryHealth {
  reachable: boolean
  lastProbeMs: number
  lastProbeAt: string | null
  lastFailureAt: string | null
  lastFailureMessage: string | null
}

export interface RingBufferInfo {
  occupancy: number
  capacity: number
}

export interface CaptureInfo {
  mode: 'hybrid' | 'full'
  scope: 'session' | 'persistent'
}

export interface LogFileInfo {
  path: string
  sizeBytes: number
}

export interface CaptureModeRequest {
  mode: 'hybrid' | 'full'
  scope: 'session' | 'persistent'
}

export interface CaptureModeResponse {
  ok: boolean
  mode: string
  scope: string
}

// ---- Error -----------------------------------------------------------------

export interface AdminError {
  error: {
    type: string
    message?: string
    issues?: Array<{ path: string; message: string }>
  }
}

// ---- SSE event payloads (camelCase — mirrors sse-events.md) ----------------

export interface RequestSnapshotRecord {
  id: string
  ts: string
  originalModel: string
  resolvedModel: string
  status: 'received' | 'translated' | 'foundry-sent' | 'streaming' | 'complete' | 'error'
  elapsedMs: number | null
  usage: { input: number; output: number } | null
  error: string | null
  phase: string
}

export interface ReplaySnapshotEvent {
  records: RequestSnapshotRecord[]
}

export interface RequestSummary {
  id: string
  ts: string
  originalModel: string
  stream: boolean
  bodyPreview?: unknown
  headers: Record<string, string>
}

export interface RequestReceivedEvent {
  id: string
  ts: string
  originalModel: string
  stream: boolean
  bodyPreview?: unknown
  headers: Record<string, string>
}

export interface RequestTranslatedEvent {
  id: string
  resolvedModel: string
  openaiBody?: unknown
}

export interface FoundryRequestSentEvent {
  id: string
  ts: string
}

export interface FoundryChunkEvent {
  id: string
  seq: number
  deltaText?: string
  deltaToolCall?: unknown
  reasoningDelta?: string
}

export interface FoundryCompleteEvent {
  id: string
  usage: { input: number; output: number }
  finishReason: string
}

export interface ResponseSentEvent {
  id: string
  elapsedMs: number
  anthropicAssembled?: unknown
}

export interface SseErrorEvent {
  id: string
  phase: string
  origin: 'Adapter' | 'Foundry'
  message: string
}

export interface RequestFullRecord {
  id: string
  anthropicBody?: unknown
  openaiBody?: unknown
  responseBody?: unknown
  headers: Record<string, string>
  phase: string
}

// ---- Extended RequestSummary (full replay.snapshot shape) ------------------

export interface RequestSummaryFull {
  id: string
  ts: string
  originalModel: string
  resolvedModel: string
  status: 'received' | 'translated' | 'foundry-sent' | 'streaming' | 'complete' | 'error'
  elapsedMs: number | null
  usage: { input: number; output: number } | null
  error: string | null
  phase: string
  stream: boolean
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

// ---- Full body record (discriminated union) --------------------------------

export interface RequestFullBody {
  expired: false
  id: string
  phase: string
  anthropicBody: unknown | null
  openaiBody: unknown | null
  responseBody: unknown | null
  /** Contract amendment authorized by Tech Lead — pending backend impl in phase 1 PR;
   *  GET /api/admin/events/full/{id} will surface this once Engineer A updates
   *  the FullBodyCache/JSONL reader. Until then, null for historical entries. */
  anthropicAssembled: unknown | null
  headers: Record<string, string>
}

export interface RequestExpired {
  expired: true
}

export type RequestFullRecordResult = RequestFullBody | RequestExpired
