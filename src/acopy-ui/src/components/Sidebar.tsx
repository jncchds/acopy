import { useState } from 'react'
import { NavLink } from 'react-router-dom'
import { useTheme, type Theme } from '../hooks/useTheme'

interface SidebarProps {
  hasAlert?: boolean
  children: React.ReactNode
  bottomChildren?: React.ReactNode
}

/** The single left rail: pinned brand row, scrolling contextual nav, pinned bottom actions + theme. */
export default function Sidebar({ hasAlert, children, bottomChildren }: SidebarProps) {
  const [collapsed, setCollapsed] = useState(() => localStorage.getItem('sidebarCollapsed') === 'true')
  const { theme, setTheme } = useTheme()

  const toggle = () => {
    const next = !collapsed
    setCollapsed(next)
    localStorage.setItem('sidebarCollapsed', String(next))
  }

  const nextTheme: Theme = theme === 'light' ? 'dark' : theme === 'dark' ? 'system' : 'light'
  const themeIcon = theme === 'light' ? '☀' : theme === 'dark' ? '🌙' : '⬡'

  return (
    <aside className={`app-sidebar ${collapsed ? 'collapsed' : 'expanded'}`}>
      <div className="sidebar-fixed-top">
        <div className="sidebar-top-row">
          <button className="sidebar-btn sidebar-toggle-btn" onClick={toggle} title={collapsed ? 'Expand sidebar' : 'Collapse sidebar'}>
            <span className="s-icon">☰</span>
            {!collapsed && <span className="s-label">ACopy</span>}
            {hasAlert && <span className="s-pending-dot" title="A backup is failing" />}
          </button>
          {!collapsed && (
            <>
              <a className="version-pill" href="https://github.com/jncchds/acopy" target="_blank" rel="noopener noreferrer" title="ACopy on GitHub">
                v{__APP_VERSION__}
              </a>
              <span className="beta-badge">ALPHA</span>
            </>
          )}
        </div>
      </div>

      <div className="sidebar-scroll">{children}</div>

      <div className="sidebar-fixed-bottom">
        {bottomChildren}
        <button className="sidebar-btn" onClick={() => setTheme(nextTheme)} title={`Theme: ${theme}`}>
          <span className="s-icon">{themeIcon}</span>
          <span className="s-label">Theme: {theme}</span>
        </button>
      </div>
    </aside>
  )
}

interface SidebarBtnProps {
  icon: string
  label: string
  onClick?: () => void
  active?: boolean
  disabled?: boolean
  dot?: boolean
  dotColor?: string
  title?: string
}

export function SidebarBtn({ icon, label, onClick, active, disabled, dot, dotColor, title }: SidebarBtnProps) {
  return (
    <button className={`sidebar-btn${active ? ' active' : ''}`} onClick={onClick} disabled={disabled} title={title ?? label}>
      <span className="s-icon">{icon}</span>
      <span className="s-label">{label}</span>
      {dot && <span className="s-dot" style={dotColor ? { background: dotColor } : undefined} />}
    </button>
  )
}

/** A rail destination backed by a route; active state comes from the router. */
export function SidebarLink({ to, icon, label, end = true, dot, dotColor }: {
  to: string; icon: string; label: string; end?: boolean; dot?: boolean; dotColor?: string
}) {
  return (
    <NavLink to={to} end={end} className="nav-link" title={label}>
      {({ isActive }) => (
        <span className={`sidebar-btn${isActive ? ' active' : ''}`}>
          <span className="s-icon">{icon}</span>
          <span className="s-label">{label}</span>
          {dot && <span className="s-dot" style={dotColor ? { background: dotColor } : undefined} />}
        </span>
      )}
    </NavLink>
  )
}

export function SidebarDivider() {
  return <div className="sidebar-divider" />
}

export function SidebarSection({ title }: { title: string }) {
  return <div className="sidebar-section-title hide-when-collapsed"><span className="s-label">{title}</span></div>
}
