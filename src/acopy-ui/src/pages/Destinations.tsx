import { useCallback, useEffect, useState } from 'react'
import { api, ApiError, apiError, type Backend, type Destination, type DestinationInput, type DestinationKind } from '../api'
import ToggleField from '../components/ToggleField'
import { useToast } from '../components/Bits'

const PUBLIC_API = 'https://api.telegram.org'

export default function Destinations() {
  const [list, setList] = useState<Destination[] | null>(null)
  const [backends, setBackends] = useState<Backend[]>([])
  const [editing, setEditing] = useState<Destination | 'new' | null>(null)
  const [toast, show] = useToast()

  const load = useCallback(async () => {
    try { setList(await api.destinations()) } catch (e) { show(apiError(e), true) }
  }, [show])
  useEffect(() => { load(); api.backends().then(setBackends) }, [load])

  const remove = async (d: Destination) => {
    if (!confirm(`Delete destination "${d.name}"? Databases stop copying there; files already uploaded stay where they are.`)) return
    try { await api.deleteDestination(d.id); await load(); show('Destination deleted') }
    catch (e) { show(apiError(e), true) }
  }

  const describe = (d: Destination) => {
    if (d.kind === 'Telegram') {
      const chats = (d.params.chatId ?? '').split(',').filter(Boolean)
      return `Telegram chat${chats.length === 1 ? '' : 's'} ${chats.join(', ')}${d.params.threadId ? ` · topic ${d.params.threadId}` : ''}${d.params.apiUrl ? ` · ${d.params.apiUrl}` : ''}`
    }
    const b = backends.find(x => x.type === d.rcloneType)
    const where = d.rcloneType === 'other' ? d.params.type : b?.label ?? d.rcloneType
    return `${where} · ${d.params.host ?? d.params.endpoint ?? d.params.url ?? ''} ${d.remotePath ?? ''}`.trim()
  }

  return (
    <div className="page">
      <div className="page-header">
        <h2>Destinations</h2>
        <span className="spacer" />
        {!editing && <button className="btn-primary" onClick={() => setEditing('new')}>＋ Add destination</button>}
      </div>
      <p className="page-sub">
        After each successful backup, the file is also copied to the destinations picked on that database
        (or <code>acopy.destinations=a,b</code>). Each destination applies its own retention.
      </p>

      {editing && (
        <DestinationForm
          existing={editing === 'new' ? null : editing}
          backends={backends}
          onDone={async saved => { setEditing(null); if (saved) { await load(); show('Destination saved') } }}
        />
      )}

      {!list ? <p className="loading">Loading…</p> : list.length === 0 && !editing ? (
        <p className="empty">No destinations — backups stay on this host only.</p>
      ) : (
        <div className="entity-list">
          {list.map(d => (
            <div key={d.id} className={`entity-card ${d.enabled ? 'st-ok' : 'st-paused'}`}>
              <div className="entity-card-left">
                <h3 onClick={() => setEditing(d)}>{d.kind === 'Telegram' ? '✈' : '☁'} {d.name}</h3>
                <div className="meta-row">
                  <span className={`chip ${d.kind === 'Telegram' ? 'chip-patina' : 'chip-accent'}`}>{d.kind === 'Telegram' ? 'Telegram' : `rclone · ${d.rcloneType}`}</span>
                  <span className="chip">{d.keep != null || d.maxAgeDays != null
                    ? `keep ${d.keep ? d.keep : '∞'}${d.maxAgeDays ? ` · ≤ ${d.maxAgeDays} days` : ''}`
                    : 'same retention as local'}</span>
                  <span className="chip">{d.usedBy} database{d.usedBy === 1 ? '' : 's'}</span>
                  {!d.enabled && <span className="chip chip-warn">disabled</span>}
                </div>
                <div className="meta-line mono">{describe(d)}</div>
              </div>
              <div className="entity-card-right">
                <div className="entity-card-actions">
                  <button className="btn-ghost btn-sm" onClick={() => setEditing(d)}>Edit</button>
                  <button className="btn-icon danger" onClick={() => remove(d)} title="Delete destination">🗑</button>
                </div>
              </div>
            </div>
          ))}
        </div>
      )}
      {toast}
    </div>
  )
}

