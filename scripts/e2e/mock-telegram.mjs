// A tiny stand-in for the Telegram Bot API: accepts getMe / sendMessage / sendDocument / deleteMessage
// and records every call. GET /log returns the record (used by e2e.mjs).
import http from 'node:http'

const calls = []
let nextId = 1000

http.createServer((req, res) => {
  if (req.method === 'GET' && req.url === '/log') {
    res.writeHead(200, { 'Content-Type': 'application/json' })
    return res.end(JSON.stringify(calls))
  }
  const m = req.url.match(/^\/bot([^/]+)\/(\w+)/)
  if (!m) { res.writeHead(404); return res.end() }
  const [, token, method] = m
  let size = 0
  const chunks = []
  req.on('data', c => { size += c.length; if (size < 64_000) chunks.push(c) })
  req.on('end', () => {
    const head = Buffer.concat(chunks).toString('latin1')
    const fileName = head.match(/filename="?([^";\r\n]+)"?/)?.[1] ?? null
    const call = { method, token, size, fileName, at: new Date().toISOString() }
    calls.push(call)
    const ok = (result) => { res.writeHead(200, { 'Content-Type': 'application/json' }); res.end(JSON.stringify({ ok: true, result })) }
    if (token !== '123:e2e') { res.writeHead(401, { 'Content-Type': 'application/json' }); return res.end(JSON.stringify({ ok: false, description: 'Unauthorized' })) }
    switch (method) {
      case 'getMe': return ok({ id: 1, is_bot: true, username: 'acopy_e2e_bot' })
      case 'sendMessage':
      case 'sendDocument': return ok({ message_id: (call.messageId = nextId++) })
      case 'deleteMessage': return ok(true)
      default: res.writeHead(404, { 'Content-Type': 'application/json' }); res.end(JSON.stringify({ ok: false, description: 'Not Found' }))
    }
  })
}).listen(8081, () => console.log('mock telegram on :8081'))
