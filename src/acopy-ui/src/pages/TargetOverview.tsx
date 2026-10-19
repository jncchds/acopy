import { useCallback, useEffect, useState } from 'react'
import { useParams, useSearchParams } from 'react-router-dom'
import { api, apiError, describeCron, duration, formatBytes, formatDate, relative, type Run, type TestResult } from '../api'
import { RunsTable, StatusPill, TargetStatusPill } from '../components/Bits'
import { useTargets } from '../hooks/useTargets'

export default function TargetOverview() {
  const { id } = useParams()
  const targetId = Number(id)
  const { targets } = useTargets()
  const t = targets?.find(x => x.id === targetId)
  const [runs, setRuns] = useState<Run[] | null>(null)
  const [params, setParams] = useSearchParams()
  const [selected, setSelected] = useState<Run | null>(null)
  const [test, setTest] = useState<TestResult | null>(null)
  const [testing, setTesting] = useState(false)

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
          {selected.error && <p className="error-msg" style={{ marginTop: '0.5rem' }}>{selected.error}</p>}
          {selected.log ? <div className="log-well">{selected.log}</div> : <p className="hint">No tool output.</p>}
        </div>
      )}

      <h3 className="section-title">History</h3>
      {!runs ? <p className="loading">Loading…</p> : (
        <RunsTable runs={runs} onOpen={r => setParams({ run: String(r.id) })} onChanged={load} />
      )}
    </div>
  )
}

/** The slot the next rolling backup will write: one after the newest successful slot. */
function nextRollingSlot(runs: Run[], keep: number): number {
  const last = runs.find(r => r.status === 'Succeeded' && r.slot != null)
  return last ? (last.slot! + 1) % keep : 0
}
