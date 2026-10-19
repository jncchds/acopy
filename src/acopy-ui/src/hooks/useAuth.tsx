import { createContext, useCallback, useContext, useEffect, useState } from 'react'
import { api, UNAUTHORIZED_EVENT, type AuthState } from '../api'

interface AuthCtx {
  state: AuthState | null
  refresh: () => Promise<void>
  signOut: () => Promise<void>
}

const Ctx = createContext<AuthCtx>({ state: null, refresh: async () => {}, signOut: async () => {} })

export function AuthProvider({ children }: { children: React.ReactNode }) {
  const [state, setState] = useState<AuthState | null>(null)

  const refresh = useCallback(async () => {
    try { setState(await api.authState()) }
    catch { setState({ disabled: false, setupRequired: false, authenticated: false, passwordFromEnv: false }) }
  }, [])

  useEffect(() => {
    refresh()
    const onUnauthorized = () => setState(s => (s ? { ...s, authenticated: false } : s))
    window.addEventListener(UNAUTHORIZED_EVENT, onUnauthorized)
    return () => window.removeEventListener(UNAUTHORIZED_EVENT, onUnauthorized)
  }, [refresh])

  const signOut = async () => {
    await api.logout().catch(() => {})
    await refresh()
  }

  return <Ctx.Provider value={{ state, refresh, signOut }}>{children}</Ctx.Provider>
}

export const useAuth = () => useContext(Ctx)
