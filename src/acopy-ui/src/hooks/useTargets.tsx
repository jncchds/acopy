import { createContext, useCallback, useContext, useEffect, useRef, useState } from 'react'
import { api, type Target } from '../api'

interface TargetsCtx {
  targets: Target[] | null
  error: string | null
  reload: () => Promise<void>
}

const Ctx = createContext<TargetsCtx>({ targets: null, error: null, reload: async () => {} })

/**
 * One shared, polled list of targets for the rail and every page. Polls every 3 s while any
 * backup is running (so progress shows up promptly), otherwise every 20 s.
 */
export function TargetsProvider({ children }: { children: React.ReactNode }) {
  const [targets, setTargets] = useState<Target[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const timer = useRef<number | undefined>(undefined)

  const reload = useCallback(async () => {
    window.clearTimeout(timer.current)
    let anyActive = false
    try {
      const list = await api.targets()
      setTargets(list)
      setError(null)
      anyActive = list.some(t => t.active)
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    }
    timer.current = window.setTimeout(() => { reload() }, anyActive ? 3000 : 20000)
  }, [])

  useEffect(() => {
    reload()
    return () => window.clearTimeout(timer.current)
  }, [reload])

  return <Ctx.Provider value={{ targets, error, reload }}>{children}</Ctx.Provider>
}

export const useTargets = () => useContext(Ctx)
