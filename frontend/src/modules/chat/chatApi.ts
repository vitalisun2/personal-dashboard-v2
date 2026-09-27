export type ChatModel = 'Gemma'
export type ChatScope =
  | { mode: 'general' }
  | { mode: 'entity'; entityType: string; entityId: string; entityVersion: number }

export interface ChatEntityEntry {
  entityType: string
  entityId: string
  entityVersion: number
  label: string
}

export interface ChatMessage {
  id: string
  role: 'User' | 'Assistant' | 'Tool'
  kind: 'Text' | 'ProposalPreview' | 'ProposalStatus' | 'SourceReference'
  content: string
  createdAt: string
  turnId?: string | null
  structuredContent?: unknown
  proposalId?: string | null
}

export interface ChatTurn {
  id: string
  userMessage: string
  assistantMessage: string
  scope: ChatScope
  requestedModel: ChatModel
  actualModel: ChatModel
  fallbackReason?: string | null
  modelRoute?: 'Default' | 'ManualSelection' | 'AutomaticFallback'
  createdAt: string
  sourceReferences: string[]
  sourceDetails?: ChatSource[]
  proposalId?: string | null
  proposalStatus?: 'Pending' | 'Applied' | 'Dismissed' | 'Stale' | 'Expired' | null
  changes?: ChatProposedChange[]
  localFailure?: string
}

export interface ChatSource {
  kind?: string
  title: string
  path?: string | null
  url: string | null
  snippet: string
  highlight?: { start: number; length: number } | null
  semanticSimilarity?: number | null
  matchKind?: 'lexical' | 'semantic' | null
  isShowResult?: boolean
}

export interface ChatProposedChange {
  id: string
  operation: string
  entityType: string
  entityId: string
  displayName: string
  preview: string
  before?: unknown
  after: Record<string, unknown>
}

export interface ChatConversation {
  id: string
  title: string
  createdAt: string
  updatedAt: string
  messages: ChatMessage[]
  turns: ChatTurn[]
  nextCursor?: string | null
}

export interface ChatHistoryItem {
  id: string
  title: string
  updatedAt: string
}

export interface ChatHistoryPage {
  conversations: ChatHistoryItem[]
  nextCursor: string | null
}

export interface ChatSendResult {
  turn: ChatTurn
}

export interface ChatProgressEnvelope {
  type: 'progress'
  turnId: string
  stage: string
  text: string
}

export interface ChatResultEnvelope {
  type: 'result'
  turn: ChatTurn
}

export interface ChatErrorEnvelope {
  type: 'error'
  message: string
}

type ChatStreamEnvelope = ChatProgressEnvelope | ChatResultEnvelope | ChatErrorEnvelope

export interface ProposalConflictAction {
  actionId: string
  entityType: string
  entityId: string
  label: string
  current: unknown
  proposed: unknown
}

export interface ProposalConflictPayload {
  stale: boolean
  expired: boolean
  error: string
  currentValues: ProposalConflictAction[]
}

export class ProposalConflictError extends Error {
  constructor(public readonly conflict: ProposalConflictPayload) {
    super(conflict.error || 'Предложение больше не соответствует текущим данным.')
    this.name = 'ProposalConflictError'
  }
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(path, {
    ...init,
    headers: { 'Content-Type': 'application/json', ...init?.headers },
  })
  if (!response.ok) throw await responseError(response)
  return response.status === 204 ? (undefined as T) : response.json() as Promise<T>
}

async function responseError(response: Response): Promise<Error> {
  const detail = await response.text()
  if (response.status === 409) {
    try {
      const conflict = JSON.parse(detail) as ProposalConflictPayload
      if (Array.isArray(conflict.currentValues)) return new ProposalConflictError(conflict)
    } catch { /* use the regular response message */ }
  }
  let message = response.status === 503 ? 'Gemma временно недоступна. Попробуйте позже.' : detail || `Chat request failed (${response.status})`
  try {
    const parsed = JSON.parse(detail) as { error?: string; message?: string; title?: string }
    message = parsed.error || parsed.message || parsed.title || message
  } catch { /* keep the server's plain-text detail */ }
  return new Error(message)
}

