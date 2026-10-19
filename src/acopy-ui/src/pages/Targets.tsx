import { useNavigate } from 'react-router-dom'
import { api, describeCron, ENGINE_ICON, formatBytes, relative, targetState, type Target } from '../api'
import { TargetStatusPill, useToast } from '../components/Bits'
import { useTargets } from '../hooks/useTargets'

export default function Targets() {
  const { targets, error, reload } = useTargets()
  const nav = useNavigate()
  const [toast, show] = useToast()

  const run = async (t: Target) => {
    try { await api.runNow(t.id); show(`Backing up ${t.name}…`) }
    catch (e) { show(e instanceof Error ? e.message : String(e), true) }
    reload()
  }

  return (
    <div className="page">
      <div className="page-header">
        <h2>Databases</h2>
        <span className="spacer" />
        <button className="btn-primary" onClick={() => nav('/targets/new')}>＋ Add database</button>
      </div>
      {error && <p className="error-msg">{error}</p>}
      {!targets ? <p className="loading">Loading…</p> : targets.length === 0 ? (
        <p className="empty">Nothing configured yet. Add a database, or label a container with <code>acopy.enable=true</code>.</p>
      ) : (
        <div className="entity-list">
          {targets.map(t => {
            const st = targetState(t)
            return (
              <div key={t.id} className={`entity-card st-${st === 'ok' ? 'ok' : st === 'failed' ? 'failed' : st === 'running' ? 'running' : 'paused'}`}>
                <div className="entity-card-left">
                  <h3 onClick={() => nav(`/targets/${t.id}`)}>{ENGINE_ICON[t.engine]} {t.name}</h3>
                  <div className="meta-row">
                    <span className="chip chip-accent">{t.engine}</span>
                    {t.mode === 'DockerExec'
                      ? <span className="chip chip-patina" title="Dump runs inside the container">exec · {t.containerName}</span>
                      : <span className="chip" title="Dump runs in ACopy over the network">{t.engine === 'Sqlite' ? t.databaseName : `${t.host}${t.port ? ':' + t.port : ''}`}</span>}
                    <span className="chip">{t.naming === 'Rolling' ? `rolling × ${t.keep}` : `keep ${t.keep}`}</span>
                    {t.maxAgeDays && <span className="chip">≤ {t.maxAgeDays} days</span>}
                    {t.encryptionKeyName && <span className="chip chip-accent" title="Encrypted">🔐 {t.encryptionKeyName}</span>}
                    {t.destinationIds.length > 0 && <span className="chip" title="Copied to destinations">☁ × {t.destinationIds.length}</span>}
                    {t.source === 'DockerLabel' && <span className="chip chip-patina">🏷 labels</span>}
                  </div>
                  <div className="meta-line">
                    <strong>{describeCron(t.schedule)}</strong>
                    {t.nextRunAt && <> · next {relative(t.nextRunAt)}</>}
                    {' · '}{t.storedFiles} files, {formatBytes(t.storedBytes)}
                  </div>
                  {t.lastRun?.status === 'Failed' && <div className="error-msg" style={{ marginTop: '0.3rem' }}>{t.lastRun.error}</div>}
                </div>
                <div className="entity-card-right">
                  <TargetStatusPill t={t} />
                  <span className="muted">{t.lastSuccessAt ? `last ok ${relative(t.lastSuccessAt)}` : 'never succeeded'}</span>
                  <div className="entity-card-actions">
                    <button className="btn-ghost btn-sm" disabled={t.active || t.orphaned} onClick={() => run(t)}>▶ Run</button>
                    <button className="btn-primary btn-sm" onClick={() => nav(`/targets/${t.id}`)}>Open →</button>
                  </div>
                </div>
              </div>
            )
          })}
        </div>
      )}
      {toast}
    </div>
  )
}
