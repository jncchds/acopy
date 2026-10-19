import { useCallback, useEffect, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { api, apiError, type Run } from '../api'
import { RunsTable } from '../components/Bits'
import { useTargets } from '../hooks/useTargets'

const FILTERS = ['', 'Succeeded', 'Failed', 'Running', 'Cancelled'] as const

export default function Activity() {
  const [runs, setRuns] = useState<Run[] | null>(null)
  const [status, setStatus] = useState<string>('')
  const [error, setError] = useState('')
  const { targets } = useTargets()
  const nav = useNavigate()

  const load = useCallback(async () => {
    try { setRuns(await api.runs({ status: status || undefined, limit: 300 })) }
    catch (e) { setError(apiError(e)) }
  }, [status])

  useEffect(() => { load() }, [load, targets])

  return (
    <div className="page">
      <div className="page-header"><h2>Activity</h2></div>
      <div className="tabs">
        {FILTERS.map(f => (
          <button key={f} className={`tab${status === f ? ' active' : ''}`} onClick={() => setStatus(f)}>{f || 'All'}</button>
        ))}
      </div>
      {error && <p className="error-msg">{error}</p>}
      {!runs ? <p className="loading">Loading…</p> : (
        <RunsTable runs={runs} showTarget onOpen={r => nav(`/targets/${r.targetId}?run=${r.id}`)} onChanged={load} />
      )}
    </div>
  )
}
