// End-to-end check against the e2e compose stack (scripts/e2e/docker-compose.e2e.yml):
//  1. creates encryption keys (passphrase + age) and destinations (S3 via rclone, mock Telegram)
//  2. discovers the labelled containers (incl. jobs), adds a network-mode SQLite target
//  3. tests every connection and backs everything up — checks uploads
//  4. restores: Postgres (passphrase, local file), MariaDB (age, from the S3 copy), SQLite (network)
//  5. checks the mock Telegram received documents and notifications
// Usage: node scripts/e2e/e2e.mjs [baseUrl]   — exit code 1 on any failure
import { execSync } from 'node:child_process'

const base = process.argv[2] ?? 'http://localhost:5006'
const project = 'acopy-e2e'
let cookie = ''
let failures = 0

async function call(method, path, body) {
  const res = await fetch(base + path, {
    method,
    headers: { ...(body ? { 'Content-Type': 'application/json' } : {}), cookie },
    body: body ? JSON.stringify(body) : undefined,
  })
  const set = res.headers.get('set-cookie')
  if (set) cookie = set.split(';')[0]
  if (!res.ok) throw new Error(`${method} ${path}: ${res.status} ${await res.text()}`)
  return res.status === 204 ? null : res.json()
}
const sleep = ms => new Promise(r => setTimeout(r, ms))
const check = (ok, label, detail = '') => {
  console.log(`${ok ? '✓' : '✗'} ${label}${detail ? ' — ' + detail : ''}`)
  if (!ok) failures++
}
const sh = (service, cmd) => execSync(`docker compose -p ${project} exec -T ${service} sh -c ${JSON.stringify(cmd)}`, { encoding: 'utf8' }).trim()
const targetsByName = async () => Object.fromEntries((await call('GET', '/api/targets')).map(t => [t.name, t]))
async function waitIdle() {
  for (let i = 0; i < 120; i++) {
    if (!(await call('GET', '/api/targets')).some(t => t.active)) return
    await sleep(1000)
  }
}
async function backup(t) {
  const { runId } = await call('POST', `/api/targets/${t.id}/run`)
  await waitIdle()
  return call('GET', `/api/runs/${runId}`)
}
async function restore(t, run, extra = {}) {
  const { runId } = await call('POST', `/api/runs/${run.id}/restore`, { safetyBackup: true, confirmName: t.name, ...extra })
  await waitIdle()
  return call('GET', `/api/runs/${runId}`)
}

await call('POST', '/api/auth/login', { password: 'e2e-password' })

// ── 1. keys, destinations, Telegram notifications ──
const keys = await call('GET', '/api/keys')
if (!keys.some(k => k.name === 'e2e-pass')) await call('POST', '/api/keys', { name: 'e2e-pass', kind: 'Passphrase', passphrase: 'e2e passphrase 123' })
if (!keys.some(k => k.name === 'e2e-age')) await call('POST', '/api/keys', { name: 'e2e-age', kind: 'Age', generate: true })

const dests = await call('GET', '/api/destinations')
const s3 = {
  name: 's3', kind: 'Rclone', enabled: true, rcloneType: 's3', remotePath: 'acopy-e2e/backups', keep: 2,
  params: { provider: 'Other', access_key_id: 'e2ekey', endpoint: 'http://s3:9000', force_path_style: 'true', region: 'us-east-1' },
  secrets: { secret_access_key: 'e2esecret' },
}
const tg = {
  name: 'tg', kind: 'Telegram', enabled: true, params: { chatId: '42', apiUrl: 'http://mock-telegram:8081' }, secrets: { botToken: '123:e2e' },
}
for (const d of [s3, tg]) {
  const existing = dests.find(x => x.name === d.name)
  if (existing) await call('PUT', `/api/destinations/${existing.id}`, d)
  else await call('POST', '/api/destinations', d)
}
for (const d of await call('GET', '/api/destinations')) {
  const r = await call('POST', `/api/destinations/${d.id}/test`)
  check(r.ok, `destination ${d.name} test`, r.message)
}
await call('PUT', '/api/settings', {
  timeZone: 'Europe/Berlin', webhookUrl: null, notifyOnSuccess: true,
  telegramToken: '123:e2e', telegramChatId: '42', telegramApiUrl: 'http://mock-telegram:8081',
})

// ── 2. discovery ──
const expected = ['acopy-e2e-postgres-1-app', 'acopy-e2e-postgres-1-all', 'acopy-e2e-mariadb-1', 'mysql-net', 'acopy-e2e-mongo-1', 'acopy-e2e-valkey-1']
let byName = {}
for (let i = 0; i < 30; i++) {
  await call('POST', '/api/docker/scan')
  byName = await targetsByName()
  if (expected.every(n => byName[n])) break
  await sleep(2000)
}
check(expected.every(n => byName[n]), 'label discovery incl. jobs', Object.keys(byName).join(', '))
check(byName['acopy-e2e-postgres-1-app']?.encryptionKeyName === 'e2e-pass', 'job inherits encryption label')
check(byName['acopy-e2e-postgres-1-app']?.destinationIds.length === 2, 'job has two destinations')

