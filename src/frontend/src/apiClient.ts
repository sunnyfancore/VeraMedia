import { useCallback, useEffect, useRef, useState } from 'react'

export const API_BASE = import.meta.env.VITE_API_BASE ?? ''

export type User = { id: number; email: string; displayName: string; isAdmin: boolean }
export type AuthResponse = { accessToken: string; user: User; expiresAt: string; refreshableUntil: string }
export type AppConfig = { auth: { allowRegistration: boolean; requireEmailCode: boolean } }
export type Message = { id: number; role: 'user' | 'assistant'; content: string; thinking?: string[]; createdAt: string; messageType?: string | null }
export type Conversation = { id: number; title: string; updatedAt: string }
export type GenerationJob = {
  id: number
  conversationId: number
  userMessageId: number
  assistantMessageId: number
  status: 'pending' | 'running' | 'completed' | 'failed' | 'canceled'
  content: string
  thinking: string[]
  messageType?: string | null
  error?: string | null
  version: number
  updatedAt: string
}
export type GenerationJobSummary = {
  id: number
  conversationId: number
  conversationTitle: string
  userMessageId: number
  assistantMessageId: number
  status: 'pending' | 'running' | 'completed' | 'failed' | 'canceled'
  jobType: string
  requestPreview: string
  contentPreview: string
  messageType?: string | null
  error?: string | null
  createdAt: string
  updatedAt: string
  startedAt?: string | null
  completedAt?: string | null
}
export type Attachment = { fileName: string; contentType: string; size: number; url: string }
export type ProviderResponse = {
  id: number
  name: string
  baseUrl: string
  chatModelName: string
  imageModelName: string
  enabled: boolean
  hasApiKey: boolean
  apiKeyPreview: string
}
export type ModelListResponse = { models: string[] }
export type ProviderTestResponse = { ok: boolean; message: string; detail?: string | null }
export type AdminUser = { id: number; email: string; displayName: string; isAdmin: boolean; isEnabled: boolean; createdAt: string }
export type AdminRuntimeConfig = {
  allowRegistration: boolean
  requireEmailCode: boolean
  emailCodeEnabled: boolean
  smtpHost: string
  smtpPort: number
  enableSsl: boolean
  userName: string
  password?: string
  fromEmail: string
  fromName: string
  codeMinutes: number
}
export type AdminDashboardStats = {
  totalUsers: number
  enabledUsers: number
  totalConversations: number
  totalJobs: number
  pendingJobs: number
  runningJobs: number
  completedJobs: number
  failedJobs: number
  canceledJobs: number
  staleJobs: number
  articleAssets: number
  imageAssets: number
  recentFailures: {
    id: number
    conversationId: number
    conversationTitle: string
    jobType: string
    messageType?: string | null
    error: string
    updatedAt: string
  }[]
  recentIntents: {
    jobId: number
    conversationId: number
    conversationTitle: string
    requestPreview: string
    messageType?: string | null
    status: string
    updatedAt: string
  }[]
  recentAudits: {
    id: number
    actorName: string
    operation: string
    entityType: string
    entityId: string
    detail: string
    createdAt: string
  }[]
}
export type AdminPromptConfig = {
  global: string
  chat: string
  article: string
  document: string
  image: string
  rewrite: string
  layout: string
}
export type ArticleAsset = {
  id: number
  projectId: number
  title: string
  excerpt: string
  platform: string
  version: number
  status: string
  conversationId?: number | null
  messageId?: number | null
  createdAt: string
  updatedAt: string
}
export type ArticleVersion = {
  id: number
  projectId: number
  title: string
  excerpt: string
  body: string
  platform: string
  version: number
  createdAt: string
}
export type ImageAsset = {
  id: number
  projectId: number
  projectTitle: string
  prompt: string
  imageUrl?: string | null
  status: string
  createdAt: string
}

const publicAuthPaths = new Set([
  '/api/auth/login',
  '/api/auth/register',
  '/api/auth/email-code',
  '/api/auth/reset-password',
  '/api/auth/refresh',
])

async function readErrorMessage(response: Response, fallback: string) {
  const body = await response.json().catch(() => ({}))
  return typeof body.message === 'string' && body.message.trim() ? body.message : fallback
}

