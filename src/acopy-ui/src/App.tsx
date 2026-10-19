import { BrowserRouter, Navigate, Route, Routes } from 'react-router-dom'
import AppLayout, { TargetLayout } from './layouts/AppLayout'
import Login from './pages/Login'
import Dashboard from './pages/Dashboard'
import Targets from './pages/Targets'
import TargetOverview from './pages/TargetOverview'
import TargetEdit from './pages/TargetEdit'
import Activity from './pages/Activity'
import Docker from './pages/Docker'
import Settings from './pages/Settings'
import { AuthProvider, useAuth } from './hooks/useAuth'
import { TargetsProvider } from './hooks/useTargets'

function Gate() {
  const { state } = useAuth()
  if (!state) return <p className="loading">Loading…</p>
  if (!state.authenticated) return <Login />
  return (
    <TargetsProvider>
      <Routes>
        <Route element={<AppLayout />}>
          <Route path="/" element={<Dashboard />} />
          <Route path="/targets" element={<Targets />} />
          <Route path="/targets/new" element={<TargetEdit />} />
          <Route path="/activity" element={<Activity />} />
          <Route path="/docker" element={<Docker />} />
          <Route path="/settings" element={<Settings />} />
        </Route>
        <Route path="/targets/:id" element={<TargetLayout />}>
          <Route index element={<TargetOverview />} />
          <Route path="edit" element={<TargetEdit />} />
        </Route>
        <Route path="*" element={<Navigate to="/" replace />} />
      </Routes>
    </TargetsProvider>
  )
}

export default function App() {
  return (
    <BrowserRouter>
      <AuthProvider>
        <Gate />
      </AuthProvider>
    </BrowserRouter>
  )
}