function DestinationForm({ existing, backends, onDone }: {
  existing: Destination | null
  backends: Backend[]
  onDone: (saved: boolean) => void
}) {
  const [name, setName] = useState(existing?.name ?? '')
  const [kind, setKind] = useState<DestinationKind>(existing?.kind ?? 'Rclone')
  const [enabled, setEnabled] = useState(existing?.enabled ?? true)
  const [type, setType] = useState(existing?.rcloneType ?? 's3')
  const [params, setParams] = useState<Record<string, string>>(existing?.params ?? {})
  const [secrets, setSecrets] = useState<Record<string, string>>({})
  const [clear, setClear] = useState<string[]>([])
  const [remotePath, setRemotePath] = useState(existing?.remotePath ?? '')
  const [customRetention, setCustomRetention] = useState(existing?.keep != null || existing?.maxAgeDays != null)
  const [keep, setKeep] = useState<string>(existing?.keep?.toString() ?? '')
  const [maxAge, setMaxAge] = useState<string>(existing?.maxAgeDays?.toString() ?? '')
  const [errors, setErrors] = useState<Record<string, string[]>>({})
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)
  const [test, setTest] = useState<{ ok: boolean; message: string } | null>(null)
  const [defaultApi, setDefaultApi] = useState(PUBLIC_API)
  useEffect(() => { api.meta().then(m => setDefaultApi(m.telegramApiUrl)).catch(() => {}) }, [])

  const backend = backends.find(b => b.type === type)
  const effectiveApi = (params.apiUrl?.trim() || defaultApi).replace(/\/$/, '')
  const selfHosted = effectiveApi !== PUBLIC_API
  const stored = new Set(existing?.secretKeys ?? [])

  const input = (): DestinationInput => ({
    name, kind, enabled,
    rcloneType: kind === 'Rclone' ? type : null,
    params, secrets, clearSecrets: clear,
    remotePath: kind === 'Rclone' ? remotePath : null,
    keep: customRetention && keep !== '' ? Number(keep) : null,
    maxAgeDays: customRetention && maxAge !== '' ? Number(maxAge) : null,
  })

  const save = async () => {
    setBusy(true); setErrors({}); setError('')
    try {
      if (existing) await api.updateDestination(existing.id, input())
      else await api.createDestination(input())
      onDone(true)
    } catch (e) {
      if (e instanceof ApiError) { setErrors(e.fields); setError(e.message) } else setError(apiError(e))
    } finally { setBusy(false) }
  }

  const doTest = async () => {
    setBusy(true); setTest(null)
    try { setTest(await api.testDestination(input(), existing?.id)) }
    catch (e) { setTest({ ok: false, message: apiError(e) }) }
    finally { setBusy(false) }
  }

  const logOutPublic = async () => {
    if (!confirm('Log this bot out of api.telegram.org so it can use the self-hosted Bot API server?\n\n' +
      'Do this once, when moving the bot. It cannot log back in to the public server for 10 minutes, and anything ' +
      'else that uses this bot through api.telegram.org stops working.')) return
    setBusy(true); setTest(null)
    try { setTest(await api.telegramLogout(input(), existing?.id)) }
    catch (e) { setTest({ ok: false, message: apiError(e) }) }
    finally { setBusy(false) }
  }

  const err = (k: string) => errors[k]?.map(m => <span key={m} className="field-error">{m}</span>)
  const setP = (k: string, v: string) => setParams(p => ({ ...p, [k]: v }))
  const setS = (k: string, v: string) => setSecrets(s => ({ ...s, [k]: v }))

  const secretInput = (key: string, label: string, multiline = false, placeholder?: string) => (
    <label key={key} className={multiline ? 'span-2' : ''}>
      {label}
      {multiline
        ? <textarea className="mono" value={secrets[key] ?? ''} onChange={e => setS(key, e.target.value)}
                    placeholder={stored.has(key) && !clear.includes(key) ? '•••••• (unchanged)' : placeholder} />
        : <input type="password" autoComplete="new-password" value={secrets[key] ?? ''} onChange={e => setS(key, e.target.value)}
                 placeholder={stored.has(key) && !clear.includes(key) ? '•••••• (unchanged)' : placeholder} />}
      {stored.has(key) && (
        <span className="hint">
          <a style={{ cursor: 'pointer' }} onClick={() => setClear(c => c.includes(key) ? c.filter(x => x !== key) : [...c, key])}>
            {clear.includes(key) ? 'Keep the stored value' : 'Clear the stored value'}
          </a>
        </span>
      )}
      {err(`secrets.${key}`) ?? err(`params.${key}`)}
    </label>
  )

  return (
    <div className="card" style={{ marginBottom: '1.25rem' }}>
      <h2>{existing ? `Edit ${existing.name}` : 'New destination'}</h2>
      <div className="form-grid">
        <label>Name<input value={name} onChange={e => setName(e.target.value)} placeholder="offsite-s3" />{err('name')}</label>
        <label>
          Kind
          <div className="segmented">
            <button type="button" className={kind === 'Rclone' ? 'active' : ''} onClick={() => setKind('Rclone')} disabled={!!existing}>☁ Storage (rclone)</button>
            <button type="button" className={kind === 'Telegram' ? 'active' : ''} onClick={() => setKind('Telegram')} disabled={!!existing}>✈ Telegram</button>
          </div>
        </label>
      </div>

      {kind === 'Rclone' ? (
        <>
          <label>
            Backend
            <div className="segmented">
              {backends.map(b => (
                <button key={b.type} type="button" className={type === b.type ? 'active' : ''} onClick={() => { setType(b.type); setTest(null) }}>{b.label}</button>
              ))}
            </div>
            {err('rcloneType')}
          </label>
          <div className="form-grid">
            {backend?.fields.map(f => {
              if (f.secret) return secretInput(f.key, f.label + (f.required ? '' : ' (optional)'), f.key.includes('pem') || f.key.includes('options'), f.placeholder ?? undefined)
              const multiline = f.key === 'options'
              return (
                <label key={f.key} className={multiline ? 'span-2' : ''}>
                  {f.label}{f.required ? '' : ' (optional)'}
                  {f.options ? (
                    <select value={params[f.key] ?? ''} onChange={e => setP(f.key, e.target.value)}>
                      {!f.options.includes('') && <option value="">—</option>}
                      {f.options.map(o => <option key={o} value={o}>{o || '(default)'}</option>)}
                    </select>
                  ) : multiline ? (
                    <textarea className="mono" value={params[f.key] ?? ''} onChange={e => setP(f.key, e.target.value)} placeholder={f.placeholder ?? ''} />
                  ) : (
                    <input value={params[f.key] ?? ''} onChange={e => setP(f.key, e.target.value)} placeholder={f.placeholder ?? ''} />
                  )}
                  {f.hint && <span className="hint">{f.hint}</span>}
                  {err(`params.${f.key}`)}
                </label>
              )
            })}
            <label className="span-2">
              Remote path
              <input className="mono" value={remotePath} onChange={e => setRemotePath(e.target.value)} placeholder={backend?.pathHint ?? ''} />
              <span className="hint">Each database uploads into its folder below this path, e.g. <code>{(remotePath || backend?.pathHint || '').replace(/\/$/, '')}/my-db/</code></span>
            </label>
          </div>
        </>
      ) : (
        <>
          <p className="callout-info" style={{ marginBottom: '0.9rem' }}>
            Create a bot with @BotFather, add it to the chat (or open a private chat with it), and use the chat id
            (e.g. <code>-1001234567890</code> for groups). {selfHosted
              ? <>This destination uses the self-hosted Bot API server at <code>{effectiveApi}</code> — files up to 2 GB
                  per part.</>
              : <>The public Bot API accepts files up to 50 MB — larger backups are split into numbered parts
                  (<code>cat file.* &gt; file</code> to join). A self-hosted Bot API server raises that to 2 GB (see the README).</>}
          </p>
          <div className="form-grid">
            {secretInput('botToken', 'Bot token', false, '123456:ABC…')}
            <label>
              Chat ids
              <input className="mono" value={params.chatId ?? ''} onChange={e => setP('chatId', e.target.value)} placeholder="-1001234567890, 123456789" />
              <span className="hint">Comma-separated; each chat gets every file. A database can name its own chats instead.</span>
              {err('params.chatId')}
            </label>
            <label>Topic / thread id (optional)<input value={params.threadId ?? ''} onChange={e => setP('threadId', e.target.value)} />{err('params.threadId')}</label>
            <label>Part size, MB (optional)<input value={params.maxPartMb ?? ''} onChange={e => setP('maxPartMb', e.target.value)} placeholder={selfHosted ? '1990' : '49'} />{err('params.maxPartMb')}</label>
            <label>
              Bot API URL (optional)
              <input className="mono" value={params.apiUrl ?? ''} onChange={e => setP('apiUrl', e.target.value)} placeholder={defaultApi} />
              <span className="hint">
                Blank = {defaultApi === PUBLIC_API ? 'the public server' : <>the server set by <code>ACOPY_Telegram__ApiUrl</code></>}.
                Self-hosted: <code>http://telegram-bot-api:8081</code>
              </span>
              {err('params.apiUrl')}
            </label>
          </div>
        </>
      )}

      <ToggleField checked={customRetention} onChange={setCustomRetention} label="Own retention"
                   hint="Off = mirror the database's local retention. On = e.g. keep more copies offsite." />
      {customRetention && (
        <div className="form-grid" style={{ marginTop: '0.75rem' }}>
          <label>Copies to keep<input type="number" min={0} value={keep} onChange={e => setKeep(e.target.value)} placeholder="0 = unlimited" />{err('keep')}</label>
          <label>Maximum age (days)<input type="number" min={1} value={maxAge} onChange={e => setMaxAge(e.target.value)} placeholder="no limit" />{err('maxAgeDays')}</label>
        </div>
      )}
      <div style={{ marginTop: '0.75rem' }}>
        <ToggleField checked={enabled} onChange={setEnabled} label="Enabled" hint="Off = keep the configuration but stop uploading" />
      </div>

      {error && <p className="error-msg" style={{ marginTop: '0.75rem' }}>{error}</p>}
      <div className="form-actions" style={{ marginTop: '0.75rem' }}>
        <button className="btn-primary" onClick={save} disabled={busy || !name}>{existing ? 'Save' : 'Add destination'}</button>
        <button className="btn-ghost" onClick={doTest} disabled={busy}>🔌 Test</button>
        {kind === 'Telegram' && selfHosted && (
          <button className="btn-ghost" onClick={logOutPublic} disabled={busy}
                  title="One-time step when moving a bot from api.telegram.org to a self-hosted server">🚚 Log out from public API</button>
        )}
        <button className="btn-ghost" onClick={() => onDone(false)}>Cancel</button>
        {test && <span className={test.ok ? 'ok-msg' : 'error-msg'}>{test.ok ? '✓' : '✗'} {test.message}</span>}
      </div>
    </div>
  )
}
