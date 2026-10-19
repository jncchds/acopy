import { useCallback, useEffect, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { api, apiError, ENGINE_ICON, relative, type Container, type Meta, type SystemStatus } from '../api'
import { useToast } from '../components/Bits'
import { useTargets } from '../hooks/useTargets'

export default function Docker() {
  const [sys, setSys] = useState<SystemStatus | null>(null)
  const [meta, setMeta] = useState<Meta | null>(null)
  const [containers, setContainers] = useState<Container[] | null>(null)
  const [cError, setCError] = useState('')
  const [busy, setBusy] = useState(false)
  const [toast, show] = useToast()
  const { targets, reload } = useTargets()
  const nav = useNavigate()

  const load = useCallback(async () => {
    const [s, m] = await Promise.all([api.system(), api.meta()])
    setSys(s); setMeta(m)
    if (s.docker.available) {
      try { setContainers(await api.containers()); setCError('') }
      catch (e) { setCError(apiError(e)) }
    }
  }, [])

  useEffect(() => { load() }, [load])

  const scan = async () => {
    setBusy(true)
    try { await api.scan(); await Promise.all([load(), reload()]); show('Scan complete') }
    catch (e) { show(apiError(e), true) }
    finally { setBusy(false) }
  }

  if (!sys || !meta) return <p className="loading">Loading…</p>
  const p = meta.labelPrefix
  const byContainer = new Map((targets ?? []).filter(t => t.containerName).map(t => [t.containerName!, t]))

  return (
    <div className="page">
      <div className="page-header">
        <h2>Docker</h2>
        {sys.docker.available
          ? <span className="pill pill-succeeded">Engine {sys.docker.version}</span>
          : <span className="pill pill-failed">Not connected</span>}
        <span className="spacer" />
        <button className="btn-ghost" onClick={scan} disabled={busy || !sys.docker.available}>↻ Scan labels now</button>
      </div>

      {!sys.docker.available && (
        <div className="callout warn">
          {sys.docker.error}. Mount the socket to enable <em>docker exec</em> backups and label discovery:
          <div className="code-block" style={{ marginTop: '0.5rem' }}>{'volumes:\n  - /var/run/docker.sock:/var/run/docker.sock:ro'}</div>
        </div>
      )}
      {sys.docker.available && (
        <p className="page-sub">
          Labels are scanned every minute{sys.docker.lastScan ? ` — last ${relative(sys.docker.lastScan)}` : ''}.
          {sys.docker.scanError && <span className="error-msg"> {sys.docker.scanError}</span>}
        </p>
      )}

      {sys.docker.issues.length > 0 && (
        <div className="callout danger">
          <strong>Label problems</strong>
          <ul style={{ margin: '0.3rem 0 0 1.1rem' }}>
            {sys.docker.issues.map(i => <li key={i.container + i.error}><code>{i.container}</code>: {i.error}</li>)}
          </ul>
        </div>
      )}

      {containers && (
        <>
          <h3 className="section-title">Containers</h3>
          {cError && <p className="error-msg">{cError}</p>}
          <div className="data-table">
            <table>
              <thead><tr><th>Container</th><th>Image</th><th>Engine</th><th>State</th><th>ACopy</th></tr></thead>
              <tbody>
                {containers.map(c => {
                  const t = byContainer.get(c.name)
                  return (
                    <tr key={c.id}>
                      <td><strong>{c.name}</strong></td>
                      <td className="mono muted">{c.image}</td>
                      <td>{c.detectedEngine ? <>{ENGINE_ICON[c.detectedEngine]} {c.detectedEngine}</> : <span className="muted">—</span>}</td>
                      <td><span className={`pill ${c.state === 'running' ? 'pill-succeeded' : 'pill-cancelled'}`}>{c.state}</span></td>
                      <td>
                        {t ? <a style={{ cursor: 'pointer' }} onClick={() => nav(`/targets/${t.id}`)}>{c.labelled ? '🏷 ' : ''}{t.name} →</a>
                          : c.detectedEngine && c.state === 'running'
                            ? <button className="btn-add btn-sm" onClick={() => nav('/targets/new')}>＋ Back up</button>
                            : <span className="muted">—</span>}
                      </td>
                    </tr>
                  )
                })}
              </tbody>
            </table>
          </div>
        </>
      )}

      <h3 className="section-title">Label reference</h3>
      <div className="card">
        <p className="hint" style={{ marginBottom: '0.75rem' }}>
          Put these on a database container and ACopy picks it up on the next scan. With the default <em>exec</em> mode the dump runs inside that
          container and the user/password are read from its own env (<code>POSTGRES_PASSWORD</code>, <code>MYSQL_ROOT_PASSWORD</code>, <code>MONGO_INITDB_ROOT_PASSWORD</code>, …).
        </p>
        <div className="code-block" style={{ marginBottom: '1rem' }}>{`services:
  postgres:
    image: postgres:17
    environment:
      POSTGRES_PASSWORD: secret
    labels:
      ${p}.enable: "true"
      ${p}.schedule: "0 3 * * *"   # daily 03:00
      ${p}.keep: "7"
      ${p}.naming: "rolling"       # postgres.0.sql.gz … postgres.6.sql.gz
      ${p}.encryption: "offsite"   # a key from Encryption keys
      ${p}.destinations: "s3,tg"   # destinations by name

  # Several targets from one container:
  #   ${p}.database: "app,analytics"          → postgres-app, postgres-analytics
  #   ${p}.jobs.hourly.schedule: "0 * * * *"  → postgres-hourly (inherits the rest)
  #   ${p}.jobs.hourly.naming: "rolling"`}</div>
        <div className="data-table">
          <table>
            <tbody>
              {meta.labelKeys.map(k => (
                <tr key={k.key}><td className="nowrap"><code>{p}.{k.key}</code></td><td className="muted">{k.description}</td></tr>
              ))}
            </tbody>
          </table>
        </div>
      </div>
      {toast}
    </div>
  )
}