async function sendWithProgress(
  id: string,
  message: string,
  scope: ChatScope,
  onProgress: (text: string, turnId?: string) => void,
  signal: AbortSignal,
): Promise<ChatSendResult> {
  const response = await fetch(`/api/v2/agent/conversations/${encodeURIComponent(id)}/turns`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', Accept: 'application/x-ndjson' },
    body: JSON.stringify({ message, scope, requestedModel: 'Gemma' }),
    signal,
  })
  if (!response.ok) throw await responseError(response)
  const contentType = response.headers.get('content-type')?.toLowerCase() ?? ''
  if (!contentType.includes('application/x-ndjson')) return await response.json() as ChatSendResult
  if (!response.body) throw new Error('Поток ответа недоступен. Попробуйте отправить сообщение ещё раз.')

  const reader = response.body.getReader()
  const decoder = new TextDecoder()
  let remainder = ''
  let result: ChatSendResult | undefined
  const consume = (line: string) => {
    if (!line.trim()) return
    const event = JSON.parse(line) as ChatStreamEnvelope
    if (event.type === 'progress') onProgress(event.text, event.turnId)
    else if (event.type === 'error') throw new Error(event.message || 'Не удалось обработать сообщение.')
    else if (event.type === 'result') result = { turn: event.turn }
  }
  let streamEnded = false
  try {
    while (true) {
      const { value, done } = await reader.read()
      remainder += decoder.decode(value, { stream: !done })
      const lines = remainder.split('\n')
      remainder = lines.pop() ?? ''
      for (const line of lines) consume(line.replace(/\r$/, ''))
      if (done) { streamEnded = true; break }
    }
    if (remainder.trim()) consume(remainder.replace(/\r$/, ''))
  } catch (cause) {
    try { await reader.cancel(cause) } catch { /* stream already closed */ }
    throw cause
  } finally {
    if (!streamEnded) {
      try { await reader.cancel() } catch { /* stream already closed */ }
    }
    reader.releaseLock()
  }
  if (!result) throw new Error('Поток завершился без результата. Обновите чат перед повторной отправкой.')
  return result
}

export const chatApi = {
  list: (cursor?: string | null) => request<ChatHistoryPage>(`/api/v2/chat/conversations${cursor ? `?cursor=${encodeURIComponent(cursor)}` : ''}`),
  get: (id: string, turnId?: string) => request<ChatConversation>(`/api/v2/chat/conversations/${encodeURIComponent(id)}${turnId ? `?turnId=${encodeURIComponent(turnId)}` : ''}`),
  olderTurns: (id: string, cursor: string) => request<{ turns: ChatTurn[]; nextCursor: string | null }>(`/api/v2/chat/conversations/${encodeURIComponent(id)}/turns?cursor=${encodeURIComponent(cursor)}`),
  create: (signal?: AbortSignal) => request<ChatConversation>('/api/v2/chat/conversations', { method: 'POST', body: '{}', signal }),
  delete: (id: string) => request<void>(`/api/v2/chat/conversations/${encodeURIComponent(id)}`, { method: 'DELETE' }),
  send: (id: string, message: string, scope: ChatScope) =>
    request<ChatSendResult>(`/api/v2/agent/conversations/${encodeURIComponent(id)}/turns`, {
      method: 'POST', body: JSON.stringify({ message, scope, requestedModel: 'Gemma' }),
    }),
  sendWithProgress,
  confirm: (id: string, proposalId: string) =>
    request<void>(`/api/v2/agent/conversations/${encodeURIComponent(id)}/proposals/${encodeURIComponent(proposalId)}/confirm`, { method: 'POST', body: '{}' }),
  dismissProposal: (id: string, proposalId: string) =>
    request<void>(`/api/v2/chat/conversations/${encodeURIComponent(id)}/proposals/${encodeURIComponent(proposalId)}`, { method: 'DELETE' }),
}
