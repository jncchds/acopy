// A tiny stand-in for the Telegram Bot API: accepts getMe / sendMessage / sendDocument / deleteMessage / logOut
// and records every call (with its chat, and whether a document was uploaded, sent by local path — read from the
// shared upload folder like a --local server — or re-sent by file id).
// GET /log returns the record (used by e2e.mjs). Chat 403 behaves like a user who blocked the bot.
import fs from 'node:fs'
import http from 'node:http'
import path from 'node:path'

const calls = []
let nextId = 1000

// Field of a multipart body (.NET writes `name=chat_id`, browsers `name="chat_id"`); null when it is a file.
const field = (body, name) => {
  const m = body.match(new RegExp(`name="?${name}"?(;[^\\r\\n]*)?\\r\\n(?:[^\\r\\n]+\\r\\n)*\\r\\n([^\\r\\n]*)`))
  return m && !m[1]?.includes('filename') ? m[2] : null
}

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
    const form = (req.headers['content-type'] ?? '').includes('urlencoded') ? new URLSearchParams(head) : null
    const get = name => form ? form.get(name) : field(head, name)
    let fileName = head.match(/filename="?([^";\r\n]+)"?/)?.[1] ?? null
    const call = { method, token, size, fileName, chatId: get('chat_id'), at: new Date().toISOString() }
    if (method === 'sendDocument' && !fileName) {
      const doc = get('document') ?? ''
      if (doc.startsWith('file://')) {
        // --local mode: the server reads the file from its own disk (the shared upload folder)
        call.localPath = doc.slice('file://'.length)
        call.fileName = fileName = path.basename(call.localPath)
        call.fileSize = fs.existsSync(call.localPath) ? fs.statSync(call.localPath).size : null
      } else call.fileIdSent = doc
    }
    if (method === 'deleteMessage') call.messageId = Number(get('message_id'))
    calls.push(call)
    const ok = (result) => { res.writeHead(200, { 'Content-Type': 'application/json' }); res.end(JSON.stringify({ ok: true, result })) }
    const fail = (code, description) => { res.writeHead(code, { 'Content-Type': 'application/json' }); res.end(JSON.stringify({ ok: false, error_code: code, description })) }
    if (token !== '123:e2e') return fail(401, 'Unauthorized')
    if (call.chatId === '403') return fail(403, 'Forbidden: bot was blocked by the user')
    if (call.localPath && call.fileSize == null) return fail(400, 'Bad Request: file not found')
    switch (method) {
      case 'getMe': return ok({ id: 1, is_bot: true, username: 'acopy_e2e_bot' })
      case 'logOut': return ok(true)
      case 'sendMessage': return ok({ message_id: (call.messageId = nextId++) })
      case 'sendDocument': {
        call.messageId = nextId++
        return ok({ message_id: call.messageId, document: { file_id: call.fileIdSent ?? `file-${call.messageId}`, file_name: fileName } })
      }
      case 'deleteMessage': return ok(true)
      default: return fail(404, 'Not Found')
    }
  })
}).listen(8081, () => console.log('mock telegram on :8081'))
