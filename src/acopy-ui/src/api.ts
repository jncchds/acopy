// ── DTOs (mirror ACopy.Api/Endpoints/Dtos.cs) ─────────────────────────────────

export type Engine = 'PostgreSql' | 'MySql' | 'MariaDb' | 'MongoDb' | 'Redis' | 'Sqlite' | 'SqlServer' | 'Custom'
export type Mode = 'Network' | 'DockerExec'
export type Naming = 'Timestamp' | 'Rolling'
export type Compression = 'None' | 'Gzip' | 'Brotli'
export type RunStatus = 'Queued' | 'Running' | 'Succeeded' | 'Failed' | 'Cancelled'

export interface Run {
  id: number
  targetId: number
  targetName: string | null
  status: RunStatus
  trigger: 'Schedule' | 'Manual'
  startedAt: string
  finishedAt: string | null
  filePath: string | null
  sizeBytes: number | null
  sha256: string | null
  slot: number | null
  fileGone: boolean
  error: string | null
  log: string | null
}

export interface Target {
  id: number
  name: string
  engine: Engine
  mode: Mode
  host: string | null
  port: number | null
  containerName: string | null
  username: string | null
  hasPassword: boolean
  passwordEnv: string | null
  databaseName: string | null
  extraArgs: string | null
  customExtension: string | null
  schedule: string
  enabled: boolean
  keep: number
  naming: Naming
  compression: Compression
  folder: string
  timeoutMinutes: number
  source: 'Manual' | 'DockerLabel'
  orphaned: boolean
  createdAt: string
  updatedAt: string
  nextRunAt: string | null
  active: boolean
  lastRun: Run | null
  lastSuccessAt: string | null
  storedBytes: number
  storedFiles: number
}

export interface TargetInput {
  name: string
  engine: Engine
  mode: Mode
  host?: string | null
  port?: number | null
  containerName?: string | null
  username?: string | null
  password?: string | null
  clearPassword?: boolean
  passwordEnv?: string | null
  databaseName?: string | null
  extraArgs?: string | null
  customExtension?: string | null
  schedule: string
  enabled: boolean
  keep: number
  naming: Naming
  compression: Compression
  folder?: string | null
  timeoutMinutes?: number | null
}

export interface EngineInfo {
  engine: Engine
  label: string
  defaultPort: number | null
  extension: string
  supportsNetwork: boolean
  supportsExec: boolean
  allDatabasesWhenEmpty: boolean
  databaseHint: string
  defaultUser: string | null
  notes: string
}

export interface Meta {
  version: string
  engines: EngineInfo[]
  labelPrefix: string
  labelKeys: { key: string; description: string }[]
}

export interface Dashboard {
  targets: number
  enabled: number
  failing: number
  active: number
  succeeded24h: number
  failed24h: number
  storedBytes: number
  storedFiles: number
  freeBytes: number | null
  upcoming: { id: number; name: string; engine: Engine; nextRunAt: string }[]
}

export interface SystemStatus {
  version: string
  timeZone: string
  backupsPath: string
  databasePath: string
  docker: {
    available: boolean
    version: string | null
    error: string | null
    socket: string
    labelPrefix: string
    lastScan: string | null
    scanError: string | null
    issues: { container: string; error: string }[]
  }
  tools: { tool: string; version: string | null }[]
}

export interface Container {
  id: string
  name: string
  image: string
  state: string
  status: string
  detectedEngine: Engine | null
  labelled: boolean
  acopyLabels: Record<string, string>
}

export interface Detected {
  engine: Engine | null
  user: string | null
  hasPassword: boolean
  database: string | null
  running: boolean
}

export interface TestResult { ok: boolean; message: string; details: string | null }
export interface Settings { timeZone: string; webhookUrl: string | null; notifyOnSuccess: boolean }
export interface AuthState { disabled: boolean; setupRequired: boolean; authenticated: boolean; passwordFromEnv: boolean }

// ── Transport ─────────────────────────────────────────────────────────────────

export class ApiError extends Error {
  status: number
  fields: Record<string, string[]>
  constructor(status: number, message: string, fields: Record<string, string[]> = {}) {
    super(message)
    this.status = status
    this.fields = fields
  }
}

