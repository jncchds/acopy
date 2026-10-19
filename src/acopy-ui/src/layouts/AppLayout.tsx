import { Outlet, useNavigate, useParams } from 'react-router-dom'
import Sidebar, { SidebarBtn, SidebarDivider, SidebarLink, SidebarSection } from '../components/Sidebar'
import { useAuth } from '../hooks/useAuth'
import { useTargets } from '../hooks/useTargets'
import { api, ENGINE_ICON, STATE_COLOR, describeCron, targetState } from '../api'

/** Global rail: destinations, then every target as a two-line entry. */
export default function AppLayout() {
  const { targets } = useTargets()
  const nav = useNavigate()
  const failing = targets?.some(t => targetState(t) === 'failed') ?? false

  return (
    <div className="app-layout">
      <Sidebar hasAlert={failing} bottomChildren={<BottomActions />}>
        <SidebarLink to="/" icon="◈" label="Dashboard" />
        <SidebarLink to="/targets" icon="🗃" label="Databases" />
        <SidebarLink to="/activity" icon="📜" label="Activity" />
        <SidebarLink to="/destinations" icon="☁" label="Destinations" />
        <SidebarLink to="/keys" icon="🔐" label="Encryption keys" />
        <SidebarLink to="/docker" icon="🐳" label="Docker" />
        <SidebarLink to="/settings" icon="⚙" label="Settings" />
        <SidebarDivider />
        <SidebarBtn icon="＋" label="Add database" onClick={() => nav('/targets/new')} />
        {targets && targets.length > 0 && (
          <>
            <SidebarSection title="Databases" />
            <div className="sidebar-entry-list">
              {targets.map(t => {
                const st = targetState(t)
                return (
                  <button key={t.id} className="sidebar-entry" onClick={() => nav(`/targets/${t.id}`)} title={t.name}>
                    <span className="s-e-icon" style={{ background: STATE_COLOR[st] }} />
                    <span className="s-e-content">
                      <span className="s-e-title">{ENGINE_ICON[t.engine]} {t.name}</span>
                      <span className="s-e-sub">{st === 'running' ? 'Backing up…' : st === 'paused' ? 'Paused' : describeCron(t.schedule)}</span>
                    </span>
                  </button>
                )
              })}
            </div>
          </>
        )}
      </Sidebar>
      <main className="app-main"><Outlet /></main>
    </div>
  )
}

export function BottomActions() {
  const { state, signOut } = useAuth()
  if (state?.disabled) return null
  return <SidebarBtn icon="🚪" label="Sign out" onClick={signOut} />
}

/** Entity rail for one target: back link, identity, verbs, then sub-views. */
export function TargetLayout() {
  const { id } = useParams()
  const { targets } = useTargets()
  const nav = useNavigate()
  const t = targets?.find(x => x.id === Number(id))

  return (
    <div className="app-layout">
      <Sidebar hasAlert={t ? targetState(t) === 'failed' : false} bottomChildren={<BottomActions />}>
        <SidebarBtn icon="←" label="All databases" onClick={() => nav('/targets')} />
        {t && (
          <>
            <div className="sidebar-entity-info">
              <span className="s-title">{ENGINE_ICON[t.engine]} {t.name}</span>
              <span className="s-meta">{t.engine} · {t.mode === 'DockerExec' ? 'docker exec' : 'network'}{t.source === 'DockerLabel' ? ' · labels' : ''}</span>
            </div>
            <SidebarDivider />
            <TargetActions />
            <SidebarDivider />
            <SidebarLink to={`/targets/${t.id}`} icon="📦" label="Backups" />
            <SidebarLink to={`/targets/${t.id}/edit`} icon="⚙" label={t.source === 'DockerLabel' ? 'Configuration' : 'Edit'} />
          </>
        )}
      </Sidebar>
      <main className="app-main"><Outlet /></main>
    </div>
  )
}

function TargetActions() {
  const { id } = useParams()
  const { targets, reload } = useTargets()
  const t = targets?.find(x => x.id === Number(id))
  if (!t) return null

  const act = async (fn: () => Promise<unknown>) => {
    try { await fn() } catch (e) { alert(e instanceof Error ? e.message : String(e)) }
    await reload()
  }

  if (t.active) {
    return (
      <>
        <div className="running-banner"><span className="spinner" /><span className="hide-when-collapsed">Backing up…</span></div>
        <SidebarBtn icon="⊙" label="Cancel backup" onClick={() => act(() => api.cancel(t.id))} />
      </>
    )
  }
  return (
    <>
      <SidebarBtn icon="▶" label="Back up now" disabled={t.orphaned}
                  onClick={() => act(() => api.runNow(t.id))} />
      <SidebarBtn icon={t.enabled ? '⏸' : '⏵'} label={t.enabled ? 'Pause schedule' : 'Resume schedule'}
                  onClick={() => act(() => api.setEnabled(t.id, !t.enabled))} />
    </>
  )
}
