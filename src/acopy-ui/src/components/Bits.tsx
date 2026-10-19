import { useCallback, useEffect, useState } from 'react'
import { api, formatBytes, formatDate, duration, relative, type Run, type RunStatus, type Target } from '../api'

export function StatusPill({ status }: { status: RunStatus | 'Never' | 'Paused' | 'Orphaned' }) {
  const label = status === 'Queued' ? 'Queued' : status
  return (
    <span className={`pill pill-${status.toLowerCase()}`}>
      {(status === 'Running' || status === 'Queued') && <span className="spinner" />}
      {label}
    </span>
  )
}

export function TargetStatusPill({ t }: { t: Target }) {
  if (t.active) return <StatusPill status={t.lastRun?.status === 'Queued' ? 'Queued' : 'Running'} />
  if (t.orphaned) return <StatusPill status="Orphaned" />
  if (!t.enabled) return <StatusPill status="Paused" />
  return <StatusPill status={t.lastRun?.status ?? 'Never'} />
}

export function Toast({ message, error, onDone }: { message: string; error?: boolean; onDone: () => void }) {
  useEffect(() => {
    const id = window.setTimeout(onDone, error ? 6000 : 3000)
    return () => window.clearTimeout(id)
  }, [message, error, onDone])
  return <div className={`toast${error ? ' error' : ''}`}>{error ? '⚠' : '✓'} {message}</div>
}

/** Tiny toast state helper: const [toast, show] = useToast(); … {toast} */
export function useToast(): [React.ReactNode, (msg: string, error?: boolean) => void] {
  const [t, setT] = useState<{ msg: string; error: boolean; key: number } | null>(null)
  const done = useCallback(() => setT(null), [])
  const show = useCallback((msg: string, error = false) => setT({ msg, error, key: Date.now() }), [])
  const node = t ? <Toast key={t.key} message={t.msg} error={t.error} onDone={done} /> : null
  return [node, show]
}

const TRIGGER: Record<Run['trigger'], string> = { Manual: 'manual', Schedule: 'scheduled', SafetyBackup: 'before restore' }

/** Small per-destination upload markers for a run. */
export function UploadBadges({ run }: { run: Run }) {
  if (run.uploads.length === 0) return null
  return (
    <span className="upload-badges">
      {run.uploads.map(u => (
        <span key={u.id} className={`upload-badge ub-${u.deleted ? 'deleted' : u.status.toLowerCase()}`}
              title={`${u.destinationName}: ${u.deleted ? 'removed by retention' : u.status}${u.error ? ' — ' + u.error : ''}`}>
          {u.kind === 'Telegram' ? '✈' : '☁'}
        </span>
      ))}
    </span>
  )
}

/** The run history table used on the target page and the Activity page. */
export function RunsTable({ runs, showTarget, onOpen, onChanged, onRestore }: {
  runs: Run[]
  showTarget?: boolean
  onOpen?: (r: Run) => void
  onChanged?: () => void
  onRestore?: (r: Run) => void
}) {
  if (runs.length === 0) return <p className="empty">No backups yet.</p>
  const deleteFile = async (r: Run) => {
    if (!confirm(`Delete ${r.filePath}? The file is removed from disk (remote copies stay).`)) return
    await api.deleteRunFile(r.id)
    onChanged?.()
  }
  const restorable = (r: Run) => r.kind === 'Backup' && r.status === 'Succeeded' && (!r.fileGone || r.uploads.some(u => u.canRestore))
  return (
    <div className="data-table">
      <table>
        <thead>
          <tr>
            <th>Started</th>
            {showTarget && <th>Target</th>}
            <th>Status</th>
            <th>File</th>
            <th className="num">Size</th>
            <th className="num">Took</th>
            <th />
          </tr>
        </thead>
        <tbody>
          {runs.map(r => (
            <tr key={r.id} className={onOpen ? 'clickable' : undefined} onClick={() => onOpen?.(r)}>
              <td className="nowrap" title={formatDate(r.startedAt)}>
                {relative(r.startedAt)}
                <div className="muted">{r.kind === 'Restore' ? 'restore' : TRIGGER[r.trigger]}</div>
              </td>
              {showTarget && <td>{r.targetName ?? `#${r.targetId}`}</td>}
              <td>
                {r.kind === 'Restore' && <span className="chip chip-patina" style={{ marginRight: '0.3rem' }}>↺ restore</span>}
                <StatusPill status={r.status} />
              </td>
              <td>
                {r.kind === 'Restore'
                  ? <span className="muted">from run #{r.sourceRunId}{r.error && <span className="error-msg"> — {r.error}</span>}</span>
                  : r.filePath
                    ? <>
                        {r.encryptionKeyId != null && <span title="Encrypted">🔐 </span>}
                        <span className={`mono${r.fileGone ? ' file-gone' : ''}`} title={r.fileGone ? 'Rotated out or deleted' : r.sha256 ?? ''}>{r.filePath}</span>
                        <UploadBadges run={r} />
                      </>
                    : <span className="error-msg">{r.error}</span>}
              </td>
              <td className="num">{r.kind === 'Restore' ? '' : formatBytes(r.sizeBytes)}</td>
              <td className="num">{duration(r)}</td>
              <td className="nowrap" onClick={e => e.stopPropagation()}>
                <span className="entity-card-actions">
                  {onRestore && restorable(r) && <button className="btn-icon" onClick={() => onRestore(r)} title="Restore this backup">↺</button>}
                  {r.kind === 'Backup' && r.filePath && !r.fileGone && (
                    <>
                      <a className="btn-icon" href={api.downloadUrl(r.id)} title="Download">⬇</a>
                      <button className="btn-icon danger" onClick={() => deleteFile(r)} title="Delete file">🗑</button>
                    </>
                  )}
                </span>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}