/** Fired on any 401 so the auth provider can drop back to the login screen. */
export const UNAUTHORIZED_EVENT = 'acopy:unauthorized'

async function request<T>(method: string, url: string, body?: unknown): Promise<T> {
  const res = await fetch(url, {
    method,
    headers: body !== undefined ? { 'Content-Type': 'application/json' } : undefined,
    body: body !== undefined ? JSON.stringify(body) : undefined,
    credentials: 'same-origin',
  })
  if (res.status === 401 && !url.startsWith('/api/auth/')) window.dispatchEvent(new Event(UNAUTHORIZED_EVENT))
  if (!res.ok) {
    let message = `${res.status} ${res.statusText}`
    let fields: Record<string, string[]> = {}
    try {
      const p = await res.json()
      if (p.errors) {
        fields = Object.fromEntries(Object.entries(p.errors as Record<string, string[]>).map(([k, v]) => [k.charAt(0).toLowerCase() + k.slice(1), v]))
        message = Object.values(fields).flat()[0] ?? p.title ?? message
      } else message = p.detail ?? p.title ?? message
    } catch { /* not JSON */ }
    throw new ApiError(res.status, message, fields)
  }
  if (res.status === 204) return undefined as T
  return res.json() as Promise<T>
}

export const apiError = (e: unknown) => (e instanceof Error ? e.message : String(e))

// ── Endpoints ─────────────────────────────────────────────────────────────────

export const api = {
  authState: () => request<AuthState>('GET', '/api/auth/state'),
  setup: (password: string) => request<void>('POST', '/api/auth/setup', { password }),
  login: (password: string) => request<void>('POST', '/api/auth/login', { password }),
  logout: () => request<void>('POST', '/api/auth/logout'),
  changePassword: (current: string, password: string) => request<void>('POST', '/api/auth/password', { current, password }),

  meta: () => request<Meta>('GET', '/api/meta'),
  dashboard: () => request<Dashboard>('GET', '/api/dashboard'),
  system: () => request<SystemStatus>('GET', '/api/system'),

  targets: () => request<Target[]>('GET', '/api/targets'),
  target: (id: number) => request<Target>('GET', `/api/targets/${id}`),
  createTarget: (t: TargetInput) => request<Target>('POST', '/api/targets', t),
  updateTarget: (id: number, t: TargetInput) => request<Target>('PUT', `/api/targets/${id}`, t),
  deleteTarget: (id: number, deleteFiles: boolean) => request<void>('DELETE', `/api/targets/${id}?deleteFiles=${deleteFiles}`),
  setEnabled: (id: number, enabled: boolean) => request<void>('POST', `/api/targets/${id}/enabled`, { enabled }),
  runNow: (id: number) => request<{ runId: number }>('POST', `/api/targets/${id}/run`),
  cancel: (id: number) => request<void>('POST', `/api/targets/${id}/cancel`),
  testTarget: (id: number) => request<TestResult>('POST', `/api/targets/${id}/test`),
  testInput: (t: TargetInput, id?: number) => request<TestResult>('POST', `/api/targets/test${id ? `?id=${id}` : ''}`, t),

  runs: (q: { targetId?: number; status?: string; limit?: number } = {}) => {
    const p = new URLSearchParams()
    if (q.targetId) p.set('targetId', String(q.targetId))
    if (q.status) p.set('status', q.status)
    if (q.limit) p.set('limit', String(q.limit))
    return request<Run[]>('GET', `/api/runs?${p}`)
  },
  run: (id: number) => request<Run>('GET', `/api/runs/${id}`),
  deleteRunFile: (id: number) => request<void>('DELETE', `/api/runs/${id}/file`),
  downloadUrl: (id: number) => `/api/runs/${id}/download`,

  containers: () => request<Container[]>('GET', '/api/docker/containers'),
  detect: (name: string) => request<Detected>('GET', `/api/docker/containers/${encodeURIComponent(name)}/detect`),
  scan: () => request<unknown>('POST', '/api/docker/scan'),

  settings: () => request<Settings>('GET', '/api/settings'),
  saveSettings: (s: Settings) => request<Settings>('PUT', '/api/settings', s),
  testWebhook: () => request<void>('POST', '/api/settings/test-webhook'),
  timezones: () => request<string[]>('GET', '/api/timezones'),
}

