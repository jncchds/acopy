import { useCallback, useEffect, useState } from 'react'
import { useParams, useSearchParams } from 'react-router-dom'
import { api, apiError, describeCron, duration, formatBytes, formatDate, relative, type EncKey, type Run, type Target, type TestResult } from '../api'
import { RunsTable, StatusPill, TargetStatusPill, useToast } from '../components/Bits'
import ToggleField from '../components/ToggleField'
import { useTargets } from '../hooks/useTargets'

export default function TargetOverview() {
  const { id } = useParams()
  const targetId = Number(id)
  const { targets, reload } = useTargets()
  const t = targets?.find(x => x.id === targetId)
  const [runs, setRuns] = useState<Run[] | null>(null)
  const [params, setParams] = useSearchParams()
  const [selected, setSelected] = useState<Run | null>(null)
  const [test, setTest] = useState<TestResult | null>(null)
  const [testing, setTesting] = useState(false)
  const [restoring, setRestoring] = useState<Run | null>(null)
  const [toast, show] = useToast()

  const load = useCallback(async () => {
    setRuns(await api.runs({ targetId, limit: 200 }))
  }, [targetId])

  // Refresh history whenever the shared list ticks (fast while running)
  useEffect(() => { load() }, [load, t?.active, t?.lastRun?.id, t?.lastRun?.status])

  const runParam = params.get('run')
  useEffect(() => {
    if (!runParam) { setSelected(null); return }
    api.run(Number(runParam)).then(setSelected).catch(() => setSelected(null))
  }, [runParam, t?.lastRun?.status])

  const doTest = async () => {
    setTesting(true)
    setTest(null)
    try { setTest(await api.testTarget(targetId)) }
    catch (e) { setTest({ ok: false, message: apiError(e), details: null }) }
    finally { setTesting(false) }
  }

  const retry = async (uploadId: number) => {
    try {
      const u = await api.retryUpload(uploadId)
      show(u.status === 'Succeeded' ? 'Uploaded' : `Upload failed: ${u.error}`, u.status !== 'Succeeded')
      if (selected) setSelected(await api.run(selected.id))
      load()
    } catch (e) { show(apiError(e), true) }
  }

  if (!targets) return <p className="loading">Loading…</p>
  if (!t) return <p className="empty">This database no longer exists.</p>

  const slots = t.naming === 'Rolling' ? Array.from({ length: Math.min(t.keep, 50) }, (_, i) => i) : []
  const nextSlot = t.naming === 'Rolling' && runs ? nextRollingSlot(runs, t.keep) : null

  return (
    <div className="page">
      <div className="page-header">
        <h2>{t.name}</h2>
        <TargetStatusPill t={t} />
        <span className="spacer" />
        <button className="btn-ghost" onClick={doTest} disabled={testing}>{testing ? 'Testing…' : '🔌 Test connection'}</button>
      </div>

      {t.orphaned && (
        <div className="callout warn">
          The container that defined this target (<code>{t.containerName}</code>) is gone or lost its <code>acopy.enable</code> label.
          It is no longer scheduled; its history and files are kept. Delete it from <em>Configuration</em> when you no longer need it.
        </div>
      )}
      {test && (
        <div className={`callout ${test.ok ? 'ok' : 'danger'}`}>
          <strong>{test.ok ? '✓ ' : '✗ '}</strong>{test.message}
          {test.details && <div className="log-well">{test.details}</div>}
        </div>
      )}

      <div className="stat-grid">
        <div className="stat-tile">
          <div className="st-label">Schedule</div>
          <div className="st-value" style={{ fontSize: '1.05rem' }}>{describeCron(t.schedule)}</div>
          <div className="st-sub mono">{t.schedule}</div>
        </div>
        <div className="stat-tile">
          <div className="st-label">Next run</div>
          <div className="st-value" style={{ fontSize: '1.05rem' }}>{t.nextRunAt ? relative(t.nextRunAt) : t.enabled ? '—' : 'paused'}</div>
          <div className="st-sub">{formatDate(t.nextRunAt)}</div>
        </div>
        <div className="stat-tile">
          <div className="st-label">Retention</div>
          <div className="st-value" style={{ fontSize: '1.05rem' }}>{t.naming === 'Rolling' ? `${t.keep} rolling slots` : `Newest ${t.keep}`}</div>
          {t.naming === 'Rolling'
            ? <div className="slot-strip">{slots.map(s => <span key={s} className={`slot${s === nextSlot ? ' next' : ''}`} title={s === nextSlot ? 'Written next' : undefined}>{s}</span>)}</div>
            : <div className="st-sub">timestamped files</div>}
        </div>
        <div className="stat-tile">
          <div className="st-label">Stored</div>
          <div className="st-value" style={{ fontSize: '1.05rem' }}>{formatBytes(t.storedBytes)}</div>
          <div className="st-sub mono">/backups/{t.folder}/ · {t.storedFiles} files</div>
        </div>
      </div>

      {selected && (
        <div className="card" style={{ marginBottom: '1.25rem' }}>
          <div className="page-header" style={{ marginBottom: '0.75rem' }}>
            <h2 style={{ fontSize: '1.05rem' }}>Run #{selected.id}</h2>
            <StatusPill status={selected.status} />
            <span className="spacer" />
            <button className="btn-icon" onClick={() => setParams({})} title="Close">✕</button>
          </div>
          <div className="meta-line">
            Started <strong>{formatDate(selected.startedAt)}</strong> ({selected.trigger.toLowerCase()}) · took {duration(selected)}
            {selected.sizeBytes != null && <> · {formatBytes(selected.sizeBytes)}</>}
            {selected.slot != null && <> · slot {selected.slot}</>}
          </div>
          {selected.filePath && <div className="meta-line">File <code className={selected.fileGone ? 'file-gone' : ''}>{selected.filePath}</code>{selected.fileGone && ' (rotated out / deleted)'}</div>}
          {selected.sha256 && <div className="meta-line">SHA-256 <code>{selected.sha256}</code></div>}
          {selected.uploads.length > 0 && (
            <div className="data-table" style={{ margin: '0.6rem 0' }}>
              <table>
                <tbody>
                  {selected.uploads.map(u => (
                    <tr key={u.id}>
                      <td className="nowrap">{u.kind === 'Telegram' ? '✈' : '☁'} {u.destinationName}</td>
                      <td>{u.deleted ? <span className="pill pill-cancelled">removed by retention</span> : <StatusPill status={u.status === 'Pending' ? 'Running' : u.status} />}</td>
                      <td className="mono muted" style={{ wordBreak: 'break-all' }}>{u.error ? <span className="error-msg">{u.error}</span> : u.remoteRef}</td>
                      <td className="nowrap">
                        {(u.status === 'Failed' || u.deleted) && !selected.fileGone &&
                          <button className="btn-ghost btn-sm" onClick={() => retry(u.id)} disabled={t.active}>↻ Retry</button>}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
          {selected.error && <p className="error-msg" style={{ marginTop: '0.5rem' }}>{selected.error}</p>}
          {selected.log ? <div className="log-well">{selected.log}</div> : <p className="hint">No tool output.</p>}
        </div>
      )}

      {restoring && (
        <RestorePanel
          t={t}
          run={restoring}
          onClose={() => setRestoring(null)}
          onStarted={async runId => {
            setRestoring(null)
            show('Restore started')
            await reload()
            setParams({ run: String(runId) })
          }}
        />
      )}

      <h3 className="section-title">History</h3>
      {!runs ? <p className="loading">Loading…</p> : (
        <RunsTable runs={runs} onOpen={r => setParams({ run: String(r.id) })} onChanged={load}
                   onRestore={t.restoreUnsupported || t.orphaned ? undefined : r => setRestoring(r)} />
      )}
      {t.restoreUnsupported && <p className="hint" style={{ marginTop: '0.75rem' }}>↺ {t.restoreUnsupported}</p>}
      {toast}
    </div>
  )
}

function RestorePanel({ t, run, onClose, onStarted }: { t: Target; run: Run; onClose: () => void; onStarted: (runId: number) => void }) {
  const [safety, setSafety] = useState(true)
  const [confirmName, setConfirmName] = useState('')
  const [identity, setIdentity] = useState('')
  const [source, setSource] = useState<number | null>(run.fileGone ? run.uploads.find(u => u.canRestore)?.id ?? null : null)
  const [keys, setKeys] = useState<EncKey[]>([])
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)

  useEffect(() => { api.keys().then(setKeys).catch(() => {}) }, [])
  const key = keys.find(k => k.id === run.encryptionKeyId)
  const needsIdentity = (run.filePath ?? '').endsWith('.age') && (!key || !key.hasSecret)
  const remote = run.uploads.filter(u => u.canRestore)

  const start = async () => {
    setBusy(true); setError('')
    try {
      const r = await api.restore(run.id, { safetyBackup: safety, confirmName, ageIdentity: identity || null, fromUploadId: source })
      onStarted(r.runId)
    } catch (e) { setError(apiError(e)) }
    finally { setBusy(false) }
  }

  return (
    <div className="card restore-card" style={{ marginBottom: '1.25rem' }}>
      <div className="page-header" style={{ marginBottom: '0.5rem' }}>
        <h2 style={{ fontSize: '1.05rem', margin: 0 }}>↺ Restore {t.name}</h2>
        <span className="spacer" />
        <button className="btn-icon" onClick={onClose} title="Close">✕</button>
      </div>
      <div className="callout warn">
        This <strong>overwrites the current data</strong> in <code>{t.databaseName || 'all databases'}</code>
        {t.mode === 'DockerExec' ? <> inside <code>{t.containerName}</code></> : t.host ? <> on <code>{t.host}</code></> : null} with the backup from{' '}
        <strong>{formatDate(run.startedAt)}</strong>. Applications using the database may see errors while it runs
        {!t.databaseName && t.engine === 'PostgreSql' ? '; for whole-cluster dumps, stop them first' : ''}.
      </div>
      {remote.length > 0 && (
        <label>
          Source
          <select value={source ?? ''} onChange={e => setSource(e.target.value ? Number(e.target.value) : null)}>
            {!run.fileGone && <option value="">Local file — {run.filePath}</option>}
            {remote.map(u => <option key={u.id} value={u.id}>☁ {u.destinationName} — {u.remoteRef}</option>)}
          </select>
        </label>
      )}
      {needsIdentity && (
        <label>
          age private key
          <textarea className="mono" value={identity} onChange={e => setIdentity(e.target.value)} placeholder="AGE-SECRET-KEY-1…" />
          <span className="hint">ACopy only has the public key for this file. The private key is used for this restore and not stored.</span>
        </label>
      )}
      <ToggleField checked={safety} onChange={setSafety} label="Back up the current state first"
                   hint="A fresh backup is taken right before restoring, so this can be undone. Recommended." />
      <label style={{ marginTop: '0.75rem' }}>
        Type <strong>{t.name}</strong> to confirm
        <input value={confirmName} onChange={e => setConfirmName(e.target.value)} autoComplete="off" />
      </label>
      {error && <p className="error-msg" style={{ marginBottom: '0.5rem' }}>{error}</p>}
      <div className="form-actions">
        <button className="btn-danger" onClick={start} disabled={busy || confirmName !== t.name || (needsIdentity && !identity.trim())}>↺ Restore now</button>
        <button className="btn-ghost" onClick={onClose}>Cancel</button>
      </div>
    </div>
  )
}

/** The slot the next rolling backup will write: one after the newest successful slot. */
function nextRollingSlot(runs: Run[], keep: number): number {
  const last = runs.find(r => r.status === 'Succeeded' && r.slot != null)
  return last ? (last.slot! + 1) % keep : 0
}
