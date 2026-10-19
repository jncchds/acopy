import { useCallback, useEffect, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { api, apiError, ENGINE_ICON, formatBytes, formatDate, relative, type Dashboard as D, type Run } from '../api'
import { RunsTable } from '../components/Bits'
import { useTargets } from '../hooks/useTargets'

export default function Dashboard() {
  const [d, setD] = useState<D | null>(null)
  const [runs, setRuns] = useState<Run[]>([])
  const [error, setError] = useState('')
  const { targets } = useTargets()
  const nav = useNavigate()

  const load = useCallback(async () => {
    try {
      const [dash, recent] = await Promise.all([api.dashboard(), api.runs({ limit: 15 })])
      setD(dash)
      setRuns(recent)
    } catch (e) { setError(apiError(e)) }
  }, [])

  // Re-read whenever the shared target list refreshes (it polls faster while a backup runs)
  useEffect(() => { load() }, [load, targets])

  if (error) return <p className="error-msg">{error}</p>
  if (!d) return <p className="loading">Loading…</p>

  return (
    <div className="page">
      <div className="page-header">
        <h2>Dashboard</h2>
        <span className="spacer" />
        <button className="btn-primary" onClick={() => nav('/targets/new')}>＋ Add database</button>
      </div>

      {d.targets === 0 && (
        <div className="callout">
          No databases yet. <a onClick={() => nav('/targets/new')} style={{ cursor: 'pointer' }}>Add one</a> by hand, or label a
          container with <code>acopy.enable=true</code> and it shows up here automatically — see <a onClick={() => nav('/docker')} style={{ cursor: 'pointer' }}>Docker</a>.
        </div>
      )}

      <div className="stat-grid">
        <div className="stat-tile accent">
          <div className="st-label">Databases</div>
          <div className="st-value">{d.targets}</div>
          <div className="st-sub">{d.enabled} scheduled{d.active > 0 ? ` · ${d.active} running` : ''}</div>
        </div>
        <div className={`stat-tile ${d.failing > 0 ? 'bad' : 'good'}`}>
          <div className="st-label">Failing</div>
          <div className="st-value">{d.failing}</div>
          <div className="st-sub">last run failed</div>
        </div>
        <div className="stat-tile">
          <div className="st-label">Last 24 h</div>
          <div className="st-value">{d.succeeded24h}<span className="muted"> / {d.succeeded24h + d.failed24h}</span></div>
          <div className="st-sub">{d.failed24h > 0 ? <span className="error-msg">{d.failed24h} failed</span> : 'all succeeded'}</div>
        </div>
        <div className="stat-tile">
          <div className="st-label">Stored</div>
          <div className="st-value">{formatBytes(d.storedBytes)}</div>
          <div className="st-sub">{d.storedFiles} files{d.freeBytes != null ? ` · ${formatBytes(d.freeBytes)} free` : ''}</div>
        </div>
      </div>

      {d.upcoming.length > 0 && (
        <>
          <h3 className="section-title">Up next</h3>
          <div className="data-table card" style={{ padding: 0 }}>
            <table>
              <tbody>
                {d.upcoming.map(u => (
                  <tr key={u.id} className="clickable" onClick={() => nav(`/targets/${u.id}`)}>
                    <td>{ENGINE_ICON[u.engine]} {u.name}</td>
                    <td className="num" title={formatDate(u.nextRunAt)}>{relative(u.nextRunAt)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </>
      )}

      <h3 className="section-title">Recent backups</h3>
      <RunsTable runs={runs} showTarget onOpen={r => nav(`/targets/${r.targetId}?run=${r.id}`)} onChanged={load} />
    </div>
  )
}