// ── Formatting ────────────────────────────────────────────────────────────────

export function formatBytes(n: number | null | undefined): string {
  if (n == null) return '—'
  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  let v = n
  let i = 0
  while (v >= 1024 && i < units.length - 1) { v /= 1024; i++ }
  return `${v >= 100 || i === 0 ? v.toFixed(0) : v.toFixed(1)} ${units[i]}`
}

export function formatDate(s: string | null | undefined): string {
  if (!s) return '—'
  return new Date(s).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' })
}

export function relative(s: string | null | undefined): string {
  if (!s) return '—'
  const diff = new Date(s).getTime() - Date.now()
  const abs = Math.abs(diff)
  const fmt = new Intl.RelativeTimeFormat(undefined, { numeric: 'auto' })
  if (abs < 60_000) return fmt.format(Math.round(diff / 1000), 'second')
  if (abs < 3_600_000) return fmt.format(Math.round(diff / 60_000), 'minute')
  if (abs < 86_400_000) return fmt.format(Math.round(diff / 3_600_000), 'hour')
  return fmt.format(Math.round(diff / 86_400_000), 'day')
}

export function duration(run: Run): string {
  if (!run.finishedAt) return '—'
  const ms = new Date(run.finishedAt).getTime() - new Date(run.startedAt).getTime()
  if (ms < 1000) return `${ms} ms`
  if (ms < 60_000) return `${(ms / 1000).toFixed(1)} s`
  return `${Math.floor(ms / 60_000)}m ${Math.round((ms % 60_000) / 1000)}s`
}

export const ENGINE_ICON: Record<Engine, string> = {
  PostgreSql: '🐘', MySql: '🐬', MariaDb: '🦭', MongoDb: '🍃', Redis: '🟥', Sqlite: '🪶', SqlServer: '🗄', Custom: '🧩',
}

/** Card/entry status for a target: drives the left accent bar and the rail dot. */
export function targetState(t: Target): 'running' | 'failed' | 'ok' | 'paused' | 'never' | 'orphaned' {
  if (t.active) return 'running'
  if (t.orphaned) return 'orphaned'
  if (!t.enabled) return 'paused'
  if (!t.lastRun) return 'never'
  return t.lastRun.status === 'Succeeded' ? 'ok' : t.lastRun.status === 'Failed' ? 'failed' : 'never'
}

export const STATE_COLOR: Record<ReturnType<typeof targetState>, string> = {
  running: 'var(--accent)', failed: 'var(--danger)', ok: 'var(--success)', paused: 'var(--text-muted)',
  never: 'var(--border)', orphaned: 'var(--warning)',
}

/** Human description of a 5-field cron for the common shapes; falls back to the raw expression. */
export function describeCron(expr: string): string {
  const f = expr.trim().split(/\s+/)
  if (f.length !== 5) return expr
  const [m, h, dom, mon, dow] = f
  const num = (s: string) => /^\d+$/.test(s)
  const time = num(m) && num(h) ? `${h.padStart(2, '0')}:${m.padStart(2, '0')}` : null
  const days = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat']
  if (time && dom === '*' && mon === '*' && dow === '*') return `Daily at ${time}`
  if (time && dom === '*' && mon === '*' && num(dow)) return `Weekly, ${days[+dow % 7]} at ${time}`
  if (time && num(dom) && mon === '*' && dow === '*') return `Monthly on day ${dom} at ${time}`
  if (num(m) && h === '*' && dom === '*' && mon === '*' && dow === '*') return `Hourly at :${m.padStart(2, '0')}`
  const every = /^\*\/(\d+)$/
  if (every.test(m) && h === '*' && dom === '*' && mon === '*' && dow === '*') return `Every ${m.match(every)![1]} min`
  if (num(m) && every.test(h) && dom === '*' && mon === '*' && dow === '*') return `Every ${h.match(every)![1]} h at :${m.padStart(2, '0')}`
  return expr
}
