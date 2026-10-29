import { useEffect, useMemo, useState } from 'react'
import { useNavigate, useParams, useSearchParams } from 'react-router-dom'
import {
  api, ApiError, apiError, describeCron, ENGINE_ICON,
  type Compression, type Container, type Destination, type EncKey, type Engine, type EngineInfo, type Meta, type Mode, type Naming, type NetworkReach, type Target, type TargetInput, type TestResult,
} from '../api'
import ToggleField from '../components/ToggleField'
import { useTargets } from '../hooks/useTargets'

const SCHEDULE_PRESETS: [string, string][] = [
  ['Hourly', '0 * * * *'],
  ['Every 6 h', '0 */6 * * *'],
  ['Daily 03:00', '0 3 * * *'],
  ['Weekly (Sun)', '0 3 * * 0'],
  ['Monthly', '0 3 1 * *'],
]

const EMPTY: TargetInput = {
  name: '', engine: 'PostgreSql', mode: 'DockerExec', host: '', port: null, containerName: '', username: '', password: '',
  passwordEnv: '', databaseName: '', extraArgs: '', customExtension: '', schedule: '0 3 * * *', enabled: true, keep: 7,
  naming: 'Timestamp', compression: 'Gzip', folder: '', timeoutMinutes: 120,
  maxAgeDays: null, encryptionKeyId: null, destinationIds: [], telegramChatIds: '', restoreCommand: '',
}

function fromTarget(t: Target): TargetInput {
  return {
    name: t.name, engine: t.engine, mode: t.mode, host: t.host ?? '', port: t.port, containerName: t.containerName ?? '',
    username: t.username ?? '', password: '', passwordEnv: t.passwordEnv ?? '', databaseName: t.databaseName ?? '',
    extraArgs: t.extraArgs ?? '', customExtension: t.customExtension ?? '', schedule: t.schedule, enabled: t.enabled,
    keep: t.keep, naming: t.naming, compression: t.compression, folder: t.folder, timeoutMinutes: t.timeoutMinutes,
    maxAgeDays: t.maxAgeDays, encryptionKeyId: t.encryptionKeyId, destinationIds: t.destinationIds, telegramChatIds: t.telegramChatIds ?? '', restoreCommand: t.restoreCommand ?? '',
  }
}

const REACH_STYLE: Record<NetworkReach['level'], [string, string]> = {
  Good: ['ok', '✅'],
  Fragile: ['warn', '⚠️'],
  Unreachable: ['danger', '⛔'],
  Unknown: ['', 'ℹ️'],
}

/** What the networks say about reaching the picked container, plus the live TCP check. */
function ReachCallout({ reach, host, port }: { reach: NetworkReach; host: string | null; port: number | null }) {
  const [cls, icon] = REACH_STYLE[reach.level]
  const edited = (host ?? '') !== (reach.host ?? '') || (port ?? null) !== (reach.port ?? null)
  return (
    <div className={`callout span-2 ${cls}`} style={{ marginBottom: '0.9rem' }}>
      <div>
        {icon} {reach.message}{' '}
        {reach.reachable === true && <span className="pill pill-succeeded">TCP {reach.host}:{reach.port} open</span>}
        {reach.reachable === false && <span className="pill pill-failed">TCP {reach.host}:{reach.port} — {reach.probeError}</span>}
      </div>
      {edited && <div className="hint">The host or port below was changed from this suggestion — 🔌 Test connection checks the values you entered.</div>}
      {reach.fix && (
        <>
          <div className="hint" style={{ marginTop: '0.4rem' }}>
            To reach it by name, attach {reach.acopyContainer ? <code>{reach.acopyContainer}</code> : 'ACopy'} to the database’s network
            (or run <code>docker network connect {reach.networks.find(n => n !== 'bridge') ?? '<network>'} {reach.acopyContainer ?? 'acopy'}</code>):
          </div>
          <div className="code-block" style={{ marginTop: '0.3rem' }}>{reach.fix}</div>
        </>
      )}
    </div>
  )
}

function slug(s: string) {
  return s.trim().toLowerCase().replace(/[^a-z0-9.-]+/g, '-').replace(/^[-.]+|[-.]+$/g, '') || 'backup'
}

