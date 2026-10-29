// End-to-end check against the e2e compose stack (scripts/e2e/docker-compose.e2e.yml):
//  1. creates encryption keys (passphrase + age) and destinations (S3 via rclone, mock Telegram)
//  2. discovers the labelled containers (incl. jobs), adds a network-mode SQLite target
//  3. tests every connection and backs everything up — checks uploads
//  4. restores: Postgres (passphrase, local file), MariaDB (age, from the S3 copy), SQLite (network)
//  5. checks the mock Telegram: documents, notifications, per-job chats (file-id re-sends), retention in every
//     chat, a partly failed upload and its retry
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
// Same S3 server, dated names: the rolling job's slots arrive as name_<timestamp>… and are kept by count there
const s3Dated = { ...s3, name: 's3-dated', remotePath: 'acopy-e2e/dated', params: { ...s3.params, fileNames: 'timestamp' } }
// No apiUrl: the stack sets ACOPY_Telegram__ApiUrl to the mock, as it would for a self-hosted Bot API server
// (files then go through the shared upload folder). tg-http names the same mock differently → plain HTTP uploads.
const tg = {
  name: 'tg', kind: 'Telegram', enabled: true, params: { chatId: '42' }, secrets: { botToken: '123:e2e' },
}
const tgHttp = {
  name: 'tg-http', kind: 'Telegram', enabled: true, params: { chatId: '42', apiUrl: 'http://telegram-http:8081' }, secrets: { botToken: '123:e2e' },
}
for (const d of [s3, s3Dated, tg, tgHttp]) {
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
  telegramToken: '123:e2e', telegramChatId: '42', telegramApiUrl: null,
})
const meta = await call('GET', '/api/meta')
check(meta.telegramApiUrl === 'http://mock-telegram:8081', 'default Bot API URL from ACOPY_Telegram__ApiUrl', meta.telegramApiUrl)

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
check(byName['acopy-e2e-postgres-1-all']?.telegramChatIds === '100,200', 'job has its own Telegram chats', byName['acopy-e2e-postgres-1-all']?.telegramChatIds)

if (!byName['sqlite-file']) {
  await call('POST', '/api/targets', {
    name: 'sqlite-file', engine: 'Sqlite', mode: 'Network', databaseName: '/sqlite/app.db',
    schedule: '0 3 * * *', enabled: true, keep: 2, naming: 'Timestamp', compression: 'Brotli',
  })
  byName = await targetsByName()
}

// Network-mode prefill from a container: host/port/user from Docker, the password stays server-side
const pgDetect = await call('GET', '/api/docker/containers/acopy-e2e-postgres-1/detect')
const pn = pgDetect.network
check(pn.level === 'Good' && pn.host === 'acopy-e2e-postgres-1' && pn.port === 5432 && pn.acopyContainer === 'acopy-e2e-acopy-1',
  'network prefill: shared network → container name', `${pn.level} ${pn.host}:${pn.port} via ${pn.acopyContainer}`)
