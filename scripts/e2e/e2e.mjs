// End-to-end check against the e2e compose stack: every labelled engine + a network SQLite target
// gets backed up; reports status, size and errors. Usage: node scripts/e2e/e2e.mjs [baseUrl]
const base = process.argv[2] ?? 'http://localhost:5006'
let cookie = ''

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

await call('POST', '/api/auth/login', { password: 'e2e-password' })

// Wait for label discovery (5 labelled containers)
let targets = []
for (let i = 0; i < 30; i++) {
  await call('POST', '/api/docker/scan')
  targets = await call('GET', '/api/targets')
  if (targets.filter(t => t.source === 'DockerLabel').length >= 5) break
  await sleep(2000)
}
console.log('discovered:', targets.map(t => `${t.name}(${t.engine}/${t.mode})`).join(', '))

if (!targets.some(t => t.name === 'sqlite-file')) {
  await call('POST', '/api/targets', {
    name: 'sqlite-file', engine: 'Sqlite', mode: 'Network', databaseName: '/sqlite/app.db',
    schedule: '0 3 * * *', enabled: true, keep: 2, naming: 'Timestamp', compression: 'Brotli',
  })
  targets = await call('GET', '/api/targets')
}

// Databases need a moment to initialise on first start; tests double as readiness probes
for (const t of targets) {
  for (let i = 0; i < 20; i++) {
    const r = await call('POST', `/api/targets/${t.id}/test`)
    if (r.ok || i === 19) { console.log(`test ${t.name}: ${r.ok ? 'ok' : 'FAIL'} — ${r.message}`); break }
    await sleep(3000)
  }
}

for (const t of targets) await call('POST', `/api/targets/${t.id}/run`).catch(e => console.log(e.message))
for (let i = 0; i < 60; i++) {
  targets = await call('GET', '/api/targets')
  if (!targets.some(t => t.active)) break
  await sleep(2000)
}

let failed = 0
for (const t of targets) {
  const r = t.lastRun
  const ok = r?.status === 'Succeeded'
  if (!ok) failed++
  console.log(`${ok ? '✓' : '✗'} ${t.name.padEnd(14)} ${r?.status ?? 'none'} ${r?.filePath ?? ''} ${r?.sizeBytes ?? ''} ${r?.error ?? ''}`)
  if (!ok && r) console.log((await call('GET', `/api/runs/${r.id}`)).log)
}
process.exit(failed ? 1 : 0)
