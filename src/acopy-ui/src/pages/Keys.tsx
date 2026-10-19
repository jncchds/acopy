import { useCallback, useEffect, useState } from 'react'
import { api, ApiError, apiError, formatDate, type EncKey, type KeyInput, type KeyKind } from '../api'
import { useToast } from '../components/Bits'

type AgeMode = 'generate' | 'public' | 'identity'

export default function Keys() {
  const [keys, setKeys] = useState<EncKey[] | null>(null)
  const [adding, setAdding] = useState(false)
  const [toast, show] = useToast()

  const load = useCallback(async () => {
    try { setKeys(await api.keys()) } catch (e) { show(apiError(e), true) }
  }, [show])
  useEffect(() => { load() }, [load])

  const remove = async (k: EncKey) => {
    if (!confirm(`Delete key "${k.name}"? Files already encrypted with it can no longer be restored from the UI${k.kind === 'Age' ? ' (unless you kept the private key)' : ''}.`)) return
    try { await api.deleteKey(k.id); await load(); show('Key deleted') }
    catch (e) { show(apiError(e), true) }
  }

  const exportKey = async (k: EncKey) => {
    const password = prompt('Enter the ACopy admin password to export the private key:')
    if (password == null) return
    try {
      const text = await api.exportKey(k.id, password)
      const url = URL.createObjectURL(new Blob([text], { type: 'text/plain' }))
      const a = document.createElement('a')
      a.href = url
      a.download = `${k.name}.age-key.txt`
      a.click()
      URL.revokeObjectURL(url)
    } catch (e) { show(apiError(e), true) }
  }

  return (
    <div className="page">
      <div className="page-header">
        <h2>Encryption keys</h2>
        <span className="spacer" />
        {!adding && <button className="btn-primary" onClick={() => setAdding(true)}>＋ Add key</button>}
      </div>
      <p className="page-sub">
        Pick a key on a database (or <code>acopy.encryption=&lt;name&gt;</code>) and its backups are encrypted after compression.
        Both kinds can be decrypted without ACopy.
      </p>

      {adding && <KeyForm onDone={async created => { setAdding(false); if (created) { await load(); show('Key added') } }} />}

      {!keys ? <p className="loading">Loading…</p> : keys.length === 0 && !adding ? (
        <p className="empty">No keys yet — backups are stored unencrypted.</p>
      ) : (
        <div className="entity-list">
          {keys.map(k => (
            <div key={k.id} className="entity-card">
              <div className="entity-card-left">
                <h3 style={{ cursor: 'default' }}>🔐 {k.name}</h3>
                <div className="meta-row">
                  <span className={`chip ${k.kind === 'Age' ? 'chip-patina' : 'chip-accent'}`}>{k.kind === 'Age' ? 'age (public key)' : 'passphrase (OpenSSL)'}</span>
                  {k.kind === 'Age' && (k.hasSecret
                    ? <span className="chip">private key stored — one-click restore</span>
                    : <span className="chip chip-warn">public key only — paste the private key to restore</span>)}
                  <span className="chip">{k.usedBy} database{k.usedBy === 1 ? '' : 's'}</span>
                </div>
                {k.recipients.map(r => <div key={r} className="meta-line mono" style={{ wordBreak: 'break-all' }}>{r}</div>)}
                <div className="meta-line">Decrypt: <code>{k.decryptHint}</code></div>
              </div>
              <div className="entity-card-right">
                <span className="muted">added {formatDate(k.createdAt)}</span>
                <div className="entity-card-actions">
                  {k.kind === 'Age' && k.hasSecret && <button className="btn-ghost btn-sm" onClick={() => exportKey(k)}>⬇ Private key</button>}
                  <button className="btn-icon danger" onClick={() => remove(k)} title="Delete key" disabled={k.usedBy > 0}>🗑</button>
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

function KeyForm({ onDone }: { onDone: (created: boolean) => void }) {
  const [name, setName] = useState('')
  const [kind, setKind] = useState<KeyKind>('Age')
  const [ageMode, setAgeMode] = useState<AgeMode>('generate')
  const [passphrase, setPassphrase] = useState('')
  const [passphrase2, setPassphrase2] = useState('')
  const [recipients, setRecipients] = useState('')
  const [identity, setIdentity] = useState('')
  const [errors, setErrors] = useState<Record<string, string[]>>({})
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)

  const save = async () => {
    setErrors({}); setError('')
    if (kind === 'Passphrase' && passphrase !== passphrase2) { setErrors({ passphrase: ['Passphrases do not match'] }); return }
    const input: KeyInput = { name, kind }
    if (kind === 'Passphrase') input.passphrase = passphrase
    else if (ageMode === 'generate') input.generate = true
    else if (ageMode === 'identity') { input.identity = identity; input.recipients = recipients }
    else input.recipients = recipients
    setBusy(true)
    try { await api.createKey(input); onDone(true) }
    catch (e) { if (e instanceof ApiError) { setErrors(e.fields); setError(e.message) } else setError(apiError(e)) }
    finally { setBusy(false) }
  }

  const err = (k: string) => errors[k]?.map(m => <span key={m} className="field-error">{m}</span>)

  return (
    <div className="card" style={{ marginBottom: '1.25rem' }}>
      <h2>New key</h2>
      <label>Name<input value={name} onChange={e => setName(e.target.value)} placeholder="offsite" />{err('name')}</label>
      <label>
        Kind
        <div className="segmented">
          <button type="button" className={kind === 'Age' ? 'active' : ''} onClick={() => setKind('Age')}>🗝 age public key</button>
          <button type="button" className={kind === 'Passphrase' ? 'active' : ''} onClick={() => setKind('Passphrase')}>🔑 Passphrase</button>
        </div>
      </label>

      {kind === 'Passphrase' ? (
        <>
          <p className="callout-info" style={{ marginBottom: '0.9rem' }}>
            AES-256 in OpenSSL's format — anyone with the passphrase can decrypt with plain <code>openssl</code>. ACopy keeps the passphrase (encrypted) for one-click restores.
          </p>
          <div className="form-grid">
            <label>Passphrase<input type="password" value={passphrase} onChange={e => setPassphrase(e.target.value)} autoComplete="new-password" />{err('passphrase')}</label>
            <label>Repeat<input type="password" value={passphrase2} onChange={e => setPassphrase2(e.target.value)} autoComplete="new-password" /></label>
          </div>
        </>
      ) : (
        <>
          <p className="callout-info" style={{ marginBottom: '0.9rem' }}>
            <a href="https://age-encryption.org" target="_blank" rel="noopener noreferrer">age</a> encrypts to public keys: only the matching private key can decrypt.
            Keep the private key outside ACopy and a compromised server cannot read old backups.
          </p>
          <div className="segmented" style={{ marginBottom: '0.75rem' }}>
            <button type="button" className={ageMode === 'generate' ? 'active' : ''} onClick={() => setAgeMode('generate')}>Generate a key pair</button>
            <button type="button" className={ageMode === 'public' ? 'active' : ''} onClick={() => setAgeMode('public')}>I have a public key</button>
            <button type="button" className={ageMode === 'identity' ? 'active' : ''} onClick={() => setAgeMode('identity')}>Import a private key</button>
          </div>
          {ageMode === 'generate' && <p className="hint" style={{ marginBottom: '0.75rem' }}>ACopy creates the pair and stores the private key (encrypted) — export it afterwards and keep a copy somewhere safe.</p>}
          {ageMode !== 'generate' && (
            <label>
              Recipients (one per line)
              <textarea className="mono" value={recipients} onChange={e => setRecipients(e.target.value)} placeholder={'age1…\nssh-ed25519 AAAA…'} />
              <span className="hint">{ageMode === 'public' ? 'ACopy can encrypt but not decrypt; you paste the private key when restoring.' : 'Optional extra recipients — the private key’s own public key is added automatically.'}</span>
              {err('recipients')}
            </label>
          )}
          {ageMode === 'identity' && (
            <label>
              Private key
              <textarea className="mono" value={identity} onChange={e => setIdentity(e.target.value)} placeholder="AGE-SECRET-KEY-1…" />
              {err('identity')}
            </label>
          )}
        </>
      )}
      {error && <p className="error-msg" style={{ marginBottom: '0.5rem' }}>{error}</p>}
      <div className="form-actions">
        <button className="btn-primary" onClick={save} disabled={busy || !name}>Add key</button>
        <button className="btn-ghost" onClick={() => onDone(false)}>Cancel</button>
      </div>
    </div>
  )
}
