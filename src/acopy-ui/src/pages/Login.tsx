import { useState } from 'react'
import { api, apiError } from '../api'
import { useAuth } from '../hooks/useAuth'

export default function Login() {
  const { state, refresh } = useAuth()
  const setup = state?.setupRequired === true
  const [password, setPassword] = useState('')
  const [confirm, setConfirm] = useState('')
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)

  const submit = async (e: React.FormEvent) => {
    e.preventDefault()
    setError('')
    if (setup && password !== confirm) { setError('Passwords do not match.'); return }
    setBusy(true)
    try {
      if (setup) await api.setup(password)
      else await api.login(password)
      await refresh()
    } catch (err) {
      setError(apiError(err))
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="login-wrap">
      <form className="card login-card" onSubmit={submit}>
        <div className="login-brand">
          <h1>A<span>Copy</span></h1>
          <span className="version-pill">v{__APP_VERSION__}</span>
        </div>
        {setup ? (
          <p className="callout-info" style={{ marginBottom: '1rem' }}>
            First run — choose the admin password. You can also set it with <code>ACOPY_Auth__Password</code>.
          </p>
        ) : null}
        <label>
          {setup ? 'New password' : 'Password'}
          <input type="password" autoFocus value={password} onChange={e => setPassword(e.target.value)} autoComplete={setup ? 'new-password' : 'current-password'} />
        </label>
        {setup && (
          <label>
            Confirm password
            <input type="password" value={confirm} onChange={e => setConfirm(e.target.value)} autoComplete="new-password" />
          </label>
        )}
        {error && <p className="error-msg" style={{ marginBottom: '0.75rem' }}>{error}</p>}
        <button className="btn-primary" style={{ width: '100%' }} disabled={busy || !password}>
          {setup ? 'Set password' : 'Sign in'}
        </button>
      </form>
    </div>
  )
}