if (!byName['sqlite-file']) {
  await call('POST', '/api/targets', {
    name: 'sqlite-file', engine: 'Sqlite', mode: 'Network', databaseName: '/sqlite/app.db',
    schedule: '0 3 * * *', enabled: true, keep: 2, naming: 'Timestamp', compression: 'Brotli',
  })
  byName = await targetsByName()
}

// ── 3. connection tests (also readiness probes) and backups ──
for (const t of Object.values(byName)) {
  for (let i = 0; i < 25; i++) {
    const r = await call('POST', `/api/targets/${t.id}/test`)
    if (r.ok || i === 24) { check(r.ok, `test ${t.name}`, r.message); break }
    await sleep(3000)
  }
}

// Seed data the restores will bring back
sh('postgres', `psql -U postgres -d appdb -c "drop table if exists t; create table t(x int); insert into t values (1),(2),(3);"`)
sh('mariadb', `mariadb -uroot -pmaria-secret shop -e "drop table if exists t; create table t(x int); insert into t values (1),(2),(3),(4);"`)

const runs = {}
for (const t of Object.values(byName)) {
  const r = await backup(t)
  runs[t.name] = r
  const uploads = r.uploads.map(u => `${u.destinationName}:${u.status}`).join(' ')
  check(r.status === 'Succeeded', `backup ${t.name}`, `${r.filePath ?? r.error} ${r.sizeBytes ?? ''} ${uploads}`)
  if (r.status !== 'Succeeded') console.log(r.log)
  for (const u of r.uploads) check(u.status === 'Succeeded', `  upload ${t.name} → ${u.destinationName}`, u.error ?? u.remoteRef ?? '')
}
check(runs['acopy-e2e-postgres-1-app']?.filePath?.endsWith('.sql.gz.enc'), 'passphrase-encrypted file name')
check(runs['acopy-e2e-mariadb-1']?.filePath?.endsWith('.sql.gz.age'), 'age-encrypted file name')

// ── 4. restores ──
sh('postgres', `psql -U postgres -d appdb -c "delete from t;"`)
let r = await restore(byName['acopy-e2e-postgres-1-app'], runs['acopy-e2e-postgres-1-app'])
const pgCount = sh('postgres', `psql -U postgres -d appdb -At -c "select count(*) from t"`)
check(r.status === 'Succeeded' && pgCount === '3', 'restore postgres (passphrase, local, exec)', `${r.status} ${r.error ?? ''} rows=${pgCount}`)
if (r.status !== 'Succeeded') console.log(r.log)

sh('mariadb', `mariadb -uroot -pmaria-secret shop -e "drop table t;"`)
const mariaRun = runs['acopy-e2e-mariadb-1']
const s3Upload = mariaRun.uploads.find(u => u.destinationName === 's3')
r = await restore(byName['acopy-e2e-mariadb-1'], mariaRun, { fromUploadId: s3Upload?.id })
const myCount = sh('mariadb', `mariadb -uroot -pmaria-secret shop -N -e "select count(*) from t"`)
check(r.status === 'Succeeded' && myCount === '4', 'restore mariadb (age, from the S3 copy)', `${r.status} ${r.error ?? ''} rows=${myCount}`)
if (r.status !== 'Succeeded') console.log(r.log)

sh('sqlite-seed', `sqlite3 /sqlite/app.db "delete from t;"`)
r = await restore(byName['sqlite-file'], runs['sqlite-file'], { safetyBackup: false })
const liteCount = sh('sqlite-seed', `sqlite3 /sqlite/app.db "select count(*) from t"`)
check(r.status === 'Succeeded' && liteCount === '3', 'restore sqlite (network)', `${r.status} ${r.error ?? ''} rows=${liteCount}`)

r = await call('POST', `/api/runs/${runs['acopy-e2e-valkey-1'].id}/restore`, { safetyBackup: false, confirmName: 'acopy-e2e-valkey-1' }).catch(e => e)
check(r instanceof Error && r.message.includes('400'), 'redis restore is refused with instructions')

// ── 5. remote retention + Telegram ──
for (let i = 0; i < 2; i++) await backup(byName['acopy-e2e-mariadb-1'])
const remoteFiles = sh('s3', 'ls /data/acopy-e2e/backups/acopy-e2e-mariadb-1/').split('\n').filter(Boolean)
check(remoteFiles.length === 2, 'S3 retention keeps 2', `${remoteFiles.length} files`)

const log = await (await fetch('http://localhost:5081/log')).json()
const docs = log.filter(c => c.method === 'sendDocument')
const msgs = log.filter(c => c.method === 'sendMessage')
check(docs.some(d => d.fileName?.includes('postgres-1-app') && d.fileName.endsWith('.enc')), 'Telegram received the encrypted document', `${docs.length} documents`)
check(msgs.some(m => m.size > 0), 'Telegram notifications sent', `${msgs.length} messages`)

console.log(failures ? `\n${failures} check(s) failed` : '\nall checks passed')
process.exit(failures ? 1 : 0)
