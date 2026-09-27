import assert from 'node:assert/strict'
import { readFile } from 'node:fs/promises'
import test from 'node:test'
import ts from 'typescript'

const source = await readFile(new URL('./chatApi.ts', import.meta.url), 'utf8')
const compiled = ts.transpile(source, { module: ts.ModuleKind.ESNext, target: ts.ScriptTarget.ES2022 })
const { chatApi } = await import(`data:text/javascript;base64,${Buffer.from(compiled).toString('base64')}`)

function responseFor(text, splitEveryByte = false, onCancel = () => {}, close = true) {
  const bytes = new TextEncoder().encode(text)
  const chunks = splitEveryByte ? [...bytes].map(byte => Uint8Array.of(byte)) : [bytes]
  return new Response(new ReadableStream({
    start(controller) { for (const chunk of chunks) controller.enqueue(chunk); if (close) controller.close() },
    cancel: onCancel,
  }), { headers: { 'Content-Type': 'application/x-ndjson' } })
}

test('reads split UTF-8 NDJSON progress lines and final turn', async () => {
  const turn = { id: 'turn-1', userMessage: 'Привет', assistantMessage: 'Готово' }
  globalThis.fetch = async () => responseFor([
    JSON.stringify({ type: 'progress', turnId: turn.id, stage: 'processing', text: 'Обрабатываю сообщение…' }),
    JSON.stringify({ type: 'progress', turnId: turn.id, stage: 'search', text: 'Ищу в данных…' }),
    JSON.stringify({ type: 'result', turn }),
  ].join('\n'), true)
  const progress = []
  const result = await chatApi.sendWithProgress('chat-1', 'Привет', { mode: 'general' }, (text, turnId) => progress.push([text, turnId]), new AbortController().signal)
  assert.deepEqual(progress, [['Обрабатываю сообщение…', turn.id], ['Ищу в данных…', turn.id]])
  assert.deepEqual(result, { turn })
})

test('surfaces streamed errors and cancels the reader', async () => {
  let canceled = false
  globalThis.fetch = async () => responseFor(
    JSON.stringify({ type: 'error', message: 'Ошибка обработки' }) + '\n', false, () => { canceled = true }, false,
  )
  await assert.rejects(
    chatApi.sendWithProgress('chat-1', 'Привет', { mode: 'general' }, () => {}, new AbortController().signal),
    /Ошибка обработки/,
  )
  assert.equal(canceled, true)
})

test('rejects a stream without a final result', async () => {
  globalThis.fetch = async () => responseFor(JSON.stringify({ type: 'progress', turnId: 'turn-1', stage: 'search', text: 'Ищу…' }))
  await assert.rejects(
    chatApi.sendWithProgress('chat-1', 'Привет', { mode: 'general' }, () => {}, new AbortController().signal),
    /без результата/,
  )
})