check(pgDetect.user === 'postgres' && pgDetect.hasPassword && pgDetect.password === undefined, 'network prefill: credentials detected, password not returned')
const island = (await call('GET', '/api/docker/containers/acopy-e2e-island-1/detect')).network
check(island.level === 'Unreachable' && island.fix?.includes('island'), 'network prefill: no shared network → unreachable + fix', `${island.level} ${island.message}`)
if (!byName['pg-net']) {
  // No password: it comes from the linked container's POSTGRES_PASSWORD at run time
  await call('POST', '/api/targets', {
    name: 'pg-net', engine: 'PostgreSql', mode: 'Network', host: pn.host, port: null, containerName: 'acopy-e2e-postgres-1',
    username: pgDetect.user, databaseName: 'appdb', schedule: '0 3 * * *', enabled: true, keep: 2, naming: 'Timestamp', compression: 'Gzip',
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
check(runs['acopy-e2e-mariadb-1']?.log?.includes('event scheduler is disabled'), 'mariadb: disabled event scheduler → dumped without --events')
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

const tgLog = async () => (await fetch('http://localhost:5081/log')).json()
let log = await tgLog()
const docs = log.filter(c => c.method === 'sendDocument')
const msgs = log.filter(c => c.method === 'sendMessage')
const appDoc = docs.find(d => d.fileName?.includes('postgres-1-app') && d.fileName.endsWith('.enc'))
check(appDoc, 'Telegram received the encrypted document', `${docs.length} documents`)
check(appDoc?.chatId === '42', '  … in the destination\'s chat', appDoc?.chatId)
const uploadsDir = '/var/lib/telegram-bot-api/acopy-uploads'
check(appDoc?.localPath === `${uploadsDir}/acopy-e2e-postgres-1-app/${appDoc?.fileName}` && appDoc.fileSize === runs['acopy-e2e-postgres-1-app'].sizeBytes,
  '  … sent by path from the shared upload folder', `${appDoc?.localPath} ${appDoc?.fileSize}`)
const httpDoc = docs.find(d => d.fileName?.includes('mariadb-1') && !d.localPath)
check(httpDoc && !docs.some(d => d.localPath?.includes('mariadb')), 'other Bot API URL: plain HTTP upload', httpDoc?.fileName ?? '-')
check(msgs.some(m => m.size > 0), 'Telegram notifications sent', `${msgs.length} messages`)

// Per-job chats: one upload to the first chat, the second gets the same file by its file id
const allJob = byName['acopy-e2e-postgres-1-all']
const uploaded = docs.find(d => d.chatId === '100' && d.fileName?.includes('postgres-1-all'))
const resent = docs.find(d => d.chatId === '200' && uploaded && d.fileIdSent === `file-${uploaded.messageId}`)
check(uploaded && resent, 'job chats: uploaded to 100, re-sent to 200 by file id', `${uploaded?.fileName ?? '-'} · ${resent?.fileIdSent ?? '-'}`)
check(/^acopy-e2e-postgres-1-all_\d{8}-\d{6}\.sql\.gz$/.test(uploaded?.fileName ?? ''), '  … rolling job sent with a dated name', uploaded?.fileName)
check(!docs.some(d => d.chatId === '42' && d.fileName?.includes('postgres-1-all')), '  … and not to the destination\'s chat')

// Retention never deletes Telegram messages; the upload folder follows the database's retention
for (let i = 0; i < 3; i++) await backup(allJob)
const ls = dir => sh('acopy', `ls -A /telegram-uploads/${dir} 2>/dev/null || true`).split('\n').filter(Boolean)
const allFiles = ls('acopy-e2e-postgres-1-all')
check(allFiles.length === 3 && allFiles.every(f => /_\d{8}-\d{6}\.sql\.gz$/.test(f)), 'upload folder: rolling job keeps 3 dated copies', allFiles.join(' '))
const dated = sh('s3', 'ls /data/acopy-e2e/dated/acopy-e2e-postgres-1-all/').split('\n').filter(Boolean)
check(dated.length === 2 && dated.every(f => /_\d{8}-\d{6}\.sql\.gz$/.test(f)), 'rclone "always timestamped": dated names, keep 2', dated.join(' '))

const lite = byName['sqlite-file']
const tgId = (await call('GET', '/api/destinations')).find(d => d.name === 'tg').id
const liteInput = {
  name: 'sqlite-file', engine: 'Sqlite', mode: 'Network', databaseName: '/sqlite/app.db',
  schedule: '0 3 * * *', enabled: true, keep: 2, naming: 'Timestamp', compression: 'Brotli', destinationIds: [tgId],
}
await call('PUT', `/api/targets/${lite.id}`, { ...liteInput, telegramChatIds: '300' })
for (let i = 0; i < 3; i++) { await sleep(1100); await backup(lite) }
const liteFiles = ls('sqlite-file')
check(liteFiles.length === 2, 'upload folder: retention keeps 2 timestamped copies', liteFiles.join(' '))
check(!(await tgLog()).some(c => c.method === 'deleteMessage'), 'retention deletes no Telegram messages')
check(ls('').concat(ls('sqlite-file')).every(f => !f.endsWith('.tmp')), 'upload folder: no temp files left')

// A chat that refuses the file: the upload fails, the other chat keeps its copy, and a retry only sends to the missing chat
await call('PUT', `/api/targets/${lite.id}`, {
  name: 'sqlite-file', engine: 'Sqlite', mode: 'Network', databaseName: '/sqlite/app.db',
  schedule: '0 3 * * *', enabled: true, keep: 2, naming: 'Timestamp', compression: 'Brotli',
  destinationIds: [tgId], telegramChatIds: '300, 403',
})
const partial = await backup(lite)
const pu = partial.uploads.find(u => u.destinationName === 'tg')
check(partial.status === 'Succeeded' && pu?.status === 'Failed' && pu.error?.includes('1 of 2') && pu.error.includes('403'),
  'partial Telegram upload is reported', `${pu?.status} ${pu?.error ?? ''}`)
const before = (await tgLog()).filter(c => c.method === 'sendDocument' && c.chatId === '300').length
const retried = await call('POST', `/api/uploads/${pu.id}/retry`)
const after = (await tgLog()).filter(c => c.method === 'sendDocument' && c.chatId === '300').length
check(retried.status === 'Failed' && after === before, 'retry skips the chat that already has the file', `${retried.status} · chat 300 documents ${before} → ${after}`)
const badChat = await call('PUT', `/api/targets/${lite.id}`, {
  name: 'sqlite-file', engine: 'Sqlite', mode: 'Network', databaseName: '/sqlite/app.db',
  schedule: '0 3 * * *', enabled: true, keep: 2, naming: 'Timestamp', compression: 'Brotli', telegramChatIds: 'alice',
}).catch(e => e)
check(badChat instanceof Error && badChat.message.includes('400'), 'invalid job chat id is rejected')

console.log(failures ? `\n${failures} check(s) failed` : '\nall checks passed')
process.exit(failures ? 1 : 0)