export function useAuthClient() {
  const [token, setToken] = useState(() => localStorage.getItem('vera_token') ?? '')
  const [tokenExpiresAt, setTokenExpiresAt] = useState(() => localStorage.getItem('vera_token_expires_at') ?? '')
  const [tokenRefreshableUntil, setTokenRefreshableUntil] = useState(() => localStorage.getItem('vera_token_refreshable_until') ?? '')
  const [user, setUser] = useState<User | null>(() => {
    const raw = localStorage.getItem('vera_user')
    return raw ? JSON.parse(raw) : null
  })

  const tokenRef = useRef(token)
  const tokenExpiresAtRef = useRef(tokenExpiresAt)
  const tokenRefreshableUntilRef = useRef(tokenRefreshableUntil)
  const refreshingRef = useRef<Promise<string> | null>(null)

  const setAuthSession = useCallback((next: AuthResponse) => {
    localStorage.setItem('vera_token', next.accessToken)
    localStorage.setItem('vera_user', JSON.stringify(next.user))
    localStorage.setItem('vera_token_expires_at', next.expiresAt)
    localStorage.setItem('vera_token_refreshable_until', next.refreshableUntil)
    tokenRef.current = next.accessToken
    tokenExpiresAtRef.current = next.expiresAt
    tokenRefreshableUntilRef.current = next.refreshableUntil
    setToken(next.accessToken)
    setTokenExpiresAt(next.expiresAt)
    setTokenRefreshableUntil(next.refreshableUntil)
    setUser(next.user)
  }, [])

  const doLogout = useCallback(() => {
    localStorage.removeItem('vera_token')
    localStorage.removeItem('vera_user')
    localStorage.removeItem('vera_token_expires_at')
    localStorage.removeItem('vera_token_refreshable_until')
    tokenRef.current = ''
    tokenExpiresAtRef.current = ''
    tokenRefreshableUntilRef.current = ''
    setToken('')
    setTokenExpiresAt('')
    setTokenRefreshableUntil('')
    setUser(null)
  }, [])

  const getToken = useCallback(() => tokenRef.current || localStorage.getItem('vera_token') || '', [])

  const shouldRefreshToken = useCallback(() => {
    const currentToken = getToken()
    if (!currentToken) return false
    const refreshableUntil = tokenRefreshableUntilRef.current || localStorage.getItem('vera_token_refreshable_until') || ''
    if (refreshableUntil && new Date(refreshableUntil).getTime() <= Date.now()) {
      doLogout()
      return false
    }

    const expiresAt = tokenExpiresAtRef.current || localStorage.getItem('vera_token_expires_at') || ''
    const expiresTime = expiresAt ? new Date(expiresAt).getTime() : 0
    return !expiresTime || expiresTime - Date.now() <= 10 * 60_000
  }, [doLogout, getToken])

  const tryRefreshToken = useCallback(async (): Promise<string> => {
    if (!refreshingRef.current) {
      const currentToken = getToken()
      if (!currentToken) throw new Error('missing token')
      refreshingRef.current = (async () => {
        const res = await fetch(`${API_BASE}/api/auth/refresh`, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${currentToken}` },
        })
        if (!res.ok) throw new Error('refresh failed')
        const result: AuthResponse = await res.json()
        setAuthSession(result)
        return result.accessToken
      })().finally(() => { refreshingRef.current = null })
    }
    return refreshingRef.current
  }, [getToken, setAuthSession])

  const ensureFreshToken = useCallback(async () => {
    if (shouldRefreshToken()) {
      return await tryRefreshToken()
    }

    return getToken()
  }, [getToken, shouldRefreshToken, tryRefreshToken])

  const request = useCallback(async <T,>(path: string, init: RequestInit = {}): Promise<T> => {
    const currentToken = publicAuthPaths.has(path) ? getToken() : await ensureFreshToken()
    const headers: Record<string, string> = {
      'Content-Type': 'application/json',
      'X-Client-User-Agent': navigator.userAgent,
      ...(currentToken ? { Authorization: `Bearer ${currentToken}` } : {}),
      ...init.headers as Record<string, string>,
    }
    const response = await fetch(`${API_BASE}${path}`, { ...init, headers })
    if (response.status === 401 && path !== '/api/auth/refresh') {
      try {
        const newToken = await tryRefreshToken()
        const retryHeaders = { ...headers, Authorization: `Bearer ${newToken}` }
        const retry = await fetch(`${API_BASE}${path}`, { ...init, headers: retryHeaders })
        if (!retry.ok) throw new Error(await readErrorMessage(retry, '请求失败'))
        if (retry.status === 204) return undefined as T
        return retry.json()
      } catch {
        doLogout()
        throw new Error('登录已过期，请重新登录。')
      }
    }
    if (!response.ok) throw new Error(await readErrorMessage(response, '请求失败'))
    if (response.status === 204) return undefined as T
    return response.json()
  }, [doLogout, ensureFreshToken, getToken, tryRefreshToken])

  const fetchWithAuth = useCallback(async (url: string, init: RequestInit = {}): Promise<Response> => {
    const currentToken = await ensureFreshToken()
    const headers: Record<string, string> = {
      'X-Client-User-Agent': navigator.userAgent,
      ...(currentToken ? { Authorization: `Bearer ${currentToken}` } : {}),
      ...init.headers as Record<string, string>,
    }
    const response = await fetch(url, { ...init, headers })
    if (response.status === 401) {
      try {
        const newToken = await tryRefreshToken()
        return await fetch(url, { ...init, headers: { ...headers, Authorization: `Bearer ${newToken}` } })
      } catch {
        doLogout()
        throw new Error('登录已过期，请重新登录。')
      }
    }
    return response
  }, [doLogout, ensureFreshToken, tryRefreshToken])

  const uploadAttachments = useCallback(async (files: FileList | File[]) => {
    const form = new FormData()
    Array.from(files).forEach((file) => form.append('files', file))
    const response = await fetchWithAuth(`${API_BASE}/api/uploads`, { method: 'POST', body: form })
    if (!response.ok) throw new Error(await readErrorMessage(response, '上传失败'))

    const uploaded = (await response.json()) as Attachment[]
    return uploaded.map((file) => ({
      ...file,
      url: file.url.startsWith('http') ? file.url : `${API_BASE}${file.url}`,
    }))
  }, [fetchWithAuth])

  useEffect(() => {
    const validateSession = async () => {
      if (!getToken()) return
      try {
        await tryRefreshToken()
      } catch {
        doLogout()
      }
    }

    validateSession()
  }, [doLogout, getToken, tryRefreshToken])

  return {
    token,
    tokenExpiresAt,
    tokenRefreshableUntil,
    user,
    setAuthSession,
    doLogout,
    tryRefreshToken,
    ensureFreshToken,
    request,
    fetchWithAuth,
    uploadAttachments,
  }
}
