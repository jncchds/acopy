import { useEffect, useState } from 'react'
import { api, apiError, type Settings as S, type SystemStatus } from '../api'
import ToggleField from '../components/ToggleField'
import { useToast } from '../components/Bits'
import { useAuth } from '../hooks/useAuth'

export default function Settings() {
  const [s, setS] = useState<S | null>(null)
  const [sys, setSys] = useState<SystemStatus | null>(null)
  const [zones, setZones] = useState<string[]>([])
  const [busy, setBusy] = useState(false)
  const [toast, show] = useToast()
  const { state } = useAuth()
  const [cur, setCur] = useState('')
  const [pw, setPw] = useState('')
  const [tgToken, setTgToken] = useState('')
  const [tgClear, setTgClear] = useState(false)

  useEffect(() => {
    api.settings().then(setS)
    api.system().then(setSys)
    api.timezones().then(setZones)
  }, [])

  const persist = async () => {
    if (!s) return
    const saved = await api.saveSettings({
      timeZone: s.timeZone, webhookUrl: s.webhookUrl, notifyOnSuccess: s.notifyOnSuccess,
      telegramToken: tgToken || null, clearTelegramToken: tgClear,
      telegramChatId: s.telegramChatId, telegramThreadId: s.telegramThreadId, telegramApiUrl: s.telegramApiUrl,
    })
    setS(saved)
    setTgToken('')
    setTgClear(false)
  }

  const save = async () => {
    setBusy(true)
    try { await persist(); show('Settings saved') }
    catch (e) { show(apiError(e), true) }
    finally { setBusy(false) }
  }

  const testHook = async () => {
    try { await persist(); await api.testWebhook(); show('Test notification sent') }
    catch (e) { show(apiError(e), true) }
  }

  const testTelegram = async () => {
    try { await persist(); await api.testTelegram(); show('Telegram message sent') }
    catch (e) { show(apiError(e), true) }
  }

  const changePw = async () => {
    try { await api.changePassword(cur, pw); setCur(''); setPw(''); show('Password changed') }
    catch (e) { show(apiError(e), true) }
  }

  if (!s) return <p className="loading">Loading…</p>

  return (
    <div className="page page-narrow">
      <div className="page-header"><h2>Settings</h2></div>

      <div className="card">
        <h2>Schedule</h2>
        <label>
          Time zone (cron schedules and file timestamps)
          <input list="tz-list" value={s.timeZone} onChange={e => setS({ ...s, timeZone: e.target.value })} />
          <datalist id="tz-list">{zones.map(z => <option key={z} value={z} />)}</datalist>
          <span className="hint">Defaults to the container’s <code>TZ</code> variable.</span>
        </label>
      </div>

      <div className="card">
        <h2>Notifications</h2>
        <label>
          Webhook URL
          <input value={s.webhookUrl ?? ''} onChange={e => setS({ ...s, webhookUrl: e.target.value })} placeholder="https://discord.com/api/webhooks/… or any JSON endpoint" />
          <span className="hint">ACopy POSTs JSON with <code>text</code> and <code>content</code> fields (Slack / Discord / Mattermost compatible) plus the full run details.</span>
        </label>
        <ToggleField checked={s.notifyOnSuccess} onChange={v => setS({ ...s, notifyOnSuccess: v })}
                     label="Also notify on success" hint="Failures are always sent" />
        <div className="form-actions" style={{ marginTop: '0.75rem' }}>
          <button className="btn-ghost" onClick={testHook} disabled={!s.webhookUrl}>Send test</button>
        </div>

        <h3 className="section-title">✈ Telegram</h3>
        <div className="form-grid">
          <label>
            Bot token
            <input type="password" autoComplete="new-password" value={tgToken} onChange={e => setTgToken(e.target.value)}
                   placeholder={s.telegramHasToken && !tgClear ? '•••••• (unchanged)' : '123456:ABC… from @BotFather'} />
            {s.telegramHasToken && (
              <span className="hint"><a style={{ cursor: 'pointer' }} onClick={() => setTgClear(c => !c)}>{tgClear ? 'Keep the stored token' : 'Remove the stored token'}</a></span>
            )}
          </label>
          <label>
            Chat id
            <input value={s.telegramChatId ?? ''} onChange={e => setS({ ...s, telegramChatId: e.target.value })} placeholder="-1001234567890 or your user id" />
          </label>
          <label>
            Topic / thread id (optional)
            <input value={s.telegramThreadId ?? ''} onChange={e => setS({ ...s, telegramThreadId: e.target.value })} />
          </label>
          <label>
            Bot API URL (optional)
            <input value={s.telegramApiUrl ?? ''} onChange={e => setS({ ...s, telegramApiUrl: e.target.value })} placeholder="https://api.telegram.org" />
          </label>
        </div>
        <p className="hint">Same rules as the webhook: failures always, successes when the toggle above is on. To send the backup files themselves, add a Telegram <em>destination</em>.</p>
        <div className="form-actions" style={{ marginTop: '0.75rem' }}>
          <button className="btn-ghost" onClick={testTelegram} disabled={!(s.telegramHasToken || tgToken) || !s.telegramChatId}>Send test</button>
        </div>
      </div>

      <div className="form-actions" style={{ margin: '1rem 0 1.5rem' }}>
        <button className="btn-primary" onClick={save} disabled={busy}>Save settings</button>
      </div>

      {state && !state.disabled && (
        <div className="card">
          <h2>Password</h2>
          {state.passwordFromEnv ? (
            <p className="hint">Set by <code>ACOPY_Auth__Password</code> — change it there.</p>
          ) : (
            <>
              <div className="form-grid">
                <label>Current<input type="password" value={cur} onChange={e => setCur(e.target.value)} autoComplete="current-password" /></label>
                <label>New<input type="password" value={pw} onChange={e => setPw(e.target.value)} autoComplete="new-password" /></label>
              </div>
              <button className="btn-ghost" onClick={changePw} disabled={!cur || pw.length < 8}>Change password</button>
            </>
          )}
        </div>
      )}

      {sys && (
        <div className="card">
          <h2>System</h2>
          <div className="data-table">
            <table>
              <tbody>
                <tr><td className="muted">Version</td><td>v{sys.version}</td></tr>
                <tr><td className="muted">Database</td><td className="mono">{sys.databasePath}</td></tr>
                <tr><td className="muted">Backups</td><td className="mono">{sys.backupsPath}</td></tr>
                <tr><td className="muted">Docker</td><td>{sys.docker.available ? `Engine ${sys.docker.version} via ${sys.docker.socket}` : sys.docker.error}</td></tr>
                {sys.tools.map(t => (
                  <tr key={t.tool}><td className="muted mono">{t.tool}</td><td className="mono">{t.version ?? <span className="error-msg">not available</span>}</td></tr>
                ))}
              </tbody>
            </table>
          </div>
        </div>
      )}
      {toast}
    </div>
  )
}