export default function TargetEdit() {
  const { id } = useParams()
  const isNew = !id
  const nav = useNavigate()
  const { targets, reload } = useTargets()
  const existing = isNew ? undefined : targets?.find(t => t.id === Number(id))

  const [meta, setMeta] = useState<Meta | null>(null)
  const [f, setF] = useState<TargetInput>(EMPTY)
  const [loaded, setLoaded] = useState(isNew)
  const [errors, setErrors] = useState<Record<string, string[]>>({})
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)
  const [test, setTest] = useState<TestResult | null>(null)
  const [containers, setContainers] = useState<Container[] | null>(null)
  const [dockerError, setDockerError] = useState('')
  const [hint, setHint] = useState('')
  const [reach, setReach] = useState<NetworkReach | null>(null)
  const [deleteFiles, setDeleteFiles] = useState(false)
  const [keys, setKeys] = useState<EncKey[]>([])
  const [dests, setDests] = useState<Destination[]>([])

  useEffect(() => {
    api.meta().then(setMeta).catch(e => setError(apiError(e)))
    api.keys().then(setKeys).catch(() => {})
    api.destinations().then(setDests).catch(() => {})
  }, [])
  useEffect(() => {
    api.containers().then(setContainers).catch(e => setDockerError(apiError(e)))
  }, [])
  useEffect(() => {
    if (!loaded && existing) { setF(fromTarget(existing)); setLoaded(true) }
  }, [existing, loaded])
  // "＋ Back up" on the Docker page opens the form with that container picked.
  const [params] = useSearchParams()
  const [prefilled, setPrefilled] = useState(false)
  useEffect(() => {
    const c = params.get('container')
    if (isNew && meta && c && !prefilled) { setPrefilled(true); pickContainer(c) }
  }, [isNew, meta, params, prefilled]) // eslint-disable-line react-hooks/exhaustive-deps

  const info: EngineInfo | undefined = meta?.engines.find(e => e.engine === f.engine)
  const readOnly = existing?.source === 'DockerLabel'
  const set = <K extends keyof TargetInput>(k: K, v: TargetInput[K]) => setF(prev => ({ ...prev, [k]: v }))

  const pickEngine = (e: Engine) => {
    const ei = meta?.engines.find(x => x.engine === e)
    setF(prev => ({
      ...prev,
      engine: e,
      mode: ei && !ei.supportsNetwork ? 'DockerExec' : ei && !ei.supportsExec ? 'Network' : prev.mode,
    }))
  }

  /**
   * Picking a container fills what it can. In exec mode that is the engine; in network mode also the host and port
   * ACopy can reach it on, the user and the database — the password stays on the server, read from the container's
   * env at run time (the target remembers the container by name).
   */
  const pickContainer = async (name: string, mode: Mode = f.mode) => {
    set('containerName', name)
    setHint(''); setReach(null)
    if (!name) return
    try {
      const d = await api.detect(name)
      const n = d.network
      const network = mode === 'Network'
      setF(prev => ({
        ...prev,
        engine: d.engine ?? prev.engine,
        name: prev.name || name,
        ...(network && {
          host: n.host ?? prev.host,
          port: n.port != null && n.port !== meta?.engines.find(e => e.engine === (d.engine ?? prev.engine))?.defaultPort ? n.port : null,
          username: prev.username || d.user || '',
          databaseName: prev.databaseName || d.database || '',
        }),
      }))
      const bits = [
        d.engine && `engine ${d.engine}`,
        d.user && `user “${d.user}”`,
        d.hasPassword && 'a password',
        d.database && `database “${d.database}”`,
      ].filter(Boolean)
      if (bits.length) setHint(`Detected ${bits.join(', ')} from the container — ${network
        ? 'a blank password is read from its env at run time (never sent to the browser).'
        : 'blank user/password fields use these at run time.'}`)
      if (!d.running) setHint(h => (h ? h + ' ' : '') + 'The container is not running.')
      if (network) setReach(n)
    } catch { /* detection is a convenience */ }
  }

  const pickMode = (m: Mode) => {
    set('mode', m)
    setReach(null)
    // Coming from exec mode with a container picked: fill the network fields from it.
    if (m === 'Network' && f.containerName && !f.host) pickContainer(f.containerName, m)
  }

  const payload = (): TargetInput => ({
    ...f,
    port: f.port ? Number(f.port) : null,
    keep: Number(f.keep),
    maxAgeDays: f.naming === 'Timestamp' && f.maxAgeDays ? Number(f.maxAgeDays) : null,
    timeoutMinutes: f.timeoutMinutes ? Number(f.timeoutMinutes) : 120,
    password: f.password || null,
  })

  const save = async () => {
    setBusy(true); setErrors({}); setError('')
    try {
      const saved = isNew ? await api.createTarget(payload()) : await api.updateTarget(existing!.id, payload())
      await reload()
      nav(`/targets/${saved.id}`)
    } catch (e) {
      if (e instanceof ApiError) { setErrors(e.fields); setError(e.message) } else setError(apiError(e))
    } finally { setBusy(false) }
  }

  const doTest = async () => {
    setBusy(true); setTest(null)
    try { setTest(await api.testInput(payload(), existing?.id)) }
    catch (e) { setTest({ ok: false, message: apiError(e), details: null }) }
    finally { setBusy(false) }
  }

  const remove = async () => {
    if (!existing) return
    if (!confirm(`Delete "${existing.name}"${deleteFiles ? " AND its backup files" : ""}?`)) return
    try {
      await api.deleteTarget(existing.id, deleteFiles)
      await reload()
      nav('/targets')
    } catch (e) { setError(apiError(e)) }
  }

  const ext = useMemo(() => {
    const base = f.engine === 'Custom' && f.customExtension ? f.customExtension.replace(/^\./, '') : info?.extension ?? 'bin'
    const key = keys.find(k => k.id === f.encryptionKeyId)
    return base + (f.compression === 'Gzip' ? '.gz' : f.compression === 'Brotli' ? '.br' : '') + (key ? (key.kind === 'Age' ? '.age' : '.enc') : '')
  }, [f.engine, f.customExtension, f.compression, f.encryptionKeyId, keys, info])
  const base = slug(f.name || 'name')
  const folder = f.folder ? f.folder : slug(f.name || 'name')
  const examples = f.naming === 'Rolling'
    ? Array.from({ length: Math.min(Number(f.keep) || 1, 4) }, (_, i) => `${base}.${i}.${ext}`)
    : [`${base}_20261019-030000.${ext}`, `${base}_20261020-030000.${ext}`]

  const labels = useMemo(() => {
    const p = meta?.labelPrefix ?? 'acopy'
    const l: string[] = [`${p}.enable: "true"`]
    l.push(`${p}.engine: "${f.engine.toLowerCase()}"`)
    if (f.mode === 'Network') {
      l.push(`${p}.mode: "network"`)
      // Labels default the host to the container itself and the port to the engine's.
      if (f.host && f.host !== f.containerName) l.push(`${p}.host: "${f.host}"`)
      if (f.port && f.port !== info?.defaultPort) l.push(`${p}.port: "${f.port}"`)
    }
    if (f.name) l.push(`${p}.name: "${f.name}"`)
    l.push(`${p}.schedule: "${f.schedule}"`)
    l.push(`${p}.keep: "${f.keep}"`)
    if (f.naming === 'Rolling') l.push(`${p}.naming: "rolling"`)
    if (f.compression !== 'Gzip') l.push(`${p}.compression: "${f.compression.toLowerCase()}"`)
    if (f.databaseName) l.push(`${p}.database: "${f.databaseName}"`)
    if (f.username) l.push(`${p}.user: "${f.username}"`)
    if (f.passwordEnv) l.push(`${p}.password_env: "${f.passwordEnv}"`)
    if (f.extraArgs) l.push(`${p}.args: "${f.extraArgs.replace(/"/g, '\\"')}"`)
    if (f.maxAgeDays && f.naming === 'Timestamp') l.push(`${p}.max_age_days: "${f.maxAgeDays}"`)
    const key = keys.find(k => k.id === f.encryptionKeyId)
    if (key) l.push(`${p}.encryption: "${key.name}"`)
    const names = dests.filter(d => f.destinationIds?.includes(d.id)).map(d => d.name)
    if (names.length) l.push(`${p}.destinations: "${names.join(',')}"`)
    if (f.telegramChatIds?.trim()) l.push(`${p}.telegram_chats: "${f.telegramChatIds.split(',').map(s => s.trim()).filter(Boolean).join(',')}"`)
    if (f.engine === 'Custom' && f.restoreCommand) l.push(`${p}.restore_command: "${f.restoreCommand.replace(/"/g, '\\"')}"`)
    return l.map(x => `      ${x}`).join('\n')
  }, [f, meta, keys, dests])

  if (!meta || (!isNew && !targets)) return <p className="loading">Loading…</p>
  if (!isNew && !existing) return <p className="empty">This database no longer exists.</p>

  const err = (k: string) => errors[k]?.map(m => <span key={m} className="field-error">{m}</span>)
  const tgSelected = dests.filter(d => d.kind === 'Telegram' && f.destinationIds?.includes(d.id))
  /** Credentials can come from a container's env: always in exec mode, in network mode when a container is linked. */
  const fromEnv = f.mode === 'DockerExec' || !!f.containerName

  return (
    <div className="page page-narrow" style={{ maxWidth: 820 }}>
      <div className="page-header">
        <h2>{isNew ? 'Add database' : readOnly ? 'Configuration' : `Edit ${existing!.name}`}</h2>
      </div>

      {readOnly && (
        <div className="callout">
          This target is defined by Docker labels on <code>{existing!.containerName}</code> and re-synced every scan — change the labels to change it.
          Pausing/resuming from the rail is kept across scans.
        </div>
      )}

      <fieldset disabled={readOnly} style={{ border: 'none' }}>
        <div className="card">
          <h2>What to back up</h2>
          <label>
            Engine
            <div className="segmented">
              {meta.engines.map(e => (
                <button key={e.engine} type="button" className={f.engine === e.engine ? 'active' : ''} onClick={() => pickEngine(e.engine)}>
                  {ENGINE_ICON[e.engine]} {e.label}
                </button>
              ))}
            </div>
          </label>
          {info && <p className="callout-info" style={{ marginBottom: '0.9rem' }}>{info.notes}</p>}

          <label>
            Where the dump runs
            <div className="segmented">
              {(['DockerExec', 'Network'] as Mode[]).map(m => (
                <button key={m} type="button" className={f.mode === m ? 'active' : ''} onClick={() => pickMode(m)}
                        disabled={m === 'Network' ? !info?.supportsNetwork : !info?.supportsExec}>
                  {m === 'DockerExec' ? '🐳 Inside the container (docker exec)' : '🌐 From ACopy over the network'}
                </button>
              ))}
            </div>
            <span className="hint">
              {f.mode === 'DockerExec'
                ? 'Runs the database’s own dump tool inside its container via the Docker socket — no ports, matching tool versions, credentials read from the container’s env.'
                : f.engine === 'Sqlite' ? 'The file must be mounted into the ACopy container.' : 'Uses the client tools bundled in the ACopy image.'}
            </span>
          </label>

          <div className="form-grid">
            {(f.mode === 'DockerExec' || (f.engine !== 'Sqlite' && f.engine !== 'Custom' && (containers || f.containerName))) && (
              <label className="span-2">
                {f.mode === 'DockerExec' ? 'Container' : 'Fill from a container (optional)'}
                {containers ? (
                  <select value={f.containerName ?? ''} onChange={e => pickContainer(e.target.value)}>
                    <option value="">{f.mode === 'DockerExec' ? '— pick a container —' : '— none, enter the host below —'}</option>
                    {f.containerName && !containers.some(c => c.name === f.containerName) && <option value={f.containerName}>{f.containerName} (not found)</option>}
                    {containers.map(c => (
                      <option key={c.id} value={c.name}>
                        {c.name} — {c.image}{c.detectedEngine ? ` (${c.detectedEngine})` : ''}{c.state !== 'running' ? ` [${c.state}]` : ''}
                      </option>
                    ))}
                  </select>
                ) : (
                  <input value={f.containerName ?? ''} onChange={e => set('containerName', e.target.value)} placeholder="container name" />
                )}
                {dockerError && <span className="hint">Container list unavailable: {dockerError} — type the name.</span>}
                {f.mode === 'Network' && !hint && (
                  <span className="hint">Fills the host, port, user and database, and checks ACopy can reach it. A blank password is read from the container’s env at run time.</span>
                )}
                {hint && <span className="hint">{hint}</span>}
                {err('containerName')}
              </label>
            )}
            {f.mode === 'Network' && reach && <ReachCallout reach={reach} host={f.host ?? null}port={f.port ?? info?.defaultPort ?? null} />}
            {f.mode === 'Network' && f.engine !== 'Sqlite' && (
              <>
                <label>Host<input value={f.host ?? ''} onChange={e => set('host', e.target.value)} placeholder="db, 192.168.1.10, my-postgres" />{err('host')}</label>
                <label>Port<input type="number" value={f.port ?? ''} onChange={e => set('port', e.target.value ? Number(e.target.value) : null)} placeholder={String(info?.defaultPort ?? '')} />{err('port')}</label>
              </>
            )}

            <label className={f.engine === 'Sqlite' || f.engine === 'SqlServer' ? 'span-2' : ''}>
              {f.engine === 'Sqlite' ? 'SQLite file path' : 'Database'}
              <input value={f.databaseName ?? ''} onChange={e => set('databaseName', e.target.value)}
                     placeholder={f.engine === 'Sqlite' ? '/data/app.db' : info?.allDatabasesWhenEmpty ? 'all databases' : ''} />
              <span className="hint">{info?.databaseHint}</span>
              {err('databaseName')}
            </label>

            {f.engine !== 'Sqlite' && (
              <>
                <label>
                  User
                  <input value={f.username ?? ''} onChange={e => set('username', e.target.value)}
                         placeholder={fromEnv ? 'from container env' : info?.defaultUser ?? ''} autoComplete="off" />
                </label>
                <label>
                  Password
                  <input type="password" value={f.password ?? ''} onChange={e => set('password', e.target.value)} autoComplete="new-password"
                         placeholder={existing?.hasPassword && !f.clearPassword ? '•••••• (unchanged)' : fromEnv ? 'from container env' : ''} />
                  {existing?.hasPassword && !readOnly && (
                    <span className="hint">
                      <a style={{ cursor: 'pointer' }} onClick={() => set('clearPassword', !f.clearPassword)}>
                        {f.clearPassword ? 'Keep the stored password' : 'Clear the stored password'}
                      </a>
                    </span>
                  )}
                  <span className="hint">Encrypted at rest; never sent back to the browser.</span>
                </label>
                {fromEnv && (
                  <label className="span-2">
                    Password from container variable
                    <input value={f.passwordEnv ?? ''} onChange={e => set('passwordEnv', e.target.value)} placeholder="e.g. POSTGRES_PASSWORD — optional, overrides the field above" />
                  </label>
                )}
              </>
            )}

            <label className="span-2">
              {f.engine === 'Custom' ? 'Command (stdout is the backup)' : 'Extra arguments'}
              {f.engine === 'Custom'
                ? <textarea value={f.extraArgs ?? ''} onChange={e => set('extraArgs', e.target.value)} className="mono"
                            placeholder={'curl -fsS -u "$ACOPY_USER:$ACOPY_PASSWORD" http://couchdb:5984/mydb/_all_docs?include_docs=true'} />
                : <input value={f.extraArgs ?? ''} onChange={e => set('extraArgs', e.target.value)} className="mono"
                         placeholder={f.engine === 'PostgreSql' ? '--no-owner --exclude-table=audit_log' : ''} />}
              {err('extraArgs')}
            </label>
            {f.engine === 'Custom' && (
              <label>File extension<input value={f.customExtension ?? ''} onChange={e => set('customExtension', e.target.value)} placeholder="json" />{err('customExtension')}</label>
            )}
            {f.engine === 'Custom' && (
              <label className="span-2">
                Restore command (optional — receives the backup on stdin)
                <textarea className="mono" value={f.restoreCommand ?? ''} onChange={e => set('restoreCommand', e.target.value)}
                          placeholder={'curl -fsS -X POST -H "Content-Type: application/json" --data-binary @- http://couchdb:5984/mydb/_bulk_docs'} />
              </label>
            )}
          </div>
          {!readOnly && (
            <div className="form-actions">
              <button type="button" className="btn-ghost" onClick={doTest} disabled={busy}>🔌 Test connection</button>
              {test && <span className={test.ok ? 'ok-msg' : 'error-msg'}>{test.ok ? '✓' : '✗'} {test.message}</span>}
            </div>
          )}
        </div>

        <div className="card">
          <h2>When and how many</h2>
          <label>
            Name
            <input value={f.name} onChange={e => set('name', e.target.value)} placeholder="nextcloud-db" />
            {err('name')}
          </label>
          <label>
            Schedule (cron, in the server time zone)
            <input className="mono" value={f.schedule} onChange={e => set('schedule', e.target.value)} />
            <div className="segmented">
              {SCHEDULE_PRESETS.map(([label, cron]) => (
                <button key={cron} type="button" className={f.schedule === cron ? 'active' : ''} onClick={() => set('schedule', cron)}>{label}</button>
              ))}
            </div>
            <span className="hint">{describeCron(f.schedule)} · minute hour day month weekday (a 6th leading field adds seconds)</span>
            {err('schedule')}
          </label>

          <div className="form-grid">
            <label>
              File naming
              <div className="segmented">
                {(['Timestamp', 'Rolling'] as Naming[]).map(n => (
                  <button key={n} type="button" className={f.naming === n ? 'active' : ''} onClick={() => set('naming', n)}>
                    {n === 'Timestamp' ? '🕒 Timestamped' : '🔁 Rolling slots'}
                  </button>
                ))}
              </div>
            </label>
            <label>
              {f.naming === 'Rolling' ? 'Slots' : 'Copies to keep'}
              <input type="number" min={f.naming === 'Rolling' ? 1 : 0} max={1000} value={f.keep} onChange={e => set('keep', Number(e.target.value))} />
              {f.naming === 'Timestamp' && <span className="hint">0 = unlimited (needs a maximum age)</span>}
              {err('keep')}
            </label>
            {f.naming === 'Timestamp' && (
              <label>
                Maximum age (days)
                <input type="number" min={1} value={f.maxAgeDays ?? ''} placeholder="no limit"
                       onChange={e => set('maxAgeDays', e.target.value ? Number(e.target.value) : null)} />
                <span className="hint">The newest copy is always kept, however old.</span>
                {err('maxAgeDays')}
              </label>
            )}
          </div>
          <p className="callout-info" style={{ marginBottom: '0.9rem' }}>
            {f.naming === 'Rolling'
              ? <>Writes slot 0 → 1 → … → {Math.max(0, Number(f.keep) - 1)} → 0, overwriting in place — a fixed set of file names, ideal for Syncthing & co.</>
              : <>A new file per run; {[
                  Number(f.keep) > 0 && `the oldest are deleted once there are more than ${f.keep}`,
                  f.maxAgeDays && `copies older than ${f.maxAgeDays} days are deleted`,
                ].filter(Boolean).join(', and ') || 'nothing is deleted'}.</>}
            <br />
            <span className="mono">/backups/{folder}/{examples.join(', ')}{f.naming === 'Rolling' && Number(f.keep) > 4 ? ', …' : ''}</span>
          </p>

          <div className="form-grid">
            <label>
              Compression
              <div className="segmented">
                {(['Gzip', 'Brotli', 'None'] as Compression[]).map(c => (
                  <button key={c} type="button" className={f.compression === c ? 'active' : ''} onClick={() => set('compression', c)}>{c}</button>
                ))}
              </div>
            </label>
            <label>
              Folder under /backups
              <input value={f.folder ?? ''} onChange={e => set('folder', e.target.value)} placeholder={slug(f.name || 'name')} />
            </label>
            <label>
              Timeout (minutes)
              <input type="number" min={1} value={f.timeoutMinutes ?? 120} onChange={e => set('timeoutMinutes', Number(e.target.value))} />
              {err('timeoutMinutes')}
            </label>
          </div>
          <ToggleField checked={f.enabled} onChange={v => set('enabled', v)} label="Scheduled" hint="Off = only manual backups" />
        </div>

        <div className="card">
          <h2>Protection &amp; off-site copies</h2>
          <label>
            Encryption
            <select value={f.encryptionKeyId ?? ''} onChange={e => set('encryptionKeyId', e.target.value ? Number(e.target.value) : null)}>
              <option value="">None — store plain (compressed) files</option>
              {keys.map(k => <option key={k.id} value={k.id}>🔐 {k.name} ({k.kind === 'Age' ? 'age public key' : 'passphrase'})</option>)}
            </select>
            <span className="hint">
              {keys.length === 0 ? <>No keys yet — <a style={{ cursor: 'pointer' }} onClick={() => nav('/keys')}>create one</a>.</> : 'Applies to new backups; older files keep the key they were written with.'}
            </span>
            {err('encryptionKeyId')}
          </label>
          <label>
            Copy each backup to
            {dests.length === 0 ? (
              <span className="hint">No destinations yet — <a style={{ cursor: 'pointer' }} onClick={() => nav('/destinations')}>add S3, SFTP, Telegram, …</a></span>
            ) : (
              <div className="segmented">
                {dests.map(d => {
                  const on = f.destinationIds?.includes(d.id) ?? false
                  return (
                    <button key={d.id} type="button" className={on ? 'active' : ''}
                            onClick={() => set('destinationIds', on ? (f.destinationIds ?? []).filter(x => x !== d.id) : [...(f.destinationIds ?? []), d.id])}>
                      {d.kind === 'Telegram' ? '✈' : '☁'} {d.name}{!d.enabled ? ' (disabled)' : ''}
                    </button>
                  )
                })}
              </div>
            )}
            {err('destinationIds')}
          </label>
          {(tgSelected.length > 0 || !!f.telegramChatIds) && (
            <label className="span-2">
              Telegram chats for this database (optional)
              <input className="mono" value={f.telegramChatIds ?? ''} onChange={e => set('telegramChatIds', e.target.value)}
                     placeholder={tgSelected.map(d => d.params.chatId).filter(Boolean).join(', ') || '-1001234567890, 123456789'} />
              <span className="hint">
                Comma-separated chat ids — each one gets every file (uploaded once, forwarded by Telegram). Replaces the chat
                of {tgSelected.length ? tgSelected.map(d => d.name).join(', ') : 'the Telegram destinations'}; blank = the destination's chat.
                People must have started a chat with the bot (or be in the group) first.
              </span>
              {err('telegramChatIds')}
            </label>
          )}
        </div>
      </fieldset>

      {error && <p className="error-msg" style={{ marginTop: '1rem' }}>{error}</p>}
      {!readOnly && (
        <div className="form-actions" style={{ marginTop: '1rem' }}>
          <button className="btn-primary" onClick={save} disabled={busy}>{isNew ? 'Add database' : 'Save changes'}</button>
          <button className="btn-ghost" onClick={() => nav(isNew ? '/targets' : `/targets/${existing!.id}`)}>Cancel</button>
        </div>
      )}

      <div className="card" style={{ marginTop: '1.5rem' }}>
        <h2>As Docker labels</h2>
        <p className="hint" style={{ marginBottom: '0.6rem' }}>
          The same configuration on the database container in your <code>docker-compose.yml</code>. Credentials come from the container’s own env.
        </p>
        <div className="code-block">{`services:\n  your-db:\n    labels:\n${labels}`}</div>
      </div>

      {existing && (!readOnly || existing.orphaned) && (
        <div className="card" style={{ marginTop: '1rem', borderColor: 'rgb(239 68 68 / 0.35)' }}>
          <h2>Danger zone</h2>
          <ToggleField checked={deleteFiles} onChange={setDeleteFiles} label="Also delete its backup files" hint={`Files under /backups/${existing.folder}/ that this target wrote`} />
          <div className="form-actions" style={{ marginTop: '0.75rem' }}>
            <button className="btn-danger" onClick={remove} disabled={existing.active}>🗑 Delete this database</button>
            <span className="hint">Its run history is removed.</span>
          </div>
        </div>
      )}
    </div>
  )
}
