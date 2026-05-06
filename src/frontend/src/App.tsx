import { useEffect, useRef, useState, useCallback } from 'react'
import type { ClipboardEvent, DragEvent, FormEvent, KeyboardEvent, ReactNode } from 'react'
import { useLayoutEffect } from 'react'
import ReactMarkdown from 'react-markdown'
import remarkGfm from 'remark-gfm'
import RichEditor from './RichEditor'
import type { RichEditorHandle } from './RichEditor'
import {
  assistantBlockLabels,
  assistantBlockLoadingText,
  cleanArticleContent,
  composeDocumentDraft,
  extractReferenceSection,
  formatMessageTime,
  getContentTextLength,
  getDocumentExcerpt,
  getDocumentTitle,
  getGeneratingImageTitle,
  isArticleMessageMode,
  isArticleLike,
  readServerSentEvents,
  splitDocumentDraft,
  stripImagePrompts,
  stripReferenceSections,
} from './conversationUi'
import type { AssistantBlock, AssistantBlockType, IntentMode } from './conversationUi'
import {
  API_BASE,
  useAuthClient,
} from './apiClient'
import type {
  AdminRuntimeConfig,
  AdminDashboardStats,
  AdminPromptConfig,
  AdminUser,
  AppConfig,
  ArticleAsset,
  ArticleVersion,
  Attachment,
  AuthResponse,
  Conversation,
  ImageAsset,
  Message,
  ModelListResponse,
  ProviderTestResponse,
  ProviderResponse,
  GenerationJob,
  GenerationJobSummary,
} from './apiClient'
import './RichEditor.css'
import {
  AlertTriangle,
  ArrowLeft,
  Bold,
  Bot,
  Bookmark,
  Braces,
  Check,
  Copy,
  CornerUpRight,
  Download,
  Eraser,
  ExternalLink,
  Flag,
  Heading2,
  Eye,
  FileText,
  Globe2,
  ImagePlus,
  Italic,
  KeyRound,
  Link as LinkIcon,
  List,
  ListRestart,
  LogOut,
  Mail,
  Menu,
  MessageSquarePlus,
  Mic,
  MoreHorizontal,
  Music,
  PanelRightOpen,
  Plus,
  Podcast,
  Presentation,
  Quote,
  Redo2,
  RotateCcw,
  Save,
  Send,
  Settings,
  Sparkles,
  Table as TableIcon,
  ThumbsDown,
  ThumbsUp,
  Trash2,
  Undo2,
  UserPlus,
  Users,
  Video,
  X,
  Zap,
  Languages,
  CircleHelp,
  ChartColumn,
  Volume2,
} from 'lucide-react'
import './App.css'

const stylePresets = [
  { value: 'balanced', label: '均衡', temperature: 0.72 },
  { value: 'story', label: '故事', temperature: 0.82 },
  { value: 'practical', label: '干货', temperature: 0.58 },
  { value: 'sharp', label: '犀利', temperature: 0.78 },
  { value: 'warm', label: '温暖', temperature: 0.68 },
  { value: 'conversion', label: '转化', temperature: 0.74 },
] as const

type StylePreset = typeof stylePresets[number]['value']
type CapabilityKey = 'quick' | 'write' | 'image' | 'code' | 'translate' | 'video' | 'music' | 'meeting' | 'research' | 'podcast' | 'qa' | 'data' | 'super' | 'ppt'

const primaryCapabilities: Array<{ key: CapabilityKey; label: string; icon: ReactNode }> = [
  { key: 'quick', label: '快速', icon: <Zap size={17} /> },
  { key: 'write', label: '帮我写作', icon: <FileText size={17} /> },
  { key: 'image', label: '图像生成', icon: <ImagePlus size={17} /> },
  { key: 'code', label: '编程', icon: <Braces size={17} /> },
  { key: 'translate', label: '翻译', icon: <Languages size={17} /> },
  { key: 'video', label: '视频生成', icon: <Video size={17} /> },
]

const moreCapabilities: Array<{ key: CapabilityKey; label: string; icon: ReactNode }> = [
  { key: 'music', label: '音乐生成', icon: <Music size={17} /> },
  { key: 'meeting', label: '记录会议', icon: <Mic size={17} /> },
  { key: 'research', label: '深入研究', icon: <Globe2 size={17} /> },
  { key: 'podcast', label: 'AI 播客', icon: <Podcast size={17} /> },
  { key: 'qa', label: '解题答疑', icon: <CircleHelp size={17} /> },
  { key: 'data', label: '数据分析', icon: <ChartColumn size={17} /> },
  { key: 'super', label: '超能模式', icon: <Sparkles size={17} /> },
  { key: 'ppt', label: 'PPT 生成', icon: <Presentation size={17} /> },
]

type ConfirmOptions = {
  title: string
  message: string
  confirmText?: string
  cancelText?: string
  tone?: 'danger' | 'warning'
}

type ConfirmDialogState = ConfirmOptions & {
  resolve: (confirmed: boolean) => void
}

type ReferenceSource = {
  title: string
  url: string
  host: string
}

const recommendedPromptConfig: AdminPromptConfig = {
  global: '统一使用自然、清晰、可信的中文表达。不要展示内部模型名称、参数或系统执行细节。遇到不确定信息时先说明不确定性，不要编造。',
  chat: '普通问答优先简洁直接。用户问“怎么做”时给步骤和注意事项，不要自动扩写成文章。',
  article: '文章要有新鲜切入角度，标题避免“看完这篇就懂了”“一文讲透”等套路。开头优先用场景、痛点、反差或故事，正文小标题要有观点。',
  document: '文档输出要结构清晰、便于复制执行。优先使用标题、列表、表格和步骤，不要加入文章化的情绪铺垫。',
  image: '图片提示词要描述主体、场景、光线、构图和风格。避免可读文字、水印、品牌 Logo、畸形肢体和过度抽象表达。',
  rewrite: '改写时尊重原意和事实，不新增未经确认的信息。根据用户要求控制幅度，默认只优化表达、结构和可读性。',
  layout: '排版时尽量保留原文含义和段落顺序。优化标题层级、列表、引用和重点呈现，不要重新创作一篇新内容。',
}

const tidyReferenceUrl = (url: string) => url.trim().replace(/[),.;，。；）]+$/g, '')

const formatReferenceHost = (url: string) => {
  try {
    return new URL(url).hostname.replace(/^www\./, '')
  } catch {
    return url.replace(/^https?:\/\//, '').split('/')[0]
  }
}

const parseReferenceSources = (content: string): ReferenceSource[] => {
  const sources: ReferenceSource[] = []
  const seen = new Set<string>()
  const addSource = (title: string, rawUrl: string) => {
    const url = tidyReferenceUrl(rawUrl)
    if (!/^https?:\/\//i.test(url) || seen.has(url)) return
    seen.add(url)
    const host = formatReferenceHost(url)
    sources.push({
      title: title.trim().replace(/\s+/g, ' ').slice(0, 72) || host,
      url,
      host,
    })
  }

  for (const match of content.matchAll(/\[([^\]]+)]\((https?:\/\/[^)\s]+)\)/g)) {
    addSource(match[1], match[2])
  }

  for (const match of content.matchAll(/https?:\/\/[^\s)\]]+/g)) {
    addSource('', match[0])
  }

  return sources
}

function App() {
  const {
    token,
    user,
    setAuthSession,
    doLogout,
    tryRefreshToken,
    ensureFreshToken,
    request,
    fetchWithAuth,
    uploadAttachments,
  } = useAuthClient()

  const [authMode, setAuthMode] = useState<'login' | 'register'>('login')
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [displayName, setDisplayName] = useState('')
  const [emailCode, setEmailCode] = useState('')
  const [emailCodeSending, setEmailCodeSending] = useState(false)
  const [emailCodeCountdown, setEmailCodeCountdown] = useState(0)
  const [newPassword, setNewPassword] = useState('')
  const [currentPassword, setCurrentPassword] = useState('')
  const [resetMode, setResetMode] = useState(false)
  const [authConfig, setAuthConfig] = useState({ allowRegistration: true, requireEmailCode: false })
  const [authError, setAuthError] = useState('')

  const [conversations, setConversations] = useState<Conversation[]>([])
  const [conversationId, setConversationId] = useState<number | null>(null)
  const conversationIdRef = useRef<number | null>(null)
  const [messages, setMessages] = useState<Message[]>([])
  const [messageModes, setMessageModes] = useState<Record<number, string>>({})
  const [messageJobStatuses, setMessageJobStatuses] = useState<Record<number, string>>({})
  const [messageImageTitles, setMessageImageTitles] = useState<Record<number, string>>({})
  const [streamingMessageId, setStreamingMessageId] = useState<number | null>(null)
  const [previewMessage, setPreviewMessage] = useState<Message | null>(null)
  const previewMessageRef = useRef<Message | null>(null)
  const [showPreviewModal, setShowPreviewModal] = useState(false)
  const [layoutPreviewText, setLayoutPreviewText] = useState<string | null>(null)
  const [previewDraft, setPreviewDraft] = useState('')
  const [editorTitle, setEditorTitle] = useState('')
  const [editorBody, setEditorBody] = useState('')
  const [editorLevel, setEditorLevel] = useState('#')
  const [imageEditStatus, setImageEditStatus] = useState('')
  const [lightboxImage, setLightboxImage] = useState<{ src: string; alt?: string } | null>(null)
  const [editorBusy, setEditorBusy] = useState(false)
  const [assistantBlocks, setAssistantBlocks] = useState<AssistantBlock[]>([])
  const assistantBlocksRef = useRef<AssistantBlock[]>([])
  const assistantIdRef = useRef(0)
  const [draft, setDraft] = useState('')
  const [isStreaming, setIsStreaming] = useState(false)
  const activeJobIdsRef = useRef<Set<number>>(new Set())
  const [attachments, setAttachments] = useState<Attachment[]>([])
  const [uploadStatus, setUploadStatus] = useState('')
  const [isToolMenuOpen, setIsToolMenuOpen] = useState(false)
  const [isComposerDragging, setIsComposerDragging] = useState(false)

  const [activePage, setActivePage] = useState<'chat' | 'admin' | 'editor' | 'tasks' | 'assets' | 'images'>('chat')
  const [isSettingsOpen, setIsSettingsOpen] = useState(false)
  const [isMobileNavOpen, setIsMobileNavOpen] = useState(false)
  const [provider, setProvider] = useState({
    name: 'OpenAI',
    baseUrl: 'https://api.openai.com/v1',
    apiKey: '',
    chatModelName: 'gpt-4.1-mini',
    imageModelName: 'gpt-image-1',
  })
  const [hasApiKey, setHasApiKey] = useState(false)
  const [apiKeyPreview, setApiKeyPreview] = useState('')
  const [providerStatus, setProviderStatus] = useState('')
  const [modelStatus, setModelStatus] = useState('')
  const [providerTestStatus, setProviderTestStatus] = useState('')
  const [availableModels, setAvailableModels] = useState<string[]>([])
  const [adminUsers, setAdminUsers] = useState<AdminUser[]>([])
  const [adminConfig, setAdminConfig] = useState<AdminRuntimeConfig | null>(null)
  const [adminDashboard, setAdminDashboard] = useState<AdminDashboardStats | null>(null)
  const [adminPromptConfig, setAdminPromptConfig] = useState<AdminPromptConfig | null>(null)
  const [toastMessage, setToastMessage] = useState('')
  const toastTimerRef = useRef<number>(0)
  const showToast = useCallback((msg: string, duration = 3000) => {
    setToastMessage(msg)
    window.clearTimeout(toastTimerRef.current)
    toastTimerRef.current = window.setTimeout(() => setToastMessage(''), duration)
  }, [])
  const [confirmDialog, setConfirmDialog] = useState<ConfirmDialogState | null>(null)
  const openConfirm = useCallback((options: ConfirmOptions) => new Promise<boolean>((resolve) => {
    setConfirmDialog({ ...options, resolve })
  }), [])
  const closeConfirm = useCallback((confirmed: boolean) => {
    setConfirmDialog((current) => {
      current?.resolve(confirmed)
      return null
    })
  }, [])
  const [adminNewUser, setAdminNewUser] = useState({
    email: '',
    password: '',
    displayName: '',
    isAdmin: false,
    isEnabled: true,
  })
  const [adminProviderUserId, setAdminProviderUserId] = useState<number | null>(null)
  const [adminProvider, setAdminProvider] = useState({
    name: 'OpenAI',
    baseUrl: '',
    apiKey: '',
    chatModelName: '',
    imageModelName: '',
  })
  const [adminProviderPreview, setAdminProviderPreview] = useState('')
  const [adminProviderStatus, setAdminProviderStatus] = useState('')
  const [adminProviderTestStatus, setAdminProviderTestStatus] = useState('')
  const [adminTab, setAdminTab] = useState<'overview' | 'accounts' | 'settings' | 'email' | 'prompts'>('overview')
  const [showCreateUser, setShowCreateUser] = useState(false)
  const [passwordResetUser, setPasswordResetUser] = useState<AdminUser | null>(null)
  const [passwordResetValue, setPasswordResetValue] = useState('')
  const [passwordResetStatus, setPasswordResetStatus] = useState('')
  const [editingUser, setEditingUser] = useState<AdminUser | null>(null)
  const [passwordStatus, setPasswordStatus] = useState('')
  const [jobSummaries, setJobSummaries] = useState<GenerationJobSummary[]>([])
  const [jobStatusFilter, setJobStatusFilter] = useState('all')
  const [jobsLoading, setJobsLoading] = useState(false)
  const [cancelingJobIds, setCancelingJobIds] = useState<Set<number>>(new Set())
  const [retryingJobIds, setRetryingJobIds] = useState<Set<number>>(new Set())
  const [selectedJobDetail, setSelectedJobDetail] = useState<GenerationJob | null>(null)
  const [jobDetailLoadingId, setJobDetailLoadingId] = useState<number | null>(null)
  const [assetKeyword, setAssetKeyword] = useState('')
  const [articleStatusFilter, setArticleStatusFilter] = useState('all')
  const [selectedArticleProjectIds, setSelectedArticleProjectIds] = useState<Set<number>>(new Set())
  const [imageStatusFilter, setImageStatusFilter] = useState('all')
  const [selectedImageAssetIds, setSelectedImageAssetIds] = useState<Set<number>>(new Set())
  const [articleAssets, setArticleAssets] = useState<ArticleAsset[]>([])
  const [imageAssets, setImageAssets] = useState<ImageAsset[]>([])
  const [assetsLoading, setAssetsLoading] = useState(false)
  const [articleVersions, setArticleVersions] = useState<ArticleVersion[] | null>(null)
  const [articleVersionTitle, setArticleVersionTitle] = useState('')
  const [articleVersionProjectId, setArticleVersionProjectId] = useState<number | null>(null)
  const [articleVersionsLoading, setArticleVersionsLoading] = useState(false)
  const [restoringVersionId, setRestoringVersionId] = useState<number | null>(null)
  const [agentOptions, setAgentOptions] = useState({
    thinkingMode: 'normal',
    platform: 'wechat',
    outputFormat: 'article',
    temperature: 0.7,
    imageCount: 3,
    enableWebSearch: false,
    showThinking: true,
    intentMode: 'auto' as IntentMode,
    stylePreset: 'balanced' as StylePreset,
  })

  const bottomRef = useRef<HTMLDivElement | null>(null)
  const messagesRef = useRef<HTMLDivElement | null>(null)
  const savedScrollRef = useRef<number | null>(null)
  const chatScrollTopRef = useRef<number | null>(null)
  const shouldRestoreChatScrollRef = useRef(false)
  const prevMessageCountRef = useRef(0)
  const richEditorRef = useRef<RichEditorHandle | null>(null)
  const sendingRef = useRef(false)
  const recoverRunningJobsRef = useRef<(id?: number | null) => void>(() => undefined)
  const ensureJobMessageRef = useRef<(job: GenerationJob) => void>(() => undefined)
  const connectGenerationJobRef = useRef<(job: GenerationJob) => void>(() => undefined)

  useEffect(() => { conversationIdRef.current = conversationId }, [conversationId])
  useEffect(() => { previewMessageRef.current = previewMessage }, [previewMessage])

  useEffect(() => {
    if (token) return
    fetch(`${API_BASE}/api/app/config`)
      .then((response) => (response.ok ? response.json() : null))
      .then((config: AppConfig | null) => {
        if (config?.auth) setAuthConfig(config.auth)
      })
      .catch(() => undefined)
  }, [token])

  useEffect(() => {
    if (token) return
    queueMicrotask(() => {
      setMessages([])
      setConversations([])
      setConversationId(null)
      setPreviewMessage(null)
      setActivePage('chat')
    })
  }, [token])

  useEffect(() => {
    if (emailCodeCountdown <= 0) return
    const timer = window.setTimeout(() => setEmailCodeCountdown((value) => Math.max(0, value - 1)), 1000)
    return () => window.clearTimeout(timer)
  }, [emailCodeCountdown])

  useEffect(() => {
    const added = messages.length > prevMessageCountRef.current
    prevMessageCountRef.current = messages.length
    if (activePage === 'chat' && !isSettingsOpen && (added || isStreaming)) {
      bottomRef.current?.scrollIntoView({ behavior: 'smooth' })
    }
  }, [messages, isStreaming, activePage, isSettingsOpen])

  const rememberChatScroll = useCallback(() => {
    if (messagesRef.current) {
      chatScrollTopRef.current = messagesRef.current.scrollTop
    }
  }, [])

  const requestRestoreChatScroll = useCallback(() => {
    shouldRestoreChatScrollRef.current = true
  }, [])

  useLayoutEffect(() => {
    if (activePage !== 'chat' || !shouldRestoreChatScrollRef.current) return
    shouldRestoreChatScrollRef.current = false
    const scrollTop = chatScrollTopRef.current
    if (scrollTop === null) return
    window.requestAnimationFrame(() => {
      if (messagesRef.current) {
        messagesRef.current.scrollTop = scrollTop
      }
    })
  }, [activePage, messages.length])

  const previewMessageId = previewMessage?.id
  useEffect(() => {
    if (!previewMessageId) return
    const currentPreviewMessage = previewMessageRef.current
    if (!currentPreviewMessage || currentPreviewMessage.id !== previewMessageId) return
    setPreviewDraft(currentPreviewMessage.content)
    setShowPreviewModal(false)
    setImageEditStatus('')
    setAssistantBlocks([])
  }, [previewMessageId])

  useEffect(() => {
    assistantBlocksRef.current = assistantBlocks
  }, [assistantBlocks])

  async function submitAuth(event: FormEvent) {
    event.preventDefault()
    setAuthError('')
    try {
      const payload = authMode === 'register' ? { email, password, displayName, emailCode } : { email, password }
      const result = await request<AuthResponse>(`/api/auth/${authMode}`, {
        method: 'POST',
        body: JSON.stringify(payload),
      })
      setAuthSession(result)
    } catch (error) {
      setAuthError(error instanceof Error ? error.message : '登录失败')
    }
  }

  async function sendEmailCode() {
    if (emailCodeSending || emailCodeCountdown > 0) return
    if (!email.trim()) {
      setAuthError('请先填写邮箱。')
      return
    }
    setEmailCodeSending(true)
    setAuthError('')
    try {
      const result = await request<{ sent: boolean; message: string }>('/api/auth/email-code', {
        method: 'POST',
        body: JSON.stringify({ email }),
      })
      if (result.sent) {
        setEmailCodeCountdown(60)
      }
      setAuthError(result.message)
    } catch (error) {
      setAuthError(error instanceof Error ? error.message : '验证码发送失败')
    } finally {
      setEmailCodeSending(false)
    }
  }

  async function resetPassword() {
    setAuthError('')
    try {
      await request<{ message: string }>('/api/auth/reset-password', {
        method: 'POST',
        body: JSON.stringify({ email, emailCode, newPassword }),
      })
      setAuthError('密码已重置，请使用新密码登录。')
      setResetMode(false)
      setPassword(newPassword)
    } catch (error) {
      setAuthError(error instanceof Error ? error.message : '密码重置失败')
    }
  }

  async function changeOwnPassword() {
    setPasswordStatus('修改中...')
    try {
      await request<{ message: string }>('/api/auth/change-password', {
        method: 'POST',
        body: JSON.stringify({ currentPassword, newPassword }),
      })
      setCurrentPassword('')
      setNewPassword('')
      setPasswordStatus('密码已修改')
    } catch (error) {
      setPasswordStatus(error instanceof Error ? error.message : '密码修改失败')
    }
  }

  const loadMessages = useCallback(async (id: number) => {
    setConversationId(id)
    setMessages(await request<Message[]>(`/api/conversations/${id}/messages`))
    recoverRunningJobsRef.current(id)
  }, [request])

  const loadJobs = useCallback(async (status = jobStatusFilter) => {
    setJobsLoading(true)
    try {
      const query = status && status !== 'all' ? `?status=${encodeURIComponent(status)}&limit=80` : '?limit=80'
      setJobSummaries(await request<GenerationJobSummary[]>(`/api/conversations/jobs${query}`))
    } catch (error) {
      showToast(error instanceof Error ? error.message : '任务列表加载失败')
    } finally {
      setJobsLoading(false)
    }
  }, [jobStatusFilter, request, showToast])

  const loadAssets = useCallback(async () => {
    setAssetsLoading(true)
    try {
      const [articles, images] = await Promise.all([
        request<ArticleAsset[]>('/api/assets/articles'),
        request<ImageAsset[]>('/api/assets/images'),
      ])
      setArticleAssets(articles)
      setImageAssets(images)
      const articleProjectIds = new Set(articles.map((item) => item.projectId))
      setSelectedArticleProjectIds((current) => new Set(Array.from(current).filter((id) => articleProjectIds.has(id))))
      const imageIds = new Set(images.map((item) => item.id))
      setSelectedImageAssetIds((current) => new Set(Array.from(current).filter((id) => imageIds.has(id))))
    } catch (error) {
      showToast(error instanceof Error ? error.message : '资产库加载失败')
    } finally {
      setAssetsLoading(false)
    }
  }, [request, showToast])

  const loadConversations = useCallback(async () => {
    const rows = await request<Conversation[]>('/api/conversations')
    setConversations(rows)
    if (!conversationIdRef.current && rows.length > 0) {
      setConversationId(rows[0].id)
      loadMessages(rows[0].id)
    }
  }, [loadMessages, request])

  async function deleteConversation(item: Conversation) {
    const ok = await openConfirm({
      title: '删除会话',
      message: `删除“${item.title}”？删除后不可恢复。`,
      confirmText: '删除',
      tone: 'danger',
    })
    if (!ok) return
    try {
      await request<void>(`/api/conversations/${item.id}`, { method: 'DELETE' })
      setConversations((current) => current.filter((conversation) => conversation.id !== item.id))
      if (conversationIdRef.current === item.id) {
        setConversationId(null)
        setMessages([])
        setPreviewMessage(null)
        setActivePage('chat')
      }
      showToast('会话已删除')
    } catch (error) {
      showToast(error instanceof Error ? error.message : '删除失败')
    }
  }

  const loadProvider = useCallback(async () => {
    try {
      const current = (await request<ProviderResponse[]>('/api/providers'))[0]
      if (!current) return
      setProvider({
        name: current.name || 'OpenAI',
        baseUrl: current.baseUrl || 'https://api.openai.com/v1',
        apiKey: '',
        chatModelName: current.chatModelName || 'gpt-4.1-mini',
        imageModelName: current.imageModelName || 'gpt-image-1',
      })
      setHasApiKey(current.hasApiKey)
      setApiKeyPreview(current.apiKeyPreview)
    } catch {
      // Keep defaults when provider is not configured.
    }
  }, [request, setProvider])

  async function fetchModels() {
    setModelStatus('正在获取模型列表...')
    try {
      const result = await request<ModelListResponse>('/api/providers/models', {
        method: 'POST',
        body: JSON.stringify({ name: provider.name, baseUrl: provider.baseUrl, apiKey: provider.apiKey }),
      })
      setAvailableModels(result.models)
      setModelStatus(result.models.length > 0 ? `已获取 ${result.models.length} 个模型。` : '接口返回成功，但没有模型数据。')
    } catch (error) {
      setModelStatus(error instanceof Error ? error.message : '模型列表获取失败')
    }
  }

  async function testProvider(type: 'chat' | 'image') {
    setProviderTestStatus(type === 'chat' ? '正在测试聊天模型...' : '正在测试生图模型...')
    try {
      const result = await request<ProviderTestResponse>('/api/providers/test', {
        method: 'POST',
        body: JSON.stringify({ ...provider, testType: type }),
      })
      setProviderTestStatus(result.detail ? `${result.message} ${result.detail}` : result.message)
    } catch (error) {
      setProviderTestStatus(error instanceof Error ? error.message : '模型测试失败')
    }
  }

  async function uploadFiles(files: FileList | File[] | null) {
    if (!files?.length) return
    setUploadStatus('上传中...')
    try {
      const uploaded = await uploadAttachments(files)
      setAttachments((current) => [...current, ...uploaded])
      setUploadStatus(`已上传 ${uploaded.length} 个文件。`)
    } catch (error) {
      setUploadStatus(error instanceof Error ? error.message : '上传失败')
    }
  }

  async function handleComposerPaste(event: ClipboardEvent<HTMLTextAreaElement>) {
    const files = Array.from(event.clipboardData.files)
    if (!files.length) return

    event.preventDefault()
    await uploadFiles(files)
    showToast(files.some((file) => file.type.startsWith('image/')) ? '已从剪贴板添加图片' : '已从剪贴板添加文件')
  }

  async function handleComposerDrop(event: DragEvent<HTMLDivElement>) {
    event.preventDefault()
    setIsComposerDragging(false)
    await uploadFiles(event.dataTransfer.files)
  }

  function handleComposerDragLeave(event: DragEvent<HTMLDivElement>) {
    if (!event.currentTarget.contains(event.relatedTarget as Node | null)) {
      setIsComposerDragging(false)
    }
  }

  const editorImageInputRef = useRef<HTMLInputElement | null>(null)
  const uploadEditorImage = async (files: FileList | null) => {
    if (!files?.length) return
    try {
      setImageEditStatus('上传图片中...')
      const uploaded = await uploadAttachments(files)
      for (const file of uploaded) {
        if (file.contentType.startsWith('image/')) {
          richEditorRef.current?.insertImage(file.url, file.fileName)
        }
      }
      setImageEditStatus(`已插入 ${uploaded.length} 张图片`)
    } catch (error) {
      setImageEditStatus(error instanceof Error ? error.message : '图片上传失败')
    }
    if (editorImageInputRef.current) editorImageInputRef.current.value = ''
  }

  async function saveProvider(event: FormEvent) {
    event.preventDefault()
    setProviderStatus('保存中...')
    try {
      const saved = await request<ProviderResponse>('/api/providers', {
        method: 'POST',
        body: JSON.stringify(provider),
      })
      setHasApiKey(saved.hasApiKey)
      setApiKeyPreview(saved.apiKeyPreview)
      setProvider((value) => ({ ...value, apiKey: '' }))
      setProviderStatus('已保存。API Key 已脱敏保存，模型会按当前选择使用。')
    } catch (error) {
      setProviderStatus(error instanceof Error ? error.message : '保存失败')
    }
  }

  const loadAdminData = useCallback(async () => {
    if (!user?.isAdmin) return
    try {
      const [dashboard, users, config, promptConfig] = await Promise.all([
        request<AdminDashboardStats>('/api/admin/dashboard'),
        request<AdminUser[]>('/api/admin/users'),
        request<AdminRuntimeConfig>('/api/admin/runtime-config'),
        request<AdminPromptConfig>('/api/admin/prompt-config'),
      ])
      setAdminDashboard(dashboard)
      setAdminUsers(users)
      setAdminConfig(config)
      setAdminPromptConfig(promptConfig)
    } catch (error) {
      showToast(error instanceof Error ? error.message : '管理数据加载失败')
    }
  }, [request, showToast, user?.isAdmin])

  useEffect(() => {
    if (!token) return
    queueMicrotask(() => {
      loadConversations()
      loadProvider()
    })
  }, [token, loadConversations, loadProvider])

  useEffect(() => {
    if (!isSettingsOpen) return
    queueMicrotask(() => loadAdminData())
  }, [isSettingsOpen, loadAdminData])

  useEffect(() => {
    if (activePage !== 'admin') return
    queueMicrotask(() => loadAdminData())
  }, [activePage, loadAdminData])

  async function saveAdminConfig() {
    if (!adminConfig) return
    try {
      const saved = await request<AdminRuntimeConfig>('/api/admin/runtime-config', {
        method: 'POST',
        body: JSON.stringify(adminConfig),
      })
      setAdminConfig(saved)
      showToast('配置已保存')
    } catch (error) {
      showToast(error instanceof Error ? error.message : '保存失败')
    }
  }

  async function saveAdminPromptConfig() {
    if (!adminPromptConfig) return
    try {
      const saved = await request<AdminPromptConfig>('/api/admin/prompt-config', {
        method: 'POST',
        body: JSON.stringify(adminPromptConfig),
      })
      setAdminPromptConfig(saved)
      showToast('提示词配置已保存')
    } catch (error) {
      showToast(error instanceof Error ? error.message : '提示词保存失败')
    }
  }

  async function toggleAdminUser(item: AdminUser, patch: Partial<AdminUser>) {
    const updated = { ...item, ...patch }
    try {
      const saved = await request<AdminUser>(`/api/admin/users/${item.id}`, {
        method: 'PUT',
        body: JSON.stringify({
          displayName: updated.displayName,
          isAdmin: updated.isAdmin,
          isEnabled: updated.isEnabled,
        }),
      })
      setAdminUsers((current) => current.map((user) => user.id === saved.id ? saved : user))
      showToast('账号已更新')
    } catch (error) {
      showToast(error instanceof Error ? error.message : '账号更新失败')
    }
  }

  async function createAdminUser() {
    try {
      const saved = await request<AdminUser>('/api/admin/users', {
        method: 'POST',
        body: JSON.stringify(adminNewUser),
      })
      setAdminUsers((current) => [saved, ...current])
      setAdminNewUser({ email: '', password: '', displayName: '', isAdmin: false, isEnabled: true })
      showToast('账号已创建')
    } catch (error) {
      showToast(error instanceof Error ? error.message : '账号创建失败')
    }
  }

  function openPasswordReset(item: AdminUser) {
    setPasswordResetUser(item)
    setPasswordResetValue('')
    setPasswordResetStatus('')
  }

  async function resetAdminPassword() {
    if (!passwordResetUser) return
    const value = passwordResetValue.trim()
    if (value.length < 6) {
      setPasswordResetStatus('新密码至少 6 位。')
      return
    }
    setPasswordResetStatus('保存中...')
    try {
      await request<{ message: string }>(`/api/admin/users/${passwordResetUser.id}/password`, {
        method: 'POST',
        body: JSON.stringify({ newPassword: value }),
      })
      setPasswordResetUser(null)
      setPasswordResetValue('')
      showToast('密码已重置')
    } catch (error) {
      setPasswordResetStatus(error instanceof Error ? error.message : '密码重置失败')
    }
  }

  function goToChat() {
    requestRestoreChatScroll()
    setActivePage('chat')
    setIsSettingsOpen(false)
    setIsMobileNavOpen(false)
  }

  async function openConversationFromSidebar(id: number) {
    savedScrollRef.current = null
    setActivePage('chat')
    setIsSettingsOpen(false)
    setIsMobileNavOpen(false)
    await loadMessages(id)
  }

  function openSettings() {
    rememberChatScroll()
    setIsMobileNavOpen(false)
    setIsSettingsOpen(true)
  }

  function closeSettings() {
    requestRestoreChatScroll()
    setIsSettingsOpen(false)
  }

  function openAdminPage() {
    rememberChatScroll()
    setActivePage('admin')
  }

  function openTaskCenter() {
    rememberChatScroll()
    setIsMobileNavOpen(false)
    setActivePage('tasks')
    void loadJobs()
  }

  function openAssetLibrary() {
    rememberChatScroll()
    setIsMobileNavOpen(false)
    setActivePage('assets')
    void loadAssets()
  }

  function openImageLibrary() {
    rememberChatScroll()
    setIsMobileNavOpen(false)
    setActivePage('images')
    void loadAssets()
  }

  async function editAdminProvider(item: AdminUser) {
    setAdminProviderUserId(item.id)
    setAdminProviderStatus('正在加载账号 API 配置...')
    try {
      const current = await request<ProviderResponse>(`/api/admin/users/${item.id}/provider`)
      setAdminProvider({
        name: current.name || 'OpenAI',
        baseUrl: current.baseUrl,
        apiKey: '',
        chatModelName: current.chatModelName,
        imageModelName: current.imageModelName,
      })
      setAdminProviderPreview(current.apiKeyPreview)
      setAdminProviderStatus(current.hasApiKey ? `已配置 API Key：${current.apiKeyPreview}` : '该账号还没有配置 API Key')
    } catch (error) {
      setAdminProviderStatus(error instanceof Error ? error.message : '账号 API 配置加载失败')
    }
  }

  async function saveAdminProvider() {
    if (!adminProviderUserId) return
    setAdminProviderStatus('保存账号 API 配置中...')
    try {
      const saved = await request<ProviderResponse>(`/api/admin/users/${adminProviderUserId}/provider`, {
        method: 'POST',
        body: JSON.stringify(adminProvider),
      })
      setAdminProvider((value) => ({ ...value, apiKey: '' }))
      setAdminProviderPreview(saved.apiKeyPreview)
      setAdminProviderStatus('账号 API 配置已保存')
    } catch (error) {
      setAdminProviderStatus(error instanceof Error ? error.message : '账号 API 配置保存失败')
    }
  }

  async function testAdminProvider(type: 'chat' | 'image') {
    setAdminProviderTestStatus(type === 'chat' ? '正在测试聊天模型...' : '正在测试生图模型...')
    try {
      const result = await request<ProviderTestResponse>('/api/providers/test', {
        method: 'POST',
        body: JSON.stringify({ ...adminProvider, testType: type }),
      })
      setAdminProviderTestStatus(result.detail ? `${result.message} ${result.detail}` : result.message)
    } catch (error) {
      setAdminProviderTestStatus(error instanceof Error ? error.message : '模型测试失败')
    }
  }

  async function fillFromMyProvider() {
    if (!adminProviderUserId) return
    if (provider.apiKey) {
      setAdminProvider({
        name: provider.name || 'OpenAI',
        baseUrl: provider.baseUrl || '',
        apiKey: provider.apiKey,
        chatModelName: provider.chatModelName || '',
        imageModelName: provider.imageModelName || '',
      })
      setAdminProviderStatus('已引入当前表单中的管理员配置，点击保存后生效。')
      return
    }

    setAdminProviderStatus('正在引入并保存管理员配置...')
    try {
      const saved = await request<ProviderResponse>(`/api/admin/users/${adminProviderUserId}/provider/copy-current`, {
        method: 'POST',
      })
      setAdminProvider({
        name: saved.name || 'OpenAI',
        baseUrl: saved.baseUrl,
        apiKey: '',
        chatModelName: saved.chatModelName,
        imageModelName: saved.imageModelName,
      })
      setAdminProviderPreview(saved.apiKeyPreview)
      setAdminProviderStatus(saved.hasApiKey ? `已引入并保存管理员配置，API Key：${saved.apiKeyPreview}` : '已引入配置，但未检测到 API Key。')
    } catch (error) {
      setAdminProviderStatus(error instanceof Error ? error.message : '引入管理员配置失败')
    }
  }

  async function sendMessage(event: FormEvent) {
    event.preventDefault()
    await submitMessage(draft.trim())
  }

  function handleComposerKeyDown(event: KeyboardEvent<HTMLTextAreaElement>) {
    if (event.key !== 'Enter' || event.shiftKey || event.nativeEvent.isComposing) {
      return
    }

    event.preventDefault()
    void submitMessage(draft.trim())
  }

  function applyCapability(key: CapabilityKey) {
    const source = draft.trim()
    const instruction: Record<CapabilityKey, string> = {
      quick: '请快速理解我的需求，并直接给出可执行结果：\n\n',
      write: '请帮我写作下面内容，要求结构清楚、表达自然、可直接使用：\n\n',
      image: '请根据下面需求生成图片，并给出适合图像模型的清晰提示词：\n\n',
      code: '请作为编程助手处理下面问题，优先给出可运行方案和关键代码：\n\n',
      translate: '请把下面内容翻译成目标语言，并保留原意、语气和格式：\n\n',
      video: '请把下面需求整理成视频生成方案，包含脚本、镜头、画面和旁白：\n\n',
      music: '请把下面需求整理成音乐生成提示，包含风格、情绪、节奏、乐器和歌词方向：\n\n',
      meeting: '请帮我整理会议记录，输出议题、结论、待办、负责人和时间节点：\n\n',
      research: '请进行深入研究，先拆解问题，再结合可验证信息给出结论、依据和建议：\n\n',
      podcast: '请把下面内容改写成 AI 播客脚本，包含开场、分段对话和结尾总结：\n\n',
      qa: '请逐步解答下面问题，说明关键思路，并给出最终答案：\n\n',
      data: '请分析下面数据或材料，输出洞察、趋势、异常点和行动建议：\n\n',
      super: '请用深度思考模式处理下面复杂任务，先规划，再给出完整结果：\n\n',
      ppt: '请把下面内容整理成一份可直接导出为 PPTX 的中文演示稿。要求：先给封面标题，再按页输出，每页包含页标题和 3-5 个要点，控制文字密度，适合商务汇报。\n\n',
    }

    setIsToolMenuOpen(false)
    setAgentOptions((current) => {
      const next = { ...current }
      if (key === 'image') next.intentMode = 'image'
      if (key === 'write') next.intentMode = 'article'
      if (key === 'ppt') {
        next.intentMode = 'document'
        next.outputFormat = 'pptx'
      }
      if (key === 'research' || key === 'super') {
        next.thinkingMode = 'deep'
        next.enableWebSearch = true
      }
      return next
    })
    setDraft(source ? `${instruction[key]}${source}` : instruction[key])
  }

  async function submitMessage(content: string) {
    if (!content || isStreaming || sendingRef.current) return

    sendingRef.current = true
    setDraft('')
    try {
      const job = await request<GenerationJob>('/api/conversations/jobs', {
        method: 'POST',
        body: JSON.stringify({ content, conversationId, attachments, options: agentOptions }),
      })
      setConversationId(job.conversationId)
      setStreamingMessageId(job.assistantMessageId)
      setMessages((current) => [
        ...current,
        { id: job.userMessageId, role: 'user', content, createdAt: new Date().toISOString() },
        {
          id: job.assistantMessageId,
          role: 'assistant',
          content: job.content,
          thinking: job.thinking,
          createdAt: job.updatedAt,
          messageType: job.messageType,
        },
      ])
      setAttachments([])
      connectGenerationJobRef.current(job)
    } catch (error) {
      showToast(error instanceof Error ? error.message : '创建生成任务失败')
    } finally {
      sendingRef.current = false
    }
  }

  async function retryFromMessage(message: Message) {
    await submitMessage(message.content)
  }

  async function downloadMessageExport(message: Message, format: 'docx' | 'pptx') {
    const cid = conversationIdRef.current
    if (!cid || isMessageGenerating(message)) return

    try {
      const response = await fetchWithAuth(`${API_BASE}/api/conversations/${cid}/messages/${message.id}/export?format=${format}`)
      if (!response.ok) {
        const body = await response.json().catch(() => ({}))
        throw new Error(typeof body.message === 'string' ? body.message : '导出失败')
      }

      const blob = await response.blob()
      const url = window.URL.createObjectURL(blob)
      const link = document.createElement('a')
      const title = getCleanDocumentTitle(cleanArticleContent(message.content)).replace(/[\\/:*?"<>|]+/g, '').slice(0, 32) || 'VeraMedia'
      link.href = url
      link.download = `${title}.${format}`
      document.body.appendChild(link)
      link.click()
      link.remove()
      window.URL.revokeObjectURL(url)
      showToast(format === 'pptx' ? 'PPTX 已生成' : 'DOCX 已生成')
    } catch (error) {
      showToast(error instanceof Error ? error.message : '导出失败')
    }
  }

  async function recoverRunningJobs(id?: number | null) {
    try {
      const query = id ? `?conversationId=${id}` : ''
      const jobs = await request<GenerationJob[]>(`/api/conversations/jobs/running${query}`)
      jobs.forEach((job) => {
        ensureJobMessageRef.current(job)
        connectGenerationJobRef.current(job)
      })
    } catch {
      // Recovery is best effort; normal message loading still works without it.
    }
  }
  function ensureJobMessage(job: GenerationJob) {
    setMessageJobStatuses((current) => ({ ...current, [job.assistantMessageId]: job.status }))
    if (job.messageType) {
      setMessageModes((current) => ({ ...current, [job.assistantMessageId]: job.messageType || '' }))
    }
    const imageTitle = getGeneratingImageTitle(job.content)
    if (imageTitle) {
      setMessageImageTitles((current) => ({ ...current, [job.assistantMessageId]: imageTitle }))
    }
    setMessages((current) => {
      const hasAssistant = current.some((item) => item.id === job.assistantMessageId)
      if (hasAssistant) {
        return current.map((item) => item.id === job.assistantMessageId
          ? {
              ...item,
              content: job.content || item.content,
              thinking: job.thinking,
              messageType: job.messageType ?? item.messageType,
            }
          : item)
      }

      return [
        ...current,
        {
          id: job.assistantMessageId,
          role: 'assistant',
          content: job.content,
          thinking: job.thinking,
          createdAt: job.updatedAt,
          messageType: job.messageType,
        },
      ]
    })
  }

  function applyJobSnapshot(job: GenerationJob) {
    setConversationId(job.conversationId)
    setMessageJobStatuses((current) => ({ ...current, [job.assistantMessageId]: job.status }))
    if (job.messageType) {
      setMessageModes((current) => ({ ...current, [job.assistantMessageId]: job.messageType || '' }))
    }
    const imageTitle = getGeneratingImageTitle(job.content)
    if (imageTitle) {
      setMessageImageTitles((current) => ({ ...current, [job.assistantMessageId]: imageTitle }))
    }
    setMessages((current) => {
      const hasAssistant = current.some((item) => item.id === job.assistantMessageId)
      if (!hasAssistant) {
        return [
          ...current,
          {
            id: job.assistantMessageId,
            role: 'assistant',
            content: job.content,
            thinking: job.thinking,
            createdAt: job.updatedAt,
            messageType: job.messageType,
          },
        ]
      }

      return current.map((item) => item.id === job.assistantMessageId
        ? {
            ...item,
            content: job.content || item.content,
            thinking: job.thinking ?? item.thinking,
            messageType: job.messageType ?? item.messageType,
          }
        : item)
    })
  }

  function updateJobActivity(jobId: number, active: boolean) {
    const next = new Set(activeJobIdsRef.current)
    if (active) next.add(jobId)
    else next.delete(jobId)
    activeJobIdsRef.current = next
    setIsStreaming(next.size > 0)
  }

  async function connectGenerationJob(job: GenerationJob) {
    if (activeJobIdsRef.current.has(job.id)) return
    updateJobActivity(job.id, true)
    setStreamingMessageId(job.assistantMessageId)
    let pendingSnapshot: GenerationJob | null = null
    let flushTimer = 0
    const flushPendingSnapshot = () => {
      if (!pendingSnapshot) return
      const snapshot = pendingSnapshot
      pendingSnapshot = null
      applyJobSnapshot(snapshot)
    }
    const scheduleJobSnapshot = (snapshot: GenerationJob) => {
      pendingSnapshot = snapshot
      if (flushTimer) return
      flushTimer = window.setTimeout(() => {
        flushTimer = 0
        flushPendingSnapshot()
      }, 160)
    }

    try {
      const response = await fetchWithAuth(`${API_BASE}/api/conversations/jobs/${job.id}/stream?sinceVersion=${job.version}`)
      if (!response.ok || !response.body) {
        throw new Error(response.statusText || '任务进度连接失败')
      }

      await readServerSentEvents(response, (event) => {
        if (event.event !== 'job' && event.event !== 'done') return
        const data = event.data as GenerationJob
        if (!data?.assistantMessageId) return
        if (event.event === 'done') {
          if (flushTimer) {
            window.clearTimeout(flushTimer)
            flushTimer = 0
          }
          pendingSnapshot = data
          flushPendingSnapshot()
          return
        }
        scheduleJobSnapshot(data)
      })
    } catch (error) {
      showToast(`进度连接中断，正在后台继续：${error instanceof Error ? error.message : '连接异常'}`)
      window.setTimeout(() => recoverRunningJobsRef.current(job.conversationId), 2500)
    } finally {
      if (flushTimer) {
        window.clearTimeout(flushTimer)
        flushTimer = 0
      }
      flushPendingSnapshot()
      const cid = conversationIdRef.current || job.conversationId
      if (cid) {
        try {
          setMessages(await request<Message[]>(`/api/conversations/${cid}/messages`))
        } catch {
          // The live job snapshot remains available even if the final sync fails.
        }
      }
      updateJobActivity(job.id, false)
      setStreamingMessageId((current) => current === job.assistantMessageId ? null : current)
      loadConversations()
      if (activePage === 'tasks') {
        void loadJobs()
      }
    }
  }

  useEffect(() => {
    recoverRunningJobsRef.current = (id?: number | null) => { void recoverRunningJobs(id) }
    ensureJobMessageRef.current = ensureJobMessage
    connectGenerationJobRef.current = (job: GenerationJob) => { void connectGenerationJob(job) }
  })

  function logout() {
    setIsMobileNavOpen(false)
    doLogout()
  }

  const modelOptions = Array.from(new Set([provider.chatModelName, provider.imageModelName, ...availableModels].filter(Boolean)))
  const chatModelListId = 'chat-model-options'
  const imageModelListId = 'image-model-options'
  const sanitizeThinkingText = (text: string) => text
    .replace(/将使用(?:聊天|生图)模型[:：]\s*[^。.\n]+[。.]/g, '已准备生成能力。')
    .replace(/正在使用生图模型生成图片[:：]\s*[^。.\n]+[。.]/g, '正在生成配图。')
    .replace(/正在使用生图模型[:：]\s*[^。.\n]+[。.]/g, '正在准备生成图片。')
    .replace(/识别为直接生图请求，将使用生图模型[:：]\s*[^。.\n]+[。.]/g, '识别为直接生图请求，开始准备图片生成。')
  const getCleanDocumentTitle = (content: string) => getDocumentTitle(content)
    .replace(/^(?:文章标题|标题|题目|主题)\s*[:：]\s*/, '')
    .trim()
  const getPreviousUserContent = (messageIndex: number) => {
    for (let i = messageIndex - 1; i >= 0; i -= 1) {
      if (messages[i]?.role === 'user') return messages[i].content
    }
    return ''
  }
  const getMessageMode = (message: Message) => messageModes[message.id] ?? message.messageType ?? null
  const isTerminalJobStatus = (status: string | undefined) => status === 'completed' || status === 'failed' || status === 'canceled'
  const isMessageGenerating = (message: Message) => {
    const status = messageJobStatuses[message.id]
    return streamingMessageId === message.id || status === 'pending' || status === 'running'
  }
  const isGeneratingImageMessage = (message: Message) => {
    const status = messageJobStatuses[message.id]
    return getMessageMode(message) === 'image'
      && !isTerminalJobStatus(status)
      && (isMessageGenerating(message) || Boolean(getGeneratingImageTitle(message.content)))
  }
  const getGeneratingImageDisplayTitle = (message: Message) =>
    getGeneratingImageTitle(message.content) || messageImageTitles[message.id] || '图片'
  const getPageTitle = () => {
    if (activePage === 'admin') return '后台管理'
    if (activePage === 'tasks') return '任务中心'
    if (activePage === 'assets') return '资产库'
    if (activePage === 'images') return '图片资产'
    return 'AI 工作台'
  }
  const getPageSubtitle = () => {
    if (activePage === 'admin') return '管理注册策略、邮箱验证码服务和用户账号权限。'
    if (activePage === 'tasks') return '追踪生成进度、失败原因和历史结果。'
    if (activePage === 'assets') return '沉淀文章资产，方便复用与继续编辑。'
    if (activePage === 'images') return '集中管理生成图片、配图和可复用视觉素材。'
    return '问答、写作、配图和图片生成，都可以在这里完成。'
  }
  const jobStatusLabels: Record<GenerationJobSummary['status'], string> = {
    pending: '排队中',
    running: '生成中',
    completed: '已完成',
    failed: '失败',
    canceled: '已取消',
  }
  const assetStatusLabels: Record<string, string> = {
    draft: '草稿',
    pending: '排队中',
    running: '生成中',
    completed: '已完成',
    failed: '失败',
    canceled: '已取消',
  }
  const getAssetStatusLabel = (status?: string | null) => assetStatusLabels[status || ''] || '未知'
  const jobTypeLabel = (value?: string | null) => {
    if (value === 'article') return '文章'
    if (value === 'article_image') return '文章配图'
    if (value === 'document') return '文档'
    if (value === 'image') return '图片'
    if (value === 'chat') return '问答'
    return '内容任务'
  }
  const formatArticleAssetExcerpt = (item: ArticleAsset) => {
    const clean = stripReferenceSections(item.excerpt || '')
      .replace(/(?:参考资料|参考来源|引用来源|资料来源|参考文献|参考链接|来源链接|References|Sources)\s*[:：]?[\s\S]*$/i, '')
      .replace(/\[[^\]]+]\([^)]+\)/g, '')
      .replace(/https?:\/\/\S+/gi, '')
      .replace(/[()[\]#*_`>-]/g, '')
      .replace(/\s+/g, ' ')
      .trim()
    return clean || '暂无摘要'
  }
  const normalizedAssetKeyword = assetKeyword.trim().toLowerCase()
  const keywordFilteredArticleAssets = normalizedAssetKeyword
    ? articleAssets.filter((item) =>
      `${item.title} ${formatArticleAssetExcerpt(item)} ${item.platform} ${item.status} ${getAssetStatusLabel(item.status)}`.toLowerCase().includes(normalizedAssetKeyword))
    : articleAssets
  const filteredArticleAssets = articleStatusFilter === 'all'
    ? keywordFilteredArticleAssets
    : keywordFilteredArticleAssets.filter((item) => item.status === articleStatusFilter)
  const selectedVisibleArticleProjectIds = filteredArticleAssets
    .map((item) => item.projectId)
    .filter((id) => selectedArticleProjectIds.has(id))
  const hasSelectedArticleAssets = selectedArticleProjectIds.size > 0
  const allVisibleArticleAssetsSelected = filteredArticleAssets.length > 0 && selectedVisibleArticleProjectIds.length === filteredArticleAssets.length
  const keywordFilteredImageAssets = normalizedAssetKeyword
    ? imageAssets.filter((item) =>
      `${item.projectTitle} ${item.prompt} ${item.status} ${getAssetStatusLabel(item.status)}`.toLowerCase().includes(normalizedAssetKeyword))
    : imageAssets
  const filteredImageAssets = imageStatusFilter === 'all'
    ? keywordFilteredImageAssets
    : keywordFilteredImageAssets.filter((item) => item.status === imageStatusFilter)
  const selectedVisibleImageIds = filteredImageAssets
    .map((item) => item.id)
    .filter((id) => selectedImageAssetIds.has(id))
  const hasSelectedImageAssets = selectedImageAssetIds.size > 0
  const allVisibleImageAssetsSelected = filteredImageAssets.length > 0 && selectedVisibleImageIds.length === filteredImageAssets.length
  const jobOverview = {
    total: jobSummaries.length,
    running: jobSummaries.filter((item) => item.status === 'running' || item.status === 'pending').length,
    completed: jobSummaries.filter((item) => item.status === 'completed').length,
    failed: jobSummaries.filter((item) => item.status === 'failed').length,
  }
  const jobFailureRate = jobOverview.total > 0 ? Math.round((jobOverview.failed / jobOverview.total) * 100) : 0
  const isCancelableJob = (status: GenerationJobSummary['status']) => status === 'pending' || status === 'running'
  const isRetryableJob = (status: GenerationJobSummary['status']) => status === 'failed' || status === 'canceled'
  const openJobConversation = async (item: GenerationJobSummary) => {
    await loadMessages(item.conversationId)
    goToChat()
  }
  const openJobDetail = async (item: GenerationJobSummary) => {
    setJobDetailLoadingId(item.id)
    try {
      setSelectedJobDetail(await request<GenerationJob>(`/api/conversations/jobs/${item.id}`))
    } catch (error) {
      showToast(error instanceof Error ? error.message : '任务详情加载失败')
    } finally {
      setJobDetailLoadingId(null)
    }
  }
  const cancelJob = async (item: GenerationJobSummary) => {
    if (!await openConfirm({
      title: '取消生成任务',
      message: '取消后任务会停止，已生成的部分内容会保留。',
      confirmText: '取消任务',
      tone: 'warning',
    })) return
    setCancelingJobIds((current) => new Set(current).add(item.id))
    try {
      const canceled = await request<GenerationJob>(`/api/conversations/jobs/${item.id}/cancel`, { method: 'POST' })
      setJobSummaries((current) => current.map((job) => job.id === item.id
        ? { ...job, status: canceled.status, error: canceled.error ?? '任务已取消。', contentPreview: canceled.content || job.contentPreview, updatedAt: canceled.updatedAt }
        : job))
      setMessageJobStatuses((current) => ({ ...current, [canceled.assistantMessageId]: canceled.status }))
      setMessages((current) => current.map((message) => message.id === canceled.assistantMessageId
        ? { ...message, content: canceled.content || message.content }
        : message))
      showToast('任务已取消')
    } catch (error) {
      showToast(error instanceof Error ? error.message : '取消任务失败')
    } finally {
      setCancelingJobIds((current) => {
        const next = new Set(current)
        next.delete(item.id)
        return next
      })
      if (activePage === 'tasks') void loadJobs()
      if (activePage === 'admin') void loadAdminData()
    }
  }
  const retryJob = async (item: GenerationJobSummary) => {
    setRetryingJobIds((current) => new Set(current).add(item.id))
    try {
      const job = await request<GenerationJob>(`/api/conversations/jobs/${item.id}/retry`, { method: 'POST' })
      setJobSummaries((current) => [{
        id: job.id,
        conversationId: job.conversationId,
        conversationTitle: item.conversationTitle,
        userMessageId: job.userMessageId,
        assistantMessageId: job.assistantMessageId,
        status: job.status,
        jobType: item.jobType,
        requestPreview: item.requestPreview,
        contentPreview: job.content,
        messageType: job.messageType,
        error: job.error,
        createdAt: new Date().toISOString(),
        updatedAt: job.updatedAt,
        startedAt: null,
        completedAt: null,
      }, ...current])
      showToast('已重新提交任务')
      connectGenerationJob(job)
    } catch (error) {
      showToast(error instanceof Error ? error.message : '重试任务失败')
    } finally {
      setRetryingJobIds((current) => {
        const next = new Set(current)
        next.delete(item.id)
        return next
      })
      if (activePage === 'tasks') void loadJobs()
      if (activePage === 'admin') void loadAdminData()
    }
  }
  const openArticleAssetConversation = async (item: ArticleAsset) => {
    if (!item.conversationId) {
      showToast('这篇资产没有关联会话')
      return
    }
    await loadMessages(item.conversationId)
    goToChat()
  }
  const openArticleVersions = async (item: ArticleAsset) => {
    setArticleVersionTitle(item.title)
    setArticleVersionProjectId(item.projectId)
    setArticleVersions([])
    setArticleVersionsLoading(true)
    try {
      setArticleVersions(await request<ArticleVersion[]>(`/api/assets/articles/${item.projectId}/versions`))
    } catch (error) {
      setArticleVersions(null)
      showToast(error instanceof Error ? error.message : '版本加载失败')
    } finally {
      setArticleVersionsLoading(false)
    }
  }
  const restoreArticleVersion = async (item: ArticleVersion) => {
    if (!articleVersionProjectId) return
    setRestoringVersionId(item.id)
    try {
      const restored = await request<ArticleAsset>(`/api/assets/articles/${articleVersionProjectId}/versions/${item.id}/restore`, {
        method: 'POST',
      })
      setArticleAssets((current) => [restored, ...current.filter((asset) => asset.id !== restored.id)])
      setArticleVersionTitle(restored.title)
      setArticleVersions(await request<ArticleVersion[]>(`/api/assets/articles/${articleVersionProjectId}/versions`))
      showToast(`已恢复为 v${restored.version}`)
    } catch (error) {
      showToast(error instanceof Error ? error.message : '版本恢复失败')
    } finally {
      setRestoringVersionId(null)
    }
  }
  const deleteArticleAsset = async (item: ArticleAsset) => {
    if (!await openConfirm({
      title: '删除文章资产',
      message: `删除“${item.title}”？相关版本和图片记录也会删除。`,
      confirmText: '删除',
      tone: 'danger',
    })) return
    try {
      await request<void>(`/api/assets/articles/${item.projectId}`, { method: 'DELETE' })
      setArticleAssets((current) => current.filter((asset) => asset.projectId !== item.projectId))
      setImageAssets((current) => current.filter((asset) => asset.projectId !== item.projectId))
      setSelectedArticleProjectIds((current) => {
        const next = new Set(current)
        next.delete(item.projectId)
        return next
      })
      if (articleVersionProjectId === item.projectId) {
        setArticleVersions(null)
        setArticleVersionProjectId(null)
      }
      showToast('文章资产已删除')
    } catch (error) {
      showToast(error instanceof Error ? error.message : '文章资产删除失败')
    }
  }
  const toggleArticleAssetSelection = (projectId: number) => {
    setSelectedArticleProjectIds((current) => {
      const next = new Set(current)
      if (next.has(projectId)) next.delete(projectId)
      else next.add(projectId)
      return next
    })
  }
  const toggleVisibleArticleAssets = () => {
    setSelectedArticleProjectIds((current) => {
      const next = new Set(current)
      const visibleIds = filteredArticleAssets.map((item) => item.projectId)
      if (visibleIds.length === 0) return next
      if (visibleIds.every((id) => next.has(id))) {
        visibleIds.forEach((id) => next.delete(id))
      } else {
        visibleIds.forEach((id) => next.add(id))
      }
      return next
    })
  }
  const deleteSelectedArticleAssets = async () => {
    const projectIds = Array.from(selectedArticleProjectIds)
    if (projectIds.length === 0) return
    if (!await openConfirm({
      title: '批量删除文章资产',
      message: `删除已选择的 ${projectIds.length} 个文章资产？相关版本和图片记录也会删除。`,
      confirmText: '批量删除',
      tone: 'danger',
    })) return
    try {
      const result = await request<{ deletedCount: number }>('/api/assets/articles/batch-delete', {
        method: 'POST',
        body: JSON.stringify({ projectIds }),
      })
      const deletedIds = new Set(projectIds)
      setArticleAssets((current) => current.filter((asset) => !deletedIds.has(asset.projectId)))
      setImageAssets((current) => current.filter((asset) => !deletedIds.has(asset.projectId)))
      setSelectedArticleProjectIds(new Set())
      if (articleVersionProjectId && deletedIds.has(articleVersionProjectId)) {
        setArticleVersions(null)
        setArticleVersionProjectId(null)
      }
      showToast(`已删除 ${result.deletedCount} 个文章资产`)
    } catch (error) {
      showToast(error instanceof Error ? error.message : '批量删除失败')
      void loadAssets()
    }
  }
  const deleteImageAsset = async (item: ImageAsset) => {
    if (!await openConfirm({
      title: '删除图片资产',
      message: '删除这张图片？删除后不可恢复。',
      confirmText: '删除',
      tone: 'danger',
    })) return
    try {
      await request<void>(`/api/assets/images/${item.id}`, { method: 'DELETE' })
      setImageAssets((current) => current.filter((asset) => asset.id !== item.id))
      setSelectedImageAssetIds((current) => {
        const next = new Set(current)
        next.delete(item.id)
        return next
      })
      showToast('图片资产已删除')
    } catch (error) {
      showToast(error instanceof Error ? error.message : '图片资产删除失败')
    }
  }
  const toggleImageAssetSelection = (id: number) => {
    setSelectedImageAssetIds((current) => {
      const next = new Set(current)
      if (next.has(id)) next.delete(id)
      else next.add(id)
      return next
    })
  }
  const toggleVisibleImageAssets = () => {
    setSelectedImageAssetIds((current) => {
      const next = new Set(current)
      const visibleIds = filteredImageAssets.map((item) => item.id)
      if (visibleIds.length === 0) return next
      if (visibleIds.every((id) => next.has(id))) {
        visibleIds.forEach((id) => next.delete(id))
      } else {
        visibleIds.forEach((id) => next.add(id))
      }
      return next
    })
  }
  const deleteSelectedImageAssets = async () => {
    const ids = Array.from(selectedImageAssetIds)
    if (ids.length === 0) return
    if (!await openConfirm({
      title: '批量删除图片',
      message: `删除已选择的 ${ids.length} 张图片？删除后不可恢复。`,
      confirmText: '批量删除',
      tone: 'danger',
    })) return
    try {
      const result = await request<{ deletedCount: number }>('/api/assets/images/batch-delete', {
        method: 'POST',
        body: JSON.stringify({ imageIds: ids }),
      })
      const deletedIds = new Set(ids)
      setImageAssets((current) => current.filter((asset) => !deletedIds.has(asset.id)))
      setSelectedImageAssetIds(new Set())
      showToast(`已删除 ${result.deletedCount} 张图片`)
    } catch (error) {
      showToast(error instanceof Error ? error.message : '批量删除失败')
      void loadAssets()
    }
  }
  const getAssetImageUrl = (url?: string | null) => {
    if (!url) return ''
    if (/^(https?:|data:|blob:)/i.test(url)) return url
    const base = API_BASE ? new URL(API_BASE, window.location.origin).href : window.location.origin
    return new URL(url, base).href
  }
  const copyText = async (text: string) => {
    if (navigator.clipboard) {
      await navigator.clipboard.writeText(text)
      return true
    }
    const input = document.createElement('textarea')
    input.value = text
    input.setAttribute('readonly', 'true')
    input.style.position = 'fixed'
    input.style.left = '-9999px'
    document.body.appendChild(input)
    input.select()
    const ok = document.execCommand('copy')
    document.body.removeChild(input)
    return ok
  }
  const copyArticleAssetBody = async (item: ArticleAsset) => {
    try {
      const versions = await request<ArticleVersion[]>(`/api/assets/articles/${item.projectId}/versions`)
      const latest = versions[0]
      if (!latest?.body?.trim()) {
        showToast('这篇文章暂无可复制正文')
        return
      }
      const copied = await copyText(cleanArticleContent(latest.body))
      showToast(copied ? '正文已复制' : '当前浏览器不支持直接复制')
    } catch (error) {
      showToast(error instanceof Error ? error.message : '正文复制失败')
    }
  }
  const copyImageUrl = async (item: ImageAsset) => {
    const imageUrl = getAssetImageUrl(item.imageUrl)
    if (!imageUrl) {
      showToast('这张图片还没有可复制的链接')
      return
    }
    const copied = await copyText(imageUrl)
    if (!copied) {
      showToast('当前浏览器不支持直接复制，请打开原图后复制地址')
      return
    }
    showToast('图片链接已复制')
  }
  const copyImageMarkdown = async (item: ImageAsset) => {
    const imageUrl = getAssetImageUrl(item.imageUrl)
    if (!imageUrl) {
      showToast('这张图片还没有可复制的链接')
      return
    }
    const alt = (item.prompt || item.projectTitle || '图片').replace(/[\r\n]+/g, ' ').trim()
    const copied = await copyText(`![${alt}](${imageUrl})`)
    showToast(copied ? 'Markdown 已复制' : '当前浏览器不支持直接复制')
  }
  const copyImagePrompt = async (item: ImageAsset) => {
    if (!item.prompt?.trim()) {
      showToast('这张图片没有提示词')
      return
    }
    const copied = await copyText(item.prompt.trim())
    showToast(copied ? '提示词已复制' : '当前浏览器不支持直接复制')
  }
  const changeJobStatusFilter = (status: string) => {
    setJobStatusFilter(status)
    void loadJobs(status)
  }
  const saveArticleAsset = async (message: Message) => {
    const cid = conversationIdRef.current
    if (!cid) return
    try {
      const articleContent = cleanArticleContent(message.content)
      const title = getCleanDocumentTitle(articleContent)
      const saved = await request<ArticleAsset>('/api/assets/articles/from-message', {
        method: 'POST',
        body: JSON.stringify({ conversationId: cid, messageId: message.id, title, body: articleContent }),
      })
      setArticleAssets((current) => [saved, ...current.filter((item) => item.id !== saved.id)])
      showToast('已保存到资产库')
    } catch (error) {
      showToast(error instanceof Error ? error.message : '保存资产失败')
    }
  }

  const getMessagePlainText = (message: Message) => stripReferenceSections(stripImagePrompts(cleanArticleContent(message.content))).trim()

  const copyMessageContent = async (message: Message) => {
    const text = getMessagePlainText(message)
    if (!text) {
      showToast('这条消息暂无可复制内容')
      return
    }
    const copied = await copyText(text)
    showToast(copied ? '消息已复制' : '当前浏览器不支持直接复制')
  }

  const speakMessageContent = (message: Message) => {
    const text = getMessagePlainText(message)
    if (!text) {
      showToast('这条消息暂无可朗读内容')
      return
    }
    if (!('speechSynthesis' in window)) {
      showToast('当前浏览器不支持朗读')
      return
    }
    window.speechSynthesis.cancel()
    const utterance = new SpeechSynthesisUtterance(text.slice(0, 2000))
    utterance.lang = 'zh-CN'
    window.speechSynthesis.speak(utterance)
    showToast('开始朗读')
  }

  const markMessageFeedback = (message: Message, value: 'up' | 'down') => {
    setMessageFeedback((current) => ({ ...current, [message.id]: value }))
    showToast(value === 'up' ? '已记录：有帮助' : '已记录：需要改进')
  }

  const askFollowupFromMessage = (message: Message) => {
    setOpenMessageMenuId(null)
    setDraft(`请基于上面这条回答继续展开：\n\n${getMessagePlainText(message).slice(0, 800)}\n\n我的追问是：`)
  }

  const reportMessage = (message: Message) => {
    setOpenMessageMenuId(null)
    setMessageFeedback((current) => ({ ...current, [message.id]: 'down' }))
    showToast('已标记反馈，后续会接入后台反馈记录')
  }
  const [copyFeedback, setCopyFeedback] = useState('')
  const [copyTitleFeedback, setCopyTitleFeedback] = useState('')
  const [openMessageMenuId, setOpenMessageMenuId] = useState<number | null>(null)
  const [messageFeedback, setMessageFeedback] = useState<Record<number, 'up' | 'down'>>({})
  const [showShareModal, setShowShareModal] = useState(false)
  const [shareExpiry, setShareExpiry] = useState('24h')
  const [shareUrl, setShareUrl] = useState('')
  const [shareLoading, setShareLoading] = useState(false)
  const copyPreview = async () => {
    if (!previewMessage) return
    const html = richEditorRef.current?.getHTML() ?? ''
    const body = richEditorRef.current?.getMarkdown() ?? editorBody
    const plain = composeDocumentDraft(editorTitle, body, editorLevel)
    try {
      await navigator.clipboard.write([
        new ClipboardItem({
          'text/html': new Blob([html], { type: 'text/html' }),
          'text/plain': new Blob([plain], { type: 'text/plain' }),
        }),
      ])
    } catch {
      await navigator.clipboard.writeText(plain)
    }
    setCopyFeedback('已复制')
    window.setTimeout(() => setCopyFeedback(''), 2500)
  }
  const copyEditorTitle = async () => {
    if (!editorTitle) return
    await navigator.clipboard.writeText(editorTitle)
    setCopyTitleFeedback('已复制')
    window.setTimeout(() => setCopyTitleFeedback(''), 2500)
  }
  const syncPreviewContent = (content: string) => {
    if (!previewMessage) return
    setPreviewDraft(content)
    setPreviewMessage({ ...previewMessage, content })
    setMessages((current) => current.map((item) => item.id === previewMessage.id ? { ...item, content } : item))
  }
  const onRichEditorChange = useCallback((bodyMarkdown: string) => {
    const msg = previewMessageRef.current
    if (!msg) return
    setEditorBody(bodyMarkdown)
    const full = composeDocumentDraft(editorTitle, bodyMarkdown, editorLevel)
    setPreviewDraft(full)
    setPreviewMessage((prev) => prev ? { ...prev, content: full } : prev)
    setMessages((current) => current.map((item) => item.id === msg.id ? { ...item, content: full } : item))
  }, [editorTitle, editorLevel])
  const onEditorTitleChange = (title: string) => {
    if (!previewMessage) return
    setEditorTitle(title)
    const full = composeDocumentDraft(title, editorBody, editorLevel)
    setPreviewDraft(full)
    setPreviewMessage((prev) => prev ? { ...prev, content: full } : prev)
    setMessages((current) => current.map((item) => item.id === previewMessage.id ? { ...item, content: full } : item))
  }
  const savePreviewEdit = async () => {
    if (!previewMessage || !conversationId) return
    const body = richEditorRef.current?.getMarkdown() ?? editorBody
    const content = composeDocumentDraft(editorTitle, body, editorLevel)
    syncPreviewContent(content)
    try {
      await request<{ message: string }>(`/api/conversations/${conversationId}/messages/${previewMessage.id}`, {
        method: 'PUT',
        body: JSON.stringify({ content }),
      })
      showToast('文档已保存')
    } catch (error) {
      showToast(error instanceof Error ? error.message : '文档保存失败')
    }
  }
  const shareArticle = async () => {
    const body = richEditorRef.current?.getMarkdown() ?? editorBody
    const content = composeDocumentDraft(editorTitle, body, editorLevel)
    setShareLoading(true)
    setShareUrl('')
    try {
      const result = await request<{ articleId: number; shareUrl: string; token: string; expiresAt: string | null }>('/api/shares', {
        method: 'POST',
        body: JSON.stringify({ conversationId: conversationIdRef.current, messageId: previewMessageRef.current?.id, title: editorTitle, body: cleanArticleContent(content), expiresIn: shareExpiry }),
      })
      setShareUrl(result.shareUrl)
    } catch (error) {
      showToast(error instanceof Error ? error.message : '分享失败')
    } finally {
      setShareLoading(false)
    }
  }
  const addBlock = (type: AssistantBlockType, data: Partial<AssistantBlock> = {}) => {
    const id = ++assistantIdRef.current
    const block = { id, type, loading: true, ...data }
    setAssistantBlocks((prev) => [block, ...prev])
    return id
  }
  const updateBlock = (id: number, data: Partial<AssistantBlock>) => {
    setAssistantBlocks((prev) => prev.map((b) => b.id === id ? { ...b, ...data, loading: false } : b))
  }
  const removeBlock = (id: number) => {
    setAssistantBlocks((prev) => prev.filter((b) => b.id !== id))
  }
  const insertEditorImage = () => {
    addBlock('image', { loading: false, images: [], prompt: '', style: '纪实摄影', ratio: '1:1' })
  }
  const updateImageBlockField = (blockId: number, field: string, value: string) => {
    setAssistantBlocks((prev) => prev.map((b) => b.id === blockId ? { ...b, [field]: value } : b))
  }
  const streamGeneratedImage = async (title: string, prompt: string, onImage?: (url: string, isPartial: boolean) => void) => {
    let authToken = await ensureFreshToken()
    const init = {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${authToken}`, 'X-Client-User-Agent': navigator.userAgent },
      body: JSON.stringify({ title, prompt }),
    }
    let response = await fetch(`${API_BASE}/api/conversations/generate-image/stream`, init)
    if (response.status === 401) {
      authToken = await tryRefreshToken()
      response = await fetch(`${API_BASE}/api/conversations/generate-image/stream`, {
        ...init,
        headers: { ...init.headers, Authorization: `Bearer ${authToken}` },
      })
    }

    if (!response.ok || !response.body) {
      return null
    }

    let imageUrl: string | null = null
    await readServerSentEvents(response, (event) => {
      if (event.event !== 'image') return
      const data = event.data as { url?: string; error?: string; isPartial?: boolean }
      if (data.url) {
        imageUrl = data.url
        onImage?.(data.url, Boolean(data.isPartial))
      }
    })

    return imageUrl
  }
  const generateBlockImage = async (blockId: number, overrides?: { prompt?: string; style?: string; ratio?: string }) => {
    let promptText: string | undefined
    let style: string
    let ratio: string
    if (overrides) {
      promptText = overrides.prompt?.trim()
      style = overrides.style ?? '纪实摄影'
      ratio = overrides.ratio ?? '1:1'
    } else {
      const block = assistantBlocksRef.current.find((b) => b.id === blockId)
      if (!block) return
      promptText = block.prompt?.trim()
      style = block.style ?? '纪实摄影'
      ratio = block.ratio ?? '1:1'
    }
    const selected = richEditorRef.current?.getSelectedText()?.trim()
    const styleHint = `风格要求：${style}。`
    const ratioHint = `图片比例：${ratio}。`
    const summary = editorBody.replace(/[#*_`>-]/g, ' ').replace(/\s+/g, ' ').trim().slice(0, 300)
    const contextText = selected || promptText || summary
    const basePrompt = selected
      ? `为以下段落内容生成配图。${styleHint}${ratioHint}要求画面与内容强相关，不要出现可读文字、水印、品牌 Logo，画面清晰、真实、有信息感。\n\n段落内容：${selected.slice(0, 500)}`
      : promptText
        ? `${promptText}。${styleHint}${ratioHint}要求画面清晰、真实、有信息感，不要出现可读文字、水印、品牌 Logo。`
        : `为这篇中文图文文章生成配图。标题：${editorTitle || '配图'}\n内容摘要：${summary}\n${styleHint}${ratioHint}要求画面与文章主题强相关，不要出现可读文字、水印、品牌 Logo，画面清晰、真实、有信息感。`
    setAssistantBlocks((prev) => prev.map((b) => b.id === blockId ? { ...b, loading: true, text: contextText.slice(0, 200), images: [], error: undefined } : b))
    setEditorBusy(true)
    try {
      const updateImageSlot = (index: number, url: string) => {
        setAssistantBlocks((prev) => prev.map((b) => {
          if (b.id !== blockId) return b
          const nextImages = [...(b.images || [])]
          nextImages[index] = url
          return { ...b, images: nextImages }
        }))
      }
      const promises = Array.from({ length: 2 }, (_, i) =>
        streamGeneratedImage(`配图 ${i + 1}`, basePrompt, (url) => updateImageSlot(i, url)).catch(() => null)
      )
      const urls = await Promise.all(promises)
      urls.forEach((url, index) => { if (url) updateImageSlot(index, url) })
      setAssistantBlocks((prev) => prev.map((b) => b.id === blockId ? { ...b, loading: false } : b))
    } catch {
      setAssistantBlocks((prev) => prev.map((b) => b.id === blockId ? { ...b, loading: false, error: '图片生成失败' } : b))
    } finally {
      setEditorBusy(false)
    }
  }
  const generateTitles = async () => {
    const blockId = addBlock('title')
    setEditorBusy(true)
    try {
      const result = await request<{ content: string }>('/api/conversations/edit-document', {
        method: 'POST',
        body: JSON.stringify({
          content: composeDocumentDraft(editorTitle, richEditorRef.current?.getMarkdown() ?? editorBody, editorLevel),
          instruction: '根据文章内容生成 3 个更吸引人的标题，每行一个标题，不要编号不要引号不要其他内容，不要修改正文。',
          selection: '',
        }),
      })
      const titles = result.content.split('\n').map((s: string) => s.replace(/^\d+[.、)\s]+/, '').replace(/^["'"']+|["'"']+$/g, '').trim()).filter(Boolean)
      updateBlock(blockId, { items: titles })
    } catch (error) {
      updateBlock(blockId, { error: error instanceof Error ? error.message : '生成失败' })
    } finally {
      setEditorBusy(false)
    }
  }
  const adoptTitle = (title: string) => {
    setEditorTitle(title)
    const full = composeDocumentDraft(title, editorBody, editorLevel)
    setPreviewDraft(full)
    setPreviewMessage((prev) => prev ? { ...prev, content: full } : prev)
    setMessages((current) => current.map((item) => item.id === previewMessage?.id ? { ...item, content: full } : item))
    setImageEditStatus('标题已采纳')
  }
  const extractLead = async () => {
    const blockId = addBlock('lead')
    setEditorBusy(true)
    try {
      const result = await request<{ content: string }>('/api/conversations/edit-document', {
        method: 'POST',
        body: JSON.stringify({
          content: composeDocumentDraft(editorTitle, richEditorRef.current?.getMarkdown() ?? editorBody, editorLevel),
          instruction: '从文章内容中提取 3 段不同角度的精炼导语（每段 80-150 字），用于文章摘要。用空行分隔每段导语，不要编号不要标题不要其他内容。',
          selection: '',
        }),
      })
      const leads = result.content.split(/\n{2,}/).map((s: string) => s.trim()).filter((s: string) => s.length > 20)
      updateBlock(blockId, { items: leads })
    } catch (error) {
      updateBlock(blockId, { error: error instanceof Error ? error.message : '提取失败' })
    } finally {
      setEditorBusy(false)
    }
  }
  const adoptLead = (text: string) => {
    const lead = `> ${text.replace(/\n/g, '\n> ')}\n\n`
    const currentBody = richEditorRef.current?.getMarkdown() ?? editorBody
    const newBody = lead + currentBody
    richEditorRef.current?.setMarkdown(newBody)
    setEditorBody(newBody)
    const full = composeDocumentDraft(editorTitle, newBody, editorLevel)
    syncPreviewContent(full)
    setImageEditStatus('导语已插入文章开头')
  }
  const adoptImage = (url: string, blockText?: string) => {
    richEditorRef.current?.insertImage(url, blockText || '配图', '')
    setImageEditStatus('图片已插入正文')
  }
  const detectContent = async () => {
    const blockId = addBlock('detect')
    setEditorBusy(true)
    try {
      const currentBody = richEditorRef.current?.getMarkdown() ?? editorBody
      const result = await request<{ content: string }>('/api/conversations/edit-document', {
        method: 'POST',
        body: JSON.stringify({
          content: composeDocumentDraft(editorTitle, currentBody, editorLevel),
          instruction: `检查全文是否存在错别字、语病、事实前后不一致的问题。以 JSON 数组格式返回，每条建议格式为：{"original":"原文片段","fixed":"修正后片段","reason":"修改原因"}。只返回 JSON 数组，不要其他内容。如果没有问题返回空数组 []。`,
          selection: '',
        }),
      })
      const jsonMatch = result.content.match(/\[[\s\S]*\]/)
      if (!jsonMatch) {
        updateBlock(blockId, { text: '未发现需要优化的内容' })
      } else {
        const suggestions = JSON.parse(jsonMatch[0]) as { original: string; fixed: string; reason: string }[]
        updateBlock(blockId, { items: suggestions.map((s) => JSON.stringify(s)) })
      }
    } catch (error) {
      updateBlock(blockId, { error: error instanceof Error ? error.message : '检测失败' })
    } finally {
      setEditorBusy(false)
    }
  }
  const adoptSuggestion = (blockId: number, itemJson: string) => {
    try {
      const { original, fixed } = JSON.parse(itemJson) as { original: string; fixed: string }
      const currentBody = richEditorRef.current?.getMarkdown() ?? editorBody
      let nextBody: string | null = null
      if (currentBody.includes(original)) {
        nextBody = currentBody.replace(original, fixed)
      } else {
        const escaped = original.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')
        const fuzzy = escaped.replace(/\s+/g, '\\s*')
        const re = new RegExp(fuzzy)
        if (re.test(currentBody)) nextBody = currentBody.replace(re, fixed)
      }
      if (!nextBody) { setImageEditStatus('未找到匹配的原文'); return }
      setEditorBody(nextBody)
      richEditorRef.current?.setMarkdown(nextBody)
      const full = composeDocumentDraft(editorTitle, nextBody, editorLevel)
      setPreviewDraft(full)
      setPreviewMessage((prev) => prev ? { ...prev, content: full } : prev)
      setMessages((current) => current.map((item) => item.id === previewMessage?.id ? { ...item, content: full } : item))
      setAssistantBlocks((prev) => prev.map((b) => b.id === blockId ? { ...b, items: b.items?.filter((i) => i !== itemJson) } : b))
      setImageEditStatus('已采纳')
    } catch { setImageEditStatus('采纳失败') }
  }
  const adoptAllSuggestions = (blockId: number) => {
    const block = assistantBlocks.find((b) => b.id === blockId)
    if (!block?.items) return
    let currentBody = richEditorRef.current?.getMarkdown() ?? editorBody
    for (const itemJson of block.items) {
      try {
        const { original, fixed } = JSON.parse(itemJson) as { original: string; fixed: string }
        if (currentBody.includes(original)) { currentBody = currentBody.replace(original, fixed) }
        else {
          const escaped = original.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')
          currentBody = currentBody.replace(new RegExp(escaped.replace(/\s+/g, '\\s*')), fixed)
        }
      } catch { /* skip */ }
    }
    setEditorBody(currentBody)
    richEditorRef.current?.setMarkdown(currentBody)
    const full = composeDocumentDraft(editorTitle, currentBody, editorLevel)
    setPreviewDraft(full)
    setPreviewMessage((prev) => prev ? { ...prev, content: full } : prev)
    setMessages((current) => current.map((item) => item.id === previewMessage?.id ? { ...item, content: full } : item))
    removeBlock(blockId)
    setImageEditStatus('已全部采纳')
  }
  const generateLayout = async () => {
    const blockId = addBlock('layout')
    setEditorBusy(true)
    try {
      const currentBody = richEditorRef.current?.getMarkdown() ?? editorBody
      const result = await request<{ content: string }>('/api/conversations/edit-document', {
        method: 'POST',
        body: JSON.stringify({
          content: composeDocumentDraft(editorTitle, currentBody, editorLevel),
          instruction: '在不改变核心事实和图片位置的前提下，自动优化 Markdown 排版，修正标题层级、段落间距、列表和图片说明。',
          selection: '',
        }),
      })
      updateBlock(blockId, { text: result.content })
    } catch (error) {
      updateBlock(blockId, { error: error instanceof Error ? error.message : '排版失败' })
    } finally {
      setEditorBusy(false)
    }
  }
  const adoptLayout = (blockId: number) => {
    const block = assistantBlocks.find((b) => b.id === blockId)
    if (!block?.text) return
    const { title, body, level } = splitDocumentDraft(block.text)
    setEditorTitle(title)
    setEditorBody(body)
    setEditorLevel(level)
    syncPreviewContent(block.text)
    richEditorRef.current?.setMarkdown(body)
    removeBlock(blockId)
    setImageEditStatus('排版已采纳')
  }
  const cleanMarkdown = () => {
    const currentBody = richEditorRef.current?.getMarkdown() ?? editorBody
    const cleanBody = currentBody
      .replace(/\n{3,}/g, '\n\n')
      .replace(/[ \t]+\n/g, '\n')
      .replace(/^(#{1,6})([^\s#])/gm, '$1 $2')
      .trim()
    setEditorBody(cleanBody)
    const full = composeDocumentDraft(editorTitle, cleanBody, editorLevel)
    syncPreviewContent(full)
    richEditorRef.current?.setMarkdown(cleanBody)
    setImageEditStatus('格式已整理，记得保存文档')
  }
  const openEditor = (message: Message) => {
    savedScrollRef.current = messagesRef.current?.scrollTop ?? null
    const content = cleanArticleContent(message.content)
    const { title, body, level } = splitDocumentDraft(content)
    setPreviewMessage({ ...message, content })
    setPreviewDraft(content)
    setEditorTitle(title)
    setEditorBody(body)
    setEditorLevel(level)
    setShowPreviewModal(false)
    setActivePage('editor')
  }
  const closeEditor = () => {
    setActivePage('chat')
    const scrollTo = savedScrollRef.current
    if (scrollTo !== null) {
      window.requestAnimationFrame(() => {
        if (messagesRef.current) messagesRef.current.scrollTop = scrollTo
      })
      savedScrollRef.current = null
    }
  }
  const applyMarkdownTool = (tool: 'heading' | 'bold' | 'italic' | 'quote' | 'list' | 'divider' | 'code' | 'link' | 'table') => {
    const ed = richEditorRef.current
    if (!ed) return

    switch (tool) {
      case 'heading': ed.toggleHeading(2); break
      case 'bold': ed.toggleBold(); break
      case 'italic': ed.toggleItalic(); break
      case 'quote': ed.toggleBlockquote(); break
      case 'list': ed.toggleBulletList(); break
      case 'divider': ed.setHorizontalRule(); break
      case 'code': ed.toggleCodeBlock(); break
      case 'link': ed.insertLink('https://'); break
      case 'table': ed.insertTable(3, 3); break
    }
  }
  const downloadPreviewImage = async (src: string, alt?: string) => {
    const imageUrl = new URL(src, window.location.href).href
    const safeName = (alt || 'veramedia-image').replace(/[\\/:*?"<>|]+/g, '-').slice(0, 40)
    try {
      const response = await fetch(imageUrl)
      if (!response.ok) throw new Error(response.statusText)
      const blob = await response.blob()
      const objectUrl = URL.createObjectURL(blob)
      const link = document.createElement('a')
      link.href = objectUrl
      link.download = `${safeName || 'veramedia-image'}.${blob.type.includes('jpeg') ? 'jpg' : 'png'}`
      document.body.appendChild(link)
      link.click()
      link.remove()
      URL.revokeObjectURL(objectUrl)
    } catch {
      window.open(imageUrl, '_blank', 'noopener,noreferrer')
    }
  }
  const retryPreviewImage = async (quoteText: string) => {
    if (!previewMessage) return
    const title = quoteText.split('生成失败')[0].replace(/[>：:]/g, '').trim() || '正文配图'
    try {
      let authToken = await ensureFreshToken()
      const init = {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${authToken}`, 'X-Client-User-Agent': navigator.userAgent },
        body: JSON.stringify({ title, articleMarkdown: previewMessage.content }),
      }
      let response = await fetch(`${API_BASE}/api/conversations/retry-image/stream`, init)
      if (response.status === 401) {
        authToken = await tryRefreshToken()
        response = await fetch(`${API_BASE}/api/conversations/retry-image/stream`, {
          ...init,
          headers: { ...init.headers, Authorization: `Bearer ${authToken}` },
        })
      }

      if (!response.ok || !response.body) throw new Error('图片重试失败')
      const imageEvents: { title: string; url?: string; error?: string; isPartial?: boolean }[] = []
      await readServerSentEvents(response, (event) => {
        if (event.event !== 'image') return
        const data = event.data as { title: string; url?: string; error?: string; isPartial?: boolean }
        if (data.url || data.error) imageEvents.push(data)
      })

      const result = imageEvents.at(-1)
      if (!result?.url) throw new Error(result?.error || '图片生成失败')
      const nextContent = previewMessage.content.replace(quoteText, `![${result.title}](${result.url})`)
      setPreviewMessage({ ...previewMessage, content: nextContent })
      setMessages((current) => current.map((item) => item.id === previewMessage.id ? { ...item, content: nextContent } : item))
    } catch (error) {
      showToast(error instanceof Error ? error.message : '图片重试失败')
    }
  }
  const markdownPreviewComponents = {
    img: ({ src, alt }: { src?: string; alt?: string }) => (
      <span className="preview-image-wrap">
        <img
          src={src}
          alt={alt ?? ''}
          title="右键下载图片"
          onContextMenu={(event) => {
            event.preventDefault()
            if (src) downloadPreviewImage(src, alt)
          }}
        />
        {src && (
          <button type="button" onClick={() => downloadPreviewImage(src, alt)}>
            下载图片
          </button>
        )}
      </span>
    ),
    blockquote: ({ children }: { children?: ReactNode }) => {
      const text = String(children?.valueOf?.() ?? '')
      const failed = text.includes('生成失败')
      return (
        <blockquote>
          {children}
          {failed && <button className="inline-retry" type="button" onClick={() => retryPreviewImage(text)}>重试生成图片</button>}
        </blockquote>
      )
    },
  }
  const chatMarkdownComponents = {
    img: ({ src, alt }: { src?: string; alt?: string }) => {
      if (!src) return null
      return (
        <button className="chat-image-thumb" type="button" onClick={() => setLightboxImage({ src, alt })}>
          <img src={src} alt={alt ?? ''} />
          <span>点击放大</span>
        </button>
      )
    },
  }
  const renderReferencePanel = (referenceContent: string) => {
    const content = referenceContent.trim()
    if (!content) return null
    const sources = parseReferenceSources(content)

    return (
      <details className="article-reference-panel">
        <summary>
          <span>参考资料</span>
          {sources.length > 0 && <small>{sources.length} 条</small>}
        </summary>
        {sources.length > 0 ? (
          <div className="reference-source-list">
            {sources.map((source, index) => (
              <a className="reference-source-item" href={source.url} target="_blank" rel="noreferrer" key={source.url}>
                <span>{index + 1}</span>
                <strong>{source.title}</strong>
                <em>{source.host}</em>
                <Globe2 size={14} />
              </a>
            ))}
          </div>
        ) : (
          <div className="markdown-body">
            <ReactMarkdown remarkPlugins={[remarkGfm]} components={chatMarkdownComponents}>{content}</ReactMarkdown>
          </div>
        )}
      </details>
    )
  }

  const renderTextMessage = (message: Message) => {
    const displayContent = stripReferenceSections(stripImagePrompts(message.content)).trim()
    const referenceContent = extractReferenceSection(message.content)

    return (
      <>
        <div className="message-body markdown-body">
          {isGeneratingImageMessage(message) ? (
            <div className="chat-image-generating">
              <div className="chat-image-shimmer">
                <ImagePlus size={32} />
              </div>
              <div>
                <strong>{getGeneratingImageDisplayTitle(message)}</strong>
                <span>
                  图片生成中
                  <i />
                  <i />
                  <i />
                </span>
              </div>
            </div>
          ) : displayContent ? (
            <ReactMarkdown remarkPlugins={[remarkGfm]} components={chatMarkdownComponents}>{displayContent}</ReactMarkdown>
          ) : isMessageGenerating(message) ? (
            <span className="typing-dots">
              <i />
              <i />
              <i />
            </span>
          ) : referenceContent ? null : (
            <span className="empty-message">生成结果正在同步，请稍候。</span>
          )}
        </div>
        {message.role === 'assistant' && !isGeneratingImageMessage(message) && !isMessageGenerating(message) && renderReferencePanel(referenceContent)}
      </>
    )
  }

  const renderMessageActions = (message: Message) => {
    if (isMessageGenerating(message)) return null
    const isAssistant = message.role === 'assistant'
    const feedback = messageFeedback[message.id]

    return (
      <div className="message-action-row" aria-label="消息操作">
        <button type="button" title="复制" onClick={() => void copyMessageContent(message)}>
          <Copy size={16} />
        </button>
        {isAssistant && (
          <>
            <button type="button" title="朗读" onClick={() => speakMessageContent(message)}>
              <Volume2 size={16} />
            </button>
            <button className={feedback === 'up' ? 'active' : ''} type="button" title="有帮助" onClick={() => markMessageFeedback(message, 'up')}>
              <ThumbsUp size={16} />
            </button>
            <button className={feedback === 'down' ? 'active' : ''} type="button" title="需要改进" onClick={() => markMessageFeedback(message, 'down')}>
              <ThumbsDown size={16} />
            </button>
          </>
        )}
        <button type="button" title={isAssistant ? '追问' : '重新生成'} onClick={() => isAssistant ? askFollowupFromMessage(message) : void retryFromMessage(message)} disabled={!isAssistant && isStreaming}>
          {isAssistant ? <CornerUpRight size={16} /> : <RotateCcw size={16} />}
        </button>
        {isAssistant && (
          <div className="message-more-wrap">
            <button className={openMessageMenuId === message.id ? 'active' : ''} type="button" title="更多" onClick={() => setOpenMessageMenuId((current) => current === message.id ? null : message.id)}>
              <MoreHorizontal size={16} />
            </button>
            {openMessageMenuId === message.id && (
              <div className="message-more-menu">
                <button type="button" onClick={() => { setOpenMessageMenuId(null); openEditor(message) }}>
                  <PanelRightOpen size={16} />
                  转为文档编辑
                </button>
                <button type="button" onClick={() => { setOpenMessageMenuId(null); void saveArticleAsset(message) }}>
                  <Bookmark size={16} />
                  保存到资产库
                </button>
                <button type="button" onClick={() => { setOpenMessageMenuId(null); void downloadMessageExport(message, 'docx') }}>
                  <FileText size={16} />
                  导出 DOCX
                </button>
                <button type="button" onClick={() => { setOpenMessageMenuId(null); void downloadMessageExport(message, 'pptx') }}>
                  <Presentation size={16} />
                  导出 PPT
                </button>
                <button type="button" onClick={() => reportMessage(message)}>
                  <Flag size={16} />
                  反馈与举报
                </button>
              </div>
            )}
          </div>
        )}
      </div>
    )
  }

  if (!token || !user) {
    return (
      <main className="auth-shell">
        <section className="auth-panel">
          <div className="brand-mark"><Bot size={28} /></div>
          <h1>内容运营助手</h1>
          <p>用对话完成选题、成文、配图和内容修改。</p>
          <form onSubmit={submitAuth} className="auth-form" autoComplete="off">
            <label>邮箱<input value={email} onChange={(e) => setEmail(e.target.value)} type="email" autoComplete="off" /></label>
            {!resetMode && <label>密码<input value={password} onChange={(e) => setPassword(e.target.value)} type="password" autoComplete={authMode === 'login' ? 'current-password' : 'new-password'} /></label>}
            {authMode === 'register' && <label>昵称<input value={displayName} onChange={(e) => setDisplayName(e.target.value)} autoComplete="off" /></label>}
            {((authMode === 'register' && authConfig.requireEmailCode) || resetMode) && (
              <label>
                邮箱验证码
                <div className="inline-field">
                  <input value={emailCode} onChange={(e) => setEmailCode(e.target.value)} placeholder="请输入邮箱验证码" autoComplete="one-time-code" />
                  <button className="ghost-button code-button" type="button" onClick={sendEmailCode} disabled={emailCodeSending || emailCodeCountdown > 0}>
                    {emailCodeSending ? '发送中...' : emailCodeCountdown > 0 ? `${emailCodeCountdown} 秒后重发` : '获取验证码'}
                  </button>
                </div>
              </label>
            )}
            {resetMode && <label>新密码<input value={newPassword} onChange={(e) => setNewPassword(e.target.value)} type="password" autoComplete="new-password" /></label>}
            {authError && <div className="error">{authError}</div>}
            {resetMode && <button className="primary-button" type="button" onClick={resetPassword}>重置密码</button>}
            {!resetMode && <button className="primary-button" type="submit">
              {authMode === 'login' ? <KeyRound size={18} /> : <UserPlus size={18} />}
              {authMode === 'login' ? '登录' : '注册'}
            </button>}
            <div className="auth-links">
              {authMode === 'login' && <button type="button" onClick={() => { setResetMode(!resetMode); setAuthError('') }}>{resetMode ? '返回登录' : '忘记密码'}</button>}
              {!resetMode && <button type="button" onClick={() => { setAuthMode(authMode === 'login' ? 'register' : 'login'); setAuthError(''); setEmailCode('') }}>
                {authMode === 'login' ? '创建新账号' : '已有账号，去登录'}
              </button>}
            </div>
          </form>
        </section>
      </main>
    )
  }

  return (
    <>
    <main className={isMobileNavOpen ? 'workspace mobile-nav-open' : 'workspace'}>
      <aside className="sidebar">
        <div className="sidebar-header">
          <div className="brand-chip">
            <span className="brand-logo-mark" aria-hidden="true">
              <span />
            </span>
            <strong>内容运营助手</strong>
          </div>
          <div className="sidebar-actions">
            <button title="设置" onClick={openSettings}><Settings size={18} /></button>
            <button title="退出登录" onClick={logout}><LogOut size={18} /></button>
          </div>
        </div>

        <button className={activePage === 'chat' ? 'new-chat active' : 'new-chat'} onClick={() => { goToChat(); setConversationId(null); setMessages([]) }}>
          <MessageSquarePlus size={18} />
          新内容任务
        </button>
        <button className={activePage === 'tasks' ? 'new-chat active' : 'new-chat'} onClick={openTaskCenter}>
          <ListRestart size={18} />
          任务中心
        </button>
        <button className={activePage === 'assets' ? 'new-chat active' : 'new-chat'} onClick={openAssetLibrary}>
          <FileText size={18} />
          文章资产
        </button>
        <button className={activePage === 'images' ? 'new-chat active' : 'new-chat'} onClick={openImageLibrary}>
          <ImagePlus size={18} />
          图片资产
        </button>
        <button className="new-chat" type="button" onClick={openSettings}>
          <Settings size={18} />
          模型与账号
        </button>

        <div className="sidebar-section-title">历史对话</div>
        <div className="conversation-list">
          {conversations.map((item) => (
            <div className={item.id === conversationId ? 'conversation-item active' : 'conversation-item'} key={item.id}>
              <button className="conversation-open" type="button" onClick={() => void openConversationFromSidebar(item.id)}>
                {item.title}
              </button>
              <button className="conversation-delete" type="button" title="删除会话" onClick={() => deleteConversation(item)}>
                <Trash2 size={14} />
              </button>
            </div>
          ))}
        </div>
        <button className="sidebar-user" type="button" onClick={openSettings}>
          <span className="brand-avatar small">{(user.displayName || user.email || '你').slice(0, 1)}</span>
          <span>{user.displayName || user.email || 'SunnyFan'}</span>
        </button>
      </aside>
      <button className="mobile-nav-backdrop" type="button" aria-label="关闭会话列表" onClick={() => setIsMobileNavOpen(false)} />

      <section className={activePage === 'editor' ? 'chat-pane editor-active' : 'chat-pane'}>
        {activePage === 'editor' && previewMessage ? (
          <div className="editor-page">
            <header className="editor-page-header">
              <button className="mobile-menu-button" type="button" aria-label="打开会话列表" onClick={() => setIsMobileNavOpen(true)}>
                <Menu size={18} />
              </button>
              <button className="editor-back" type="button" onClick={closeEditor}>
                <ArrowLeft size={18} />
                返回对话
              </button>
              <div className="editor-page-title">
                <span>写文章</span>
              </div>
              <div className="preview-tools">
                <button type="button" onClick={() => setShowPreviewModal(true)}><Eye size={16} />预览</button>
                <button type="button" onClick={savePreviewEdit}><Save size={16} />保存</button>
              </div>
            </header>
              <>
                <div className="editor-left">
                  <div className="editor-topbar" aria-label="文档编辑工具栏">
                    <div className="editor-tool-group compact">
                      <button type="button" title="撤销" onClick={() => richEditorRef.current?.undo()}><Undo2 size={18} /><span>撤销</span></button>
                      <button type="button" title="重做" onClick={() => richEditorRef.current?.redo()}><Redo2 size={18} /><span>重做</span></button>
                      <button type="button" title="清除格式" onClick={cleanMarkdown}><Eraser size={18} /><span>清格式</span></button>
                      <button type="button" title="标题" onClick={() => applyMarkdownTool('heading')}><Heading2 size={18} /><span>标题</span></button>
                      <button type="button" title="加粗" onClick={() => applyMarkdownTool('bold')}><Bold size={18} /><span>加粗</span></button>
                      <button type="button" title="斜体" onClick={() => applyMarkdownTool('italic')}><Italic size={18} /><span>斜体</span></button>
                      <button type="button" title="列表" onClick={() => applyMarkdownTool('list')}><List size={18} /><span>列表</span></button>
                      <button type="button" title="引用" onClick={() => applyMarkdownTool('quote')}><Quote size={18} /><span>引用</span></button>
                      <button type="button" title="分割线" onClick={() => applyMarkdownTool('divider')}><ListRestart size={18} /><span>分割线</span></button>
                      <button type="button" title="代码" onClick={() => applyMarkdownTool('code')}><Braces size={18} /><span>代码</span></button>
                      <button type="button" title="链接" onClick={() => applyMarkdownTool('link')}><LinkIcon size={18} /><span>链接</span></button>
                      <button type="button" title="表格" onClick={() => applyMarkdownTool('table')}><TableIcon size={18} /><span>表格</span></button>
                      <label className="editor-tool-upload" title="上传本地图片">
                        <ImagePlus size={18} /><span>图片</span>
                        <input ref={editorImageInputRef} type="file" accept="image/*" multiple onChange={(e) => uploadEditorImage(e.target.files)} />
                      </label>
                    </div>
                  </div>
                  <section className="editor-main">
                    <input
                      className="editor-title-input"
                      value={editorTitle}
                      placeholder="请输入标题（最多 100 个字）"
                      maxLength={100}
                      onChange={(event) => onEditorTitleChange(event.target.value)}
                    />
                    <RichEditor
                      ref={richEditorRef}
                      content={editorBody}
                      onChange={onRichEditorChange}
                    />
                  </section>
                  <div className="editor-statusbar">
                    <div>
                      <span>字数：{getContentTextLength(editorBody)}</span>
                    </div>
                    <div>
                      <span>{imageEditStatus || '草稿已就绪'}</span>
                      <button className="ghost-button" type="button" onClick={() => setShowPreviewModal(true)}>预览</button>
                      <button className="primary-button" type="button" onClick={savePreviewEdit}>保存</button>
                    </div>
                  </div>
                </div>
                <aside className="image-editor-panel">
                  <div className="assistant-top">
                    <div className="assistant-welcome">
                      <strong>创作助手</strong>
                      {assistantBlocks.length > 0 && <button className="assistant-clear" type="button" onClick={() => { setAssistantBlocks([]); setImageEditStatus('') }}><Trash2 size={14} /></button>}
                    </div>
                    <div className="assistant-cards">
                      <button type="button" disabled={editorBusy} onClick={detectContent}>
                        <span className="assistant-icon icon-blue"><Braces size={15} /></span><span>内容检测</span>
                      </button>
                      <button type="button" disabled={editorBusy} onClick={generateLayout}>
                        <span className="assistant-icon icon-green"><List size={15} /></span><span>智能排版</span>
                      </button>
                      <button type="button" disabled={editorBusy} onClick={generateTitles}>
                        <span className="assistant-icon icon-pink"><Heading2 size={15} /></span><span>智能标题</span>
                      </button>
                      <button type="button" disabled={editorBusy} onClick={extractLead}>
                        <span className="assistant-icon icon-orange"><Quote size={15} /></span><span>提取导语</span>
                      </button>
                      <button type="button" onClick={insertEditorImage}>
                        <span className="assistant-icon icon-purple"><ImagePlus size={15} /></span><span>AI 配图</span>
                      </button>
                    </div>
                  </div>

                  <div className="assistant-scroll">
                    {assistantBlocks.map((block) => (
                      <div className="assistant-block" key={block.id}>
                        <div className="block-header">
                          <strong>{assistantBlockLabels[block.type]}</strong>
                          <button className="block-close" type="button" onClick={() => removeBlock(block.id)}><X size={12} /></button>
                        </div>

                        {block.loading && <p className="result-loading">{assistantBlockLoadingText[block.type]}</p>}
                        {block.error && <p className="result-error">{block.error}</p>}

                        {block.type === 'detect' && !block.loading && block.text && <p className="result-empty">{block.text}</p>}
                        {block.type === 'detect' && !block.loading && block.items && block.items.length > 0 && (
                          <>
                            <div className="result-summary">
                              <span>发现 <strong>{block.items.length}</strong> 条建议</span>
                              <button className="adopt-all-button" type="button" onClick={() => adoptAllSuggestions(block.id)}>全部采纳</button>
                            </div>
                            <div className="result-items">
                              {block.items.map((itemJson, i) => {
                                try {
                                  const s = JSON.parse(itemJson) as { original: string; fixed: string; reason: string }
                                  return (
                                    <div className="detect-item" key={i} onClick={() => richEditorRef.current?.searchAndHighlight(s.original)}>
                                      <div className="detect-content">
                                        <span className="detect-original">{s.original}</span>
                                        <span className="detect-arrow">→</span>
                                        <span className="detect-fixed">{s.fixed}</span>
                                      </div>
                                      {s.reason && <p className="detect-reason">{s.reason}</p>}
                                      <button className="adopt-button" type="button" onClick={(e) => { e.stopPropagation(); adoptSuggestion(block.id, itemJson) }}>采纳</button>
                                    </div>
                                  )
                                } catch { return null }
                              })}
                            </div>
                          </>
                        )}

                        {block.type === 'layout' && !block.loading && block.text && (
                          <>
                            <div className="layout-preview">
                              <ReactMarkdown remarkPlugins={[remarkGfm]}>{stripImagePrompts(block.text)}</ReactMarkdown>
                            </div>
                            <div className="result-action-row">
                              <button className="ghost-button" type="button" onClick={() => removeBlock(block.id)}>放弃</button>
                              <button className="ghost-button" type="button" onClick={() => setLayoutPreviewText(block.text!)}>预览</button>
                              <button className="primary-button" type="button" onClick={() => adoptLayout(block.id)}>采纳排版</button>
                            </div>
                          </>
                        )}

                        {block.type === 'title' && !block.loading && block.items && (
                          <div className="result-items">
                            {block.items.map((t, i) => (
                              <div className="result-item" key={i}>
                                <span>{t}</span>
                                <button className="adopt-button" type="button" onClick={() => adoptTitle(t)}>采纳</button>
                              </div>
                            ))}
                          </div>
                        )}

                        {block.type === 'lead' && !block.loading && block.items && (
                          <div className="result-items">
                            {block.items.map((l, i) => (
                              <div className="result-item" key={i}>
                                <span>{l}</span>
                                <button className="adopt-button" type="button" onClick={() => adoptLead(l)}>采纳</button>
                              </div>
                            ))}
                          </div>
                        )}

                        {block.type === 'image' && !block.loading && (!block.images || block.images.length === 0) && !block.error && (
                          <div className="image-prompt-area">
                            <div className="image-prompt-card">
                              <textarea
                                value={block.prompt ?? ''}
                                onChange={(e) => updateImageBlockField(block.id, 'prompt', e.target.value)}
                                placeholder="请描述你想要配图的内容"
                                rows={2}
                              />
                              <div className="image-prompt-options">
                                <select value={block.style ?? '纪实摄影'} onChange={(e) => updateImageBlockField(block.id, 'style', e.target.value)}>
                                  <option>纪实摄影</option>
                                  <option>插画风格</option>
                                  <option>扁平设计</option>
                                  <option>水彩手绘</option>
                                  <option>3D 渲染</option>
                                  <option>赛博朋克</option>
                                </select>
                                <select value={block.ratio ?? '1:1'} onChange={(e) => updateImageBlockField(block.id, 'ratio', e.target.value)}>
                                  <option>1:1</option>
                                  <option>4:3</option>
                                  <option>16:9</option>
                                  <option>3:4</option>
                                  <option>9:16</option>
                                </select>
                                <button className="primary-button" type="button" disabled={editorBusy} onClick={() => generateBlockImage(block.id)}>生成图片</button>
                              </div>
                            </div>
                          </div>
                        )}
                        {block.type === 'image' && block.images?.some(Boolean) && (
                          <>
                            <div className="result-image-grid">
                              {block.images.filter(Boolean).map((url, i) => (
                                <div className="result-image-item" key={i}>
                                  <img src={url} alt={`配图 ${i + 1}`} />
                                  <button className="image-insert-button" type="button" onClick={() => adoptImage(url, block.text)}>+ 插入正文</button>
                                </div>
                              ))}
                            </div>
                            {!block.loading && <div className="result-action-row">
                              <button className="ghost-button" type="button" disabled={editorBusy} onClick={() => {
                                const params = { prompt: block.prompt ?? '', style: block.style ?? '纪实摄影', ratio: block.ratio ?? '1:1' }
                                const newId = addBlock('image', { loading: true, images: [], ...params })
                                generateBlockImage(newId, params)
                              }}>重新生成</button>
                            </div>}
                          </>
                        )}
                        {block.type === 'image' && block.loading && !block.images?.some(Boolean) && (
                          <div className="result-image-grid">
                            <div className="result-image-item placeholder"><div className="image-placeholder" /></div>
                            <div className="result-image-item placeholder"><div className="image-placeholder" /></div>
                          </div>
                        )}
                      </div>
                    ))}
                    {imageEditStatus && <p className="hint">{imageEditStatus}</p>}
                  </div>
                </aside>
              </>
            {showPreviewModal && (
              <div className="modal-backdrop" onClick={() => setShowPreviewModal(false)}>
                <div className="modal-card preview-modal" onClick={(e) => e.stopPropagation()}>
                  <header className="preview-modal-header">
                    <span>文章预览</span>
                    <div className="preview-modal-actions">
                      <button className={copyTitleFeedback ? 'copied' : ''} type="button" onClick={copyEditorTitle}>{copyTitleFeedback ? <Check size={14} /> : <Copy size={14} />}{copyTitleFeedback || '复制标题'}</button>
                      <button className={copyFeedback ? 'copied' : ''} type="button" onClick={copyPreview}>{copyFeedback ? <Check size={14} /> : <Copy size={14} />}{copyFeedback || '复制正文'}</button>
                      <button type="button" onClick={() => { setShareUrl(''); setShowShareModal(true) }}><Globe2 size={14} />分享</button>
                      <button className="preview-modal-close" type="button" onClick={() => setShowPreviewModal(false)}><X size={16} /></button>
                    </div>
                  </header>
                  <article className="article-preview markdown-body">
                    <ReactMarkdown remarkPlugins={[remarkGfm]} components={markdownPreviewComponents}>{stripImagePrompts(previewDraft || previewMessage.content)}</ReactMarkdown>
                  </article>
                </div>
              </div>
            )}
            {showShareModal && (
              <div className="modal-backdrop" onClick={() => setShowShareModal(false)}>
                <div className="modal-card share-modal" onClick={(e) => e.stopPropagation()}>
                  <header className="preview-modal-header">
                    <span>分享文章</span>
                    <button className="preview-modal-close" type="button" onClick={() => setShowShareModal(false)}><X size={16} /></button>
                  </header>
                  <div className="share-modal-body">
                    {!shareUrl ? (
                      <>
                        <label className="share-label">有效期</label>
                        <div className="share-expiry-options">
                          {([['1h', '1 小时'], ['24h', '24 小时'], ['7d', '7 天'], ['30d', '30 天'], ['permanent', '永久']] as const).map(([val, label]) => (
                            <button key={val} className={`share-expiry-btn ${shareExpiry === val ? 'active' : ''}`} type="button" onClick={() => setShareExpiry(val)}>{label}</button>
                          ))}
                        </div>
                        <button className="primary-button" type="button" disabled={shareLoading} onClick={shareArticle}>
                          {shareLoading ? '生成中...' : '生成分享链接'}
                        </button>
                      </>
                    ) : (
                      <>
                        <label className="share-label">分享链接</label>
                        <div className="share-link-row">
                          <input className="share-link-input" type="text" readOnly value={shareUrl} onFocus={(e) => e.target.select()} />
                          <button className="primary-button" type="button" onClick={() => { navigator.clipboard.writeText(shareUrl); showToast('链接已复制') }}>复制链接</button>
                        </div>
                        <p className="hint">{shareExpiry === 'permanent' ? '链接永久有效' : `链接将在 ${({ '1h': '1 小时', '24h': '24 小时', '7d': '7 天', '30d': '30 天' })[shareExpiry]} 后过期`}</p>
                      </>
                    )}
                  </div>
                </div>
              </div>
            )}
            {layoutPreviewText && (
              <div className="modal-backdrop" onClick={() => setLayoutPreviewText(null)}>
                <div className="modal-card preview-modal" onClick={(e) => e.stopPropagation()}>
                  <header className="preview-modal-header">
                    <span>排版预览</span>
                    <div className="preview-modal-actions">
                      <button className="preview-modal-close" type="button" onClick={() => setLayoutPreviewText(null)}><X size={16} /></button>
                    </div>
                  </header>
                  <article className="article-preview markdown-body">
                    <ReactMarkdown remarkPlugins={[remarkGfm]}>{stripImagePrompts(layoutPreviewText)}</ReactMarkdown>
                  </article>
                </div>
              </div>
            )}
          </div>
        ) : (
        <>
        <header className="topbar">
          <button className="mobile-menu-button" type="button" aria-label="打开会话列表" onClick={() => setIsMobileNavOpen(true)}>
            <Menu size={18} />
          </button>
          <div>
            <h1>{getPageTitle()}</h1>
            <p>{getPageSubtitle()}</p>
          </div>
          <button className={activePage === 'tasks' ? 'top-settings mobile-only-nav-action active' : 'top-settings mobile-only-nav-action'} type="button" onClick={activePage === 'tasks' ? goToChat : openTaskCenter}>
            {activePage === 'tasks' ? <ArrowLeft size={18} /> : <ListRestart size={18} />}
            {activePage === 'tasks' ? '返回前台' : '任务'}
          </button>
          <button className={activePage === 'assets' ? 'top-settings mobile-only-nav-action active' : 'top-settings mobile-only-nav-action'} type="button" onClick={activePage === 'assets' ? goToChat : openAssetLibrary}>
            {activePage === 'assets' ? <ArrowLeft size={18} /> : <Braces size={18} />}
            {activePage === 'assets' ? '返回前台' : '资产'}
          </button>
          <button className={activePage === 'images' ? 'top-settings mobile-only-nav-action active' : 'top-settings mobile-only-nav-action'} type="button" onClick={activePage === 'images' ? goToChat : openImageLibrary}>
            {activePage === 'images' ? <ArrowLeft size={18} /> : <ImagePlus size={18} />}
            {activePage === 'images' ? '返回前台' : '图片'}
          </button>
          <button className="top-settings" type="button" onClick={openSettings}>
            <PanelRightOpen size={18} />
            设置
          </button>
          {user.isAdmin && (
            <button className={activePage === 'admin' ? 'top-settings active top-return' : 'top-settings'} type="button" onClick={() => activePage === 'admin' ? goToChat() : openAdminPage()}>
              {activePage === 'admin' ? <ArrowLeft size={18} /> : <Settings size={18} />}
              {activePage === 'admin' ? '返回前台' : '后台管理'}
            </button>
          )}
          <span className={isStreaming ? 'live on' : 'live'}>{isStreaming ? '生成中' : '就绪'}</span>
        </header>

        {activePage === 'assets' ? (
          <div className="asset-library">
            <div className="task-toolbar">
              <strong className="asset-toolbar-title">文章资产{hasSelectedArticleAssets ? ` · 已选 ${selectedArticleProjectIds.size}` : ''}</strong>
              <div className="asset-library-tools">
                <select value={articleStatusFilter} onChange={(event) => setArticleStatusFilter(event.target.value)}>
                  <option value="all">全部状态</option>
                  <option value="draft">草稿</option>
                  <option value="completed">已完成</option>
                  <option value="running">生成中</option>
                  <option value="pending">排队中</option>
                  <option value="failed">失败</option>
                  <option value="canceled">已取消</option>
                </select>
                <input
                  className="asset-search"
                  value={assetKeyword}
                  onChange={(event) => setAssetKeyword(event.target.value)}
                  placeholder="搜索标题、摘要或状态"
                />
                <button className="ghost-button" type="button" disabled={assetsLoading} onClick={loadAssets}>
                  <RotateCcw size={15} />
                  {assetsLoading ? '刷新中' : '刷新'}
                </button>
              </div>
            </div>

            {filteredArticleAssets.length === 0 ? (
              <div className="empty-state task-empty">
                <Braces size={34} />
                <h2>{assetsLoading ? '正在加载文章' : assetKeyword || articleStatusFilter !== 'all' ? '没有匹配的文章' : '暂无文章资产'}</h2>
                <p>{assetKeyword || articleStatusFilter !== 'all' ? '换个关键词或状态再试试。' : '在文章卡片里点击“存入资产库”，这里会沉淀可复用内容。'}</p>
              </div>
            ) : (
              <>
                <div className="asset-selection-bar">
                  <button className="ghost-button" type="button" onClick={toggleVisibleArticleAssets}>
                    {allVisibleArticleAssetsSelected ? '取消当前选择' : '选择当前结果'}
                  </button>
                  {hasSelectedArticleAssets && (
                    <>
                      <button className="ghost-button" type="button" onClick={() => setSelectedArticleProjectIds(new Set())}>清空选择</button>
                      <button className="ghost-button danger" type="button" onClick={() => deleteSelectedArticleAssets()}>删除已选</button>
                    </>
                  )}
                </div>
                <div className="asset-grid">
                  {filteredArticleAssets.map((item) => {
                    const selected = selectedArticleProjectIds.has(item.projectId)
                    const excerpt = formatArticleAssetExcerpt(item)
                    return (
                      <article className={selected ? 'asset-card selected' : 'asset-card'} key={item.id}>
                        <div className="asset-card-head">
                          <label className="asset-card-check" title={selected ? '取消选择' : '选择文章'}>
                            <input type="checkbox" checked={selected} onChange={() => toggleArticleAssetSelection(item.projectId)} />
                            <span>选择</span>
                          </label>
                          <span className={`asset-status ${item.status}`}>{getAssetStatusLabel(item.status)}</span>
                        </div>
                        <div className="asset-card-meta">
                          <span>文章</span>
                          <span>v{item.version}</span>
                          <span>{formatMessageTime(item.updatedAt)}</span>
                        </div>
                        <strong title={item.title}>{item.title}</strong>
                        <p title={excerpt}>{excerpt}</p>
                        <div className="asset-card-footer">
                          <button className="asset-link-button" type="button" onClick={() => openArticleVersions(item)}><Eye size={14} />版本</button>
                          <button className="asset-link-button" type="button" onClick={() => copyArticleAssetBody(item)}><Copy size={14} />复制</button>
                          <button className="asset-link-button" type="button" onClick={() => openArticleAssetConversation(item)}><MessageSquarePlus size={14} />会话</button>
                          <button className="asset-link-button danger" type="button" onClick={() => deleteArticleAsset(item)}><Trash2 size={14} />删除</button>
                        </div>
                      </article>
                    )
                  })}
                </div>
              </>
            )}
          </div>
        ) : activePage === 'images' ? (
          <div className="image-library">
            <div className="image-library-toolbar">
              <div>
                <strong>图片资产</strong>
                <span>{filteredImageAssets.length} 张图片{hasSelectedImageAssets ? ` · 已选 ${selectedImageAssetIds.size}` : ''}</span>
              </div>
              <div className="image-library-tools">
                <select value={imageStatusFilter} onChange={(event) => setImageStatusFilter(event.target.value)}>
                  <option value="all">全部状态</option>
                  <option value="completed">已完成</option>
                  <option value="running">生成中</option>
                  <option value="pending">排队中</option>
                  <option value="failed">失败</option>
                </select>
                <input
                  className="asset-search"
                  value={assetKeyword}
                  onChange={(event) => setAssetKeyword(event.target.value)}
                  placeholder="搜索项目、提示词或状态"
                />
                <button className="ghost-button" type="button" disabled={assetsLoading} onClick={loadAssets}>
                  <RotateCcw size={15} />
                  {assetsLoading ? '刷新中' : '刷新'}
                </button>
              </div>
            </div>

            {filteredImageAssets.length === 0 ? (
              <div className="empty-state task-empty">
                <ImagePlus size={34} />
                <h2>{assetsLoading ? '正在加载图片' : assetKeyword || imageStatusFilter !== 'all' ? '没有匹配的图片' : '暂无图片资产'}</h2>
                <p>{assetKeyword || imageStatusFilter !== 'all' ? '换个关键词或状态再试试。' : '文章配图或图片生成后，会在这里集中管理。'}</p>
              </div>
            ) : (
              <>
                <div className="image-selection-bar">
                  <button className="ghost-button" type="button" onClick={toggleVisibleImageAssets}>
                    {allVisibleImageAssetsSelected ? '取消本页选择' : '选择当前结果'}
                  </button>
                  {hasSelectedImageAssets && (
                    <>
                      <button className="ghost-button" type="button" onClick={() => setSelectedImageAssetIds(new Set())}>清空选择</button>
                      <button className="ghost-button danger" type="button" onClick={() => deleteSelectedImageAssets()}>删除已选</button>
                    </>
                  )}
                </div>
                <div className="image-asset-board">
                  {filteredImageAssets.map((item) => (
                    (() => {
                      const imageUrl = getAssetImageUrl(item.imageUrl)
                      const selected = selectedImageAssetIds.has(item.id)
                      return (
                        <article className={selected ? 'image-asset-tile selected' : 'image-asset-tile'} key={item.id}>
                          <label className="image-asset-check">
                            <input type="checkbox" checked={selected} onChange={() => toggleImageAssetSelection(item.id)} />
                            选择
                          </label>
                          <button className="image-asset-preview" type="button" disabled={!imageUrl} onClick={() => imageUrl && setLightboxImage({ src: imageUrl, alt: item.prompt })}>
                            {imageUrl ? <img src={imageUrl} alt={item.prompt || item.projectTitle} loading="lazy" /> : <div className="image-placeholder" />}
                          </button>
                          <div className="image-asset-info">
                            <strong>{item.projectTitle}</strong>
                            <span>{formatMessageTime(item.createdAt)} · <b className={`asset-status ${item.status}`}>{getAssetStatusLabel(item.status)}</b></span>
                            <p>{item.prompt || '暂无提示词'}</p>
                          </div>
                          <div className="image-asset-actions">
                            <button className="image-icon-action" type="button" title="复制链接" aria-label="复制链接" disabled={!imageUrl} onClick={() => copyImageUrl(item)}><LinkIcon size={14} /></button>
                            <button className="image-icon-action" type="button" title="复制 Markdown" aria-label="复制 Markdown" disabled={!imageUrl} onClick={() => copyImageMarkdown(item)}><Braces size={14} /></button>
                            <button className="image-icon-action" type="button" title="复制提示词" aria-label="复制提示词" disabled={!item.prompt?.trim()} onClick={() => copyImagePrompt(item)}><Quote size={14} /></button>
                            <button className="image-icon-action" type="button" title="下载图片" aria-label="下载图片" disabled={!imageUrl} onClick={() => imageUrl && downloadPreviewImage(imageUrl, item.prompt || item.projectTitle)}><Download size={14} /></button>
                            {imageUrl ? (
                              <a className="image-icon-action" href={imageUrl} title="打开原图" aria-label="打开原图" target="_blank" rel="noreferrer"><ExternalLink size={14} /></a>
                            ) : (
                              <button className="image-icon-action" type="button" title="打开原图" aria-label="打开原图" disabled><ExternalLink size={14} /></button>
                            )}
                            <button className="image-icon-action danger" type="button" title="删除" aria-label="删除" onClick={() => deleteImageAsset(item)}><Trash2 size={14} /></button>
                          </div>
                        </article>
                      )
                    })()
                  ))}
                </div>
              </>
            )}
          </div>
        ) : activePage === 'tasks' ? (
          <div className="task-center">
            <div className="task-toolbar">
              <div className="task-filters" aria-label="任务状态筛选">
                {[
                  ['all', '全部'],
                  ['pending', '排队中'],
                  ['running', '生成中'],
                  ['completed', '已完成'],
                  ['failed', '失败'],
                  ['canceled', '已取消'],
                ].map(([value, label]) => (
                  <button
                    key={value}
                    className={jobStatusFilter === value ? 'active' : ''}
                    type="button"
                    onClick={() => changeJobStatusFilter(value)}
                  >
                    {label}
                  </button>
                ))}
              </div>
              <button className="ghost-button" type="button" disabled={jobsLoading} onClick={() => loadJobs()}>
                <RotateCcw size={15} />
                {jobsLoading ? '刷新中' : '刷新'}
              </button>
            </div>
            <div className="task-overview">
              <div>
                <span>任务总数</span>
                <strong>{jobOverview.total}</strong>
              </div>
              <div>
                <span>进行中</span>
                <strong>{jobOverview.running}</strong>
              </div>
              <div>
                <span>已完成</span>
                <strong>{jobOverview.completed}</strong>
              </div>
              <div>
                <span>失败率</span>
                <strong>{jobFailureRate}%</strong>
              </div>
            </div>

            {jobSummaries.length === 0 ? (
              <div className="empty-state task-empty">
                <ListRestart size={34} />
                <h2>{jobsLoading ? '正在加载任务' : '暂无任务记录'}</h2>
                <p>发起文章、文档或图片生成后，这里会显示进度和结果。</p>
              </div>
            ) : (
              <div className="task-list">
                {jobSummaries.map((item) => (
                  <article className="task-item" key={item.id}>
                    <div className="task-item-main">
                      <div className="task-item-head">
                        <span className={`task-status ${item.status}`}>{jobStatusLabels[item.status]}</span>
                        <strong>{jobTypeLabel(item.messageType)} · {item.conversationTitle}</strong>
                      </div>
                      <p>{item.requestPreview || '无请求摘要'}</p>
                      {item.contentPreview && <em>{item.contentPreview}</em>}
                      {item.error && <b>{item.error}</b>}
                    </div>
                    <div className="task-item-side">
                      <span>{formatMessageTime(item.updatedAt)}</span>
                      {isCancelableJob(item.status) && (
                        <button className="ghost-button danger" type="button" disabled={cancelingJobIds.has(item.id)} onClick={() => cancelJob(item)}>
                          {cancelingJobIds.has(item.id) ? '取消中' : '取消任务'}
                        </button>
                      )}
                      {isRetryableJob(item.status) && (
                        <button className="ghost-button" type="button" disabled={retryingJobIds.has(item.id)} onClick={() => retryJob(item)}>
                          {retryingJobIds.has(item.id) ? '提交中' : '重新生成'}
                        </button>
                      )}
                      <button className="ghost-button" type="button" disabled={jobDetailLoadingId === item.id} onClick={() => openJobDetail(item)}>
                        {jobDetailLoadingId === item.id ? '加载中' : '详情'}
                      </button>
                      <button className="ghost-button" type="button" onClick={() => openJobConversation(item)}>打开会话</button>
                    </div>
                  </article>
                ))}
              </div>
            )}
            {selectedJobDetail && (
              <div className="modal-backdrop" onClick={() => setSelectedJobDetail(null)}>
                <div className="modal-card task-detail-modal" onClick={(e) => e.stopPropagation()}>
                  <header className="modal-header">
                    <div>
                      <h2>任务详情</h2>
                      <p>{jobStatusLabels[selectedJobDetail.status]} · {formatMessageTime(selectedJobDetail.updatedAt)}</p>
                    </div>
                    <button type="button" onClick={() => setSelectedJobDetail(null)}><X size={16} /></button>
                  </header>
                  <div className="task-detail-body">
                    {selectedJobDetail.error && <p className="task-detail-error">{selectedJobDetail.error}</p>}
                    {selectedJobDetail.thinking.length > 0 && (
                      <section>
                        <strong>执行记录</strong>
                        <ol>
                          {selectedJobDetail.thinking.map((item, index) => <li key={index}>{item}</li>)}
                        </ol>
                      </section>
                    )}
                    <section>
                      <strong>生成内容</strong>
                      <pre>{stripImagePrompts(selectedJobDetail.content || '暂无内容')}</pre>
                    </section>
                  </div>
                </div>
              </div>
            )}
          </div>
        ) : activePage === 'admin' && user.isAdmin ? (
          <div className="admin-page">
            <nav className="admin-tabs">
              <button className={adminTab === 'overview' ? 'active' : ''} type="button" onClick={() => setAdminTab('overview')}><Eye size={16} />概览</button>
              <button className={adminTab === 'accounts' ? 'active' : ''} type="button" onClick={() => setAdminTab('accounts')}><Users size={16} />账号管理</button>
              <button className={adminTab === 'settings' ? 'active' : ''} type="button" onClick={() => setAdminTab('settings')}><Settings size={16} />系统设置</button>
              <button className={adminTab === 'prompts' ? 'active' : ''} type="button" onClick={() => setAdminTab('prompts')}><Braces size={16} />提示词</button>
              <button className={adminTab === 'email' ? 'active' : ''} type="button" onClick={() => setAdminTab('email')}><Mail size={16} />邮件服务</button>
              <button className="admin-back" type="button" onClick={goToChat}><ArrowLeft size={16} />返回前台</button>
            </nav>

            {adminTab === 'overview' && (
              <section className="admin-tab-content">
                <div className="admin-tab-header">
                  <div>
                    <h2>运营概览</h2>
                    <p>快速查看账号、任务、资产和失败情况。</p>
                  </div>
                  <button className="ghost-button" type="button" onClick={loadAdminData}><RotateCcw size={15} />刷新</button>
                </div>
                {adminDashboard ? (
                  <>
                    <div className="admin-metric-grid">
                      <div><span>用户数</span><strong>{adminDashboard.totalUsers}</strong><em>{adminDashboard.enabledUsers} 个启用</em></div>
                      <div><span>会话数</span><strong>{adminDashboard.totalConversations}</strong><em>全站累计</em></div>
                      <div><span>任务数</span><strong>{adminDashboard.totalJobs}</strong><em>{adminDashboard.runningJobs + adminDashboard.pendingJobs} 个进行中</em></div>
                      <div><span>任务失败率</span><strong>{adminDashboard.totalJobs ? Math.round((adminDashboard.failedJobs / adminDashboard.totalJobs) * 100) : 0}%</strong><em>{adminDashboard.failedJobs} 个失败</em></div>
                      <div><span>文章资产</span><strong>{adminDashboard.articleAssets}</strong><em>版本记录</em></div>
                      <div><span>图片资产</span><strong>{adminDashboard.imageAssets}</strong><em>素材记录</em></div>
                    </div>
                    <div className="admin-status-strip">
                      <span>排队 {adminDashboard.pendingJobs}</span>
                      <span>生成中 {adminDashboard.runningJobs}</span>
                      <span>已完成 {adminDashboard.completedJobs}</span>
                      <span>已取消 {adminDashboard.canceledJobs}</span>
                      <span>需处理 {adminDashboard.staleJobs}</span>
                    </div>
                    {adminDashboard.staleJobs > 0 && (
                      <button
                        className="ghost-button"
                        type="button"
                        onClick={async () => {
                          const ok = await openConfirm({
                            title: '标记异常任务',
                            message: '这会把长时间未更新的进行中任务标记为失败，已生成的内容不会删除。',
                            confirmText: '标记',
                            tone: 'warning',
                          })
                          if (!ok) return
                          await request<{ count: number }>('/api/admin/jobs/mark-stale-failed', { method: 'POST' })
                          showToast('已处理长时间未更新任务')
                          void loadAdminData()
                        }}
                      >
                        <RotateCcw size={15} />
                        标记异常任务
                      </button>
                    )}
                    <div className="recent-failures">
                      <div className="panel-title">最近失败</div>
                      {adminDashboard.recentFailures.length === 0 ? (
                        <p className="hint">暂无失败任务。</p>
                      ) : adminDashboard.recentFailures.map((item) => (
                        <article key={item.id}>
                          <strong>{jobTypeLabel(item.messageType)} · {item.conversationTitle}</strong>
                          <span>{formatMessageTime(item.updatedAt)}</span>
                          <p>{item.error}</p>
                        </article>
                      ))}
                    </div>
                    <div className="recent-failures">
                      <div className="panel-title">最近意图识别</div>
                      {adminDashboard.recentIntents.length === 0 ? (
                        <p className="hint">暂无任务记录。</p>
                      ) : adminDashboard.recentIntents.map((item) => (
                        <article key={item.jobId}>
                          <strong>{jobTypeLabel(item.messageType)} · {item.conversationTitle}</strong>
                          <span>{item.status} · {formatMessageTime(item.updatedAt)}</span>
                          <p>{item.requestPreview || '无请求摘要'}</p>
                        </article>
                      ))}
                    </div>
                    <div className="recent-failures">
                      <div className="panel-title">操作审计</div>
                      {adminDashboard.recentAudits.length === 0 ? (
                        <p className="hint">暂无操作记录。</p>
                      ) : adminDashboard.recentAudits.map((item) => (
                        <article key={item.id}>
                          <strong>{item.actorName} · {item.operation}</strong>
                          <span>{formatMessageTime(item.createdAt)}</span>
                          <p>{item.detail || `${item.entityType} ${item.entityId}`}</p>
                        </article>
                      ))}
                    </div>
                  </>
                ) : (
                  <p className="hint">正在加载运营概览...</p>
                )}
              </section>
            )}

            {adminTab === 'accounts' && (
              <section className="admin-tab-content">
                <div className="admin-tab-header">
                  <div>
                    <h2>账号管理</h2>
                    <p>{adminUsers.length} 个账号 · {adminUsers.filter((item) => item.isEnabled).length} 个启用</p>
                  </div>
                  <button className="primary-button admin-create-button" type="button" onClick={() => setShowCreateUser(true)}><UserPlus size={16} />新增账号</button>
                </div>
                <div className="admin-user-grid">
                  {adminUsers.map((item) => (
                    <div className="admin-user-card" key={item.id}>
                      <div className="admin-user-info">
                        <span className="admin-avatar">{(item.displayName || item.email || 'U').slice(0, 1).toUpperCase()}</span>
                        <div>
                          <strong>{item.displayName || item.email.split('@')[0]}</strong>
                          <em>{item.email}</em>
                        </div>
                      </div>
                      <div className="admin-user-badges">
                        <span className={`badge ${item.isEnabled ? 'badge-green' : 'badge-gray'}`}>{item.isEnabled ? '启用' : '禁用'}</span>
                        <span className={`badge ${item.isAdmin ? 'badge-blue' : 'badge-gray'}`}>{item.isAdmin ? '管理员' : '用户'}</span>
                      </div>
                      <div className="admin-user-actions">
                        <button className="admin-action-button" type="button" onClick={() => { setEditingUser(item); editAdminProvider(item) }}><Settings size={14} />API</button>
                        <button className="admin-action-button" type="button" onClick={() => openPasswordReset(item)}><KeyRound size={14} />密码</button>
                        <button className={item.isEnabled ? 'admin-action-button danger' : 'admin-action-button'} type="button" onClick={() => toggleAdminUser(item, { isEnabled: !item.isEnabled })}>{item.isEnabled ? <X size={14} /> : <Check size={14} />}{item.isEnabled ? '禁用' : '启用'}</button>
                        <button className="admin-action-button" type="button" onClick={() => toggleAdminUser(item, { isAdmin: !item.isAdmin })}><Users size={14} />{item.isAdmin ? '取消管理员' : '设管理员'}</button>
                      </div>
                    </div>
                  ))}
                </div>
              </section>
            )}

            {adminTab === 'settings' && (
              <section className="admin-tab-content">
                <div className="admin-tab-header">
                  <h2>系统设置</h2>
                  <button className="primary-button" type="button" onClick={saveAdminConfig}>保存设置</button>
                </div>
                {adminConfig ? (
                  <div className="admin-config-page">
                    <label><input type="checkbox" checked={adminConfig.allowRegistration} onChange={(e) => setAdminConfig({ ...adminConfig, allowRegistration: e.target.checked })} />允许用户注册</label>
                    <label><input type="checkbox" checked={adminConfig.requireEmailCode} onChange={(e) => setAdminConfig({ ...adminConfig, requireEmailCode: e.target.checked })} />注册需要邮箱验证码</label>
                    <label><input type="checkbox" checked={adminConfig.emailCodeEnabled} onChange={(e) => setAdminConfig({ ...adminConfig, emailCodeEnabled: e.target.checked })} />启用邮箱验证码服务</label>
                  </div>
                ) : (
                  <p className="hint">正在加载管理配置...</p>
                )}
              </section>
            )}

            {adminTab === 'prompts' && (
              <section className="admin-tab-content">
                <div className="admin-tab-header">
                  <div>
                    <h2>提示词配置</h2>
                    <p>这里的内容会作为系统提示补充生效，用来调整口径、风格和不同任务链路。</p>
                  </div>
                  <div className="admin-header-actions">
                    <button className="ghost-button" type="button" onClick={() => setAdminPromptConfig(recommendedPromptConfig)}>填入推荐配置</button>
                    <button className="primary-button" type="button" onClick={saveAdminPromptConfig}>保存提示词</button>
                  </div>
                </div>
                {adminPromptConfig ? (
                  <div className="prompt-config-grid">
                    <label>全局补充<textarea value={adminPromptConfig.global} onChange={(e) => setAdminPromptConfig({ ...adminPromptConfig, global: e.target.value })} placeholder="例如：统一使用品牌口吻、禁用词、输出边界等。" /></label>
                    <label>问答补充<textarea value={adminPromptConfig.chat} onChange={(e) => setAdminPromptConfig({ ...adminPromptConfig, chat: e.target.value })} placeholder="普通对话和问题解答的额外规则。" /></label>
                    <label>文章补充<textarea value={adminPromptConfig.article} onChange={(e) => setAdminPromptConfig({ ...adminPromptConfig, article: e.target.value })} placeholder="文章生成时的结构、标题、开头、结尾等要求。" /></label>
                    <label>文档补充<textarea value={adminPromptConfig.document} onChange={(e) => setAdminPromptConfig({ ...adminPromptConfig, document: e.target.value })} placeholder="方案、报告、清单、SOP 等文档输出规则。" /></label>
                    <label>图片补充<textarea value={adminPromptConfig.image} onChange={(e) => setAdminPromptConfig({ ...adminPromptConfig, image: e.target.value })} placeholder="直接生图或配图时的画面约束。" /></label>
                    <label>改写补充<textarea value={adminPromptConfig.rewrite} onChange={(e) => setAdminPromptConfig({ ...adminPromptConfig, rewrite: e.target.value })} placeholder="润色、扩写、降重、本地化等要求。" /></label>
                    <label>排版补充<textarea value={adminPromptConfig.layout} onChange={(e) => setAdminPromptConfig({ ...adminPromptConfig, layout: e.target.value })} placeholder="Markdown、公众号、小红书等排版规则。" /></label>
                  </div>
                ) : (
                  <p className="hint">正在加载提示词配置...</p>
                )}
              </section>
            )}

            {adminTab === 'email' && (
              <section className="admin-tab-content">
                <div className="admin-tab-header">
                  <h2>邮件服务</h2>
                  <button className="primary-button" type="button" onClick={saveAdminConfig}>保存配置</button>
                </div>
                {adminConfig && (
                  <div className="admin-form-grid">
                    <label>SMTP Host<input value={adminConfig.smtpHost} placeholder="smtp.qq.com" onChange={(e) => setAdminConfig({ ...adminConfig, smtpHost: e.target.value })} /></label>
                    <label>SMTP Port<input type="number" value={adminConfig.smtpPort} onChange={(e) => setAdminConfig({ ...adminConfig, smtpPort: Number(e.target.value) })} /></label>
                    <label>邮箱账号<input value={adminConfig.userName} onChange={(e) => setAdminConfig({ ...adminConfig, userName: e.target.value })} /></label>
                    <label>邮箱授权码/密码<input type="password" placeholder="留空则不修改已保存密码" onChange={(e) => setAdminConfig({ ...adminConfig, password: e.target.value })} /></label>
                    <label>发件邮箱<input value={adminConfig.fromEmail} onChange={(e) => setAdminConfig({ ...adminConfig, fromEmail: e.target.value })} /></label>
                    <label>发件名称<input value={adminConfig.fromName} onChange={(e) => setAdminConfig({ ...adminConfig, fromName: e.target.value })} /></label>
                    <label>验证码有效分钟<input type="number" value={adminConfig.codeMinutes} onChange={(e) => setAdminConfig({ ...adminConfig, codeMinutes: Number(e.target.value) })} /></label>
                    <label className="checkbox-line"><input type="checkbox" checked={adminConfig.enableSsl} onChange={(e) => setAdminConfig({ ...adminConfig, enableSsl: e.target.checked })} />启用 SSL/TLS</label>
                  </div>
                )}
              </section>
            )}

            {showCreateUser && (
              <div className="modal-backdrop" onClick={() => setShowCreateUser(false)}>
                <div className="modal-card" onClick={(e) => e.stopPropagation()}>
                  <div className="modal-header">
                    <h2>新增账号</h2>
                    <button type="button" onClick={() => setShowCreateUser(false)}><X size={18} /></button>
                  </div>
                  <div className="modal-body">
                    <label>邮箱<input value={adminNewUser.email} type="email" placeholder="user@example.com" onChange={(e) => setAdminNewUser({ ...adminNewUser, email: e.target.value })} /></label>
                    <label>昵称<input value={adminNewUser.displayName} placeholder="可留空，默认取邮箱前缀" onChange={(e) => setAdminNewUser({ ...adminNewUser, displayName: e.target.value })} /></label>
                    <label>初始密码<input value={adminNewUser.password} type="password" placeholder="请输入初始密码" onChange={(e) => setAdminNewUser({ ...adminNewUser, password: e.target.value })} /></label>
                    <div className="modal-checkboxes">
                      <label><input type="checkbox" checked={adminNewUser.isEnabled} onChange={(e) => setAdminNewUser({ ...adminNewUser, isEnabled: e.target.checked })} />启用账号</label>
                      <label><input type="checkbox" checked={adminNewUser.isAdmin} onChange={(e) => setAdminNewUser({ ...adminNewUser, isAdmin: e.target.checked })} />设为管理员</label>
                    </div>
                  </div>
                  <div className="modal-footer">
                    <button className="ghost-button" type="button" onClick={() => setShowCreateUser(false)}>取消</button>
                    <button className="primary-button" type="button" onClick={createAdminUser}>创建账号</button>
                  </div>
                </div>
              </div>
            )}

            {editingUser && (
              <div className="modal-backdrop" onClick={() => { setEditingUser(null); setAdminProviderUserId(null) }}>
                <div className="modal-card modal-wide" onClick={(e) => e.stopPropagation()}>
                  <div className="modal-header">
                    <h2>{editingUser.displayName || editingUser.email} — API 配置</h2>
                    <button type="button" onClick={() => { setEditingUser(null); setAdminProviderUserId(null) }}><X size={18} /></button>
                  </div>
                  <div className="modal-body">
                    <div className="admin-provider-hint">
                      <strong>OpenAI 兼容接口</strong>
                      <span>{adminProviderPreview ? `Key 已配置：${adminProviderPreview}` : '为该账号单独配置模型服务'}</span>
                      <button className="ghost-button" type="button" onClick={fillFromMyProvider}>一键引入管理员配置</button>
                    </div>
                    <div className="admin-form-grid">
                      <label>配置名称<input value={adminProvider.name} onChange={(e) => setAdminProvider({ ...adminProvider, name: e.target.value })} /></label>
                      <label>Base URL<input value={adminProvider.baseUrl} placeholder="https://api.openai.com/v1" onChange={(e) => setAdminProvider({ ...adminProvider, baseUrl: e.target.value })} /></label>
                      <label>API Key<input value={adminProvider.apiKey} type="password" placeholder={adminProviderPreview ? `已配置：${adminProviderPreview}。如需替换请输入新 Key` : '输入该账号使用的 API Key'} onChange={(e) => setAdminProvider({ ...adminProvider, apiKey: e.target.value })} /></label>
                      <label>聊天模型<input value={adminProvider.chatModelName} placeholder="例如 gpt-4.1-mini" onChange={(e) => setAdminProvider({ ...adminProvider, chatModelName: e.target.value })} /></label>
                      <label>生图模型<input value={adminProvider.imageModelName} placeholder="例如 gpt-image-1" onChange={(e) => setAdminProvider({ ...adminProvider, imageModelName: e.target.value })} /></label>
                    </div>
                    <div className="provider-test-row">
                      <button className="ghost-button" type="button" onClick={() => testAdminProvider('chat')}>测试聊天</button>
                      <button className="ghost-button" type="button" onClick={() => testAdminProvider('image')}>测试生图</button>
                    </div>
                    {adminProviderTestStatus && <p className="hint">{adminProviderTestStatus}</p>}
                    {adminProviderStatus && <p className="hint">{adminProviderStatus}</p>}
                  </div>
                  <div className="modal-footer">
                    <button className="ghost-button" type="button" onClick={() => { setEditingUser(null); setAdminProviderUserId(null) }}>取消</button>
                    <button className="primary-button" type="button" onClick={saveAdminProvider}>保存配置</button>
                  </div>
                </div>
              </div>
            )}
          </div>
        ) : (
          <>
        <div className="messages" ref={messagesRef}>
          {messages.length === 0 && (
            <div className="empty-state">
              <Bot size={38} />
              <h2>开始使用 AI 工作台</h2>
              <p>输入问题、选题、文章素材或图片需求，AI 会按任务类型给出结果。</p>
            </div>
          )}
          {messages.map((message, index) => (
            <article className={`message ${message.role}`} key={message.id}>
              <div className={message.role === 'assistant' ? 'agent-avatar' : 'user-avatar'}>
                {message.role === 'assistant' ? <Bot size={17} /> : (user.displayName || user.email || '你').slice(0, 1)}
              </div>
              <div className="message-stack">
                <div className="message-meta">
                  <span>{message.role === 'user' ? (user.displayName || user.email || '你') : '内容运营助手'}</span>
                  {message.role === 'user' && (
                    <button type="button" title="重新生成" onClick={() => retryFromMessage(message)} disabled={isStreaming}>
                      <RotateCcw size={14} />
                      重试
                    </button>
                  )}
                  {message.role === 'assistant' && !isMessageGenerating(message) && !(isArticleMessageMode(getMessageMode(message)) || (!getMessageMode(message) && isArticleLike(message.content, getPreviousUserContent(index)))) && (
                    <div className="message-export-actions" aria-label="导出内容">
                      <button type="button" title="导出 DOCX" onClick={() => downloadMessageExport(message, 'docx')}>
                        <FileText size={14} />
                        DOCX
                      </button>
                      <button type="button" title="导出 PPTX" onClick={() => downloadMessageExport(message, 'pptx')}>
                        <Presentation size={14} />
                        PPT
                      </button>
                    </div>
                  )}
                </div>
                {message.role === 'assistant' && !isGeneratingImageMessage(message) && message.thinking && message.thinking.length > 0 && (
                  <div className="thinking-box">
                    <strong>执行过程</strong>
                    {message.thinking.map((item, index) => <p key={`${item}-${index}`}>{sanitizeThinkingText(item)}</p>)}
                  </div>
                )}
                {message.role === 'assistant' && (isArticleMessageMode(getMessageMode(message)) || (!getMessageMode(message) && isArticleLike(message.content, getPreviousUserContent(index)))) ? (
                  <>
                    <div className={isMessageGenerating(message) ? 'document-card generating' : 'document-card'}>
                      <button
                        className="document-card-main"
                        type="button"
                        disabled={isMessageGenerating(message)}
                        onClick={() => openEditor(message)}
                      >
                        <span className="document-card-kind">文章</span>
                        <strong>{getCleanDocumentTitle(cleanArticleContent(message.content))}</strong>
                        <p>{getDocumentExcerpt(cleanArticleContent(message.content)) || '内容正在生成中，点击可查看当前预览。'}</p>
                      </button>
                      <div className="document-card-footer">
                        <span>{isMessageGenerating(message) ? '正在生成文章...' : `${getContentTextLength(cleanArticleContent(message.content))} 字`}</span>
                        {!isMessageGenerating(message) && <span>{formatMessageTime(message.createdAt)}</span>}
                        {!isMessageGenerating(message) && (
                          <div className="document-card-actions">
                            <button type="button" onClick={() => saveArticleAsset(message)}><Save size={14} />资产</button>
                            <button type="button" onClick={() => openEditor(message)}><PanelRightOpen size={14} />编辑</button>
                            <button type="button" onClick={() => downloadMessageExport(message, 'docx')}><FileText size={14} />DOCX</button>
                            <button type="button" onClick={() => downloadMessageExport(message, 'pptx')}><Presentation size={14} />PPT</button>
                          </div>
                        )}
                      </div>
                    </div>
                    {!isMessageGenerating(message) && renderReferencePanel(extractReferenceSection(message.content))}
                  </>
                ) : (
                  renderTextMessage(message)
                )}
                {renderMessageActions(message)}
              </div>
            </article>
          ))}
          <div ref={bottomRef} />
        </div>

        <form className="composer" onSubmit={sendMessage}>
          <div
            className={isComposerDragging ? 'composer-card dragging' : 'composer-card'}
            onDragOver={(event) => {
              event.preventDefault()
              setIsComposerDragging(true)
            }}
            onDragLeave={handleComposerDragLeave}
            onDrop={(event) => void handleComposerDrop(event)}
          >
            {isComposerDragging && <div className="composer-drop-hint">松开即可上传图片或文件</div>}
            <textarea
              value={draft}
              onChange={(e) => setDraft(e.target.value)}
              onKeyDown={handleComposerKeyDown}
              onPaste={(event) => void handleComposerPaste(event)}
              placeholder="发消息，粘贴图片，或拖入文件..."
            />
            {attachments.length > 0 && (
              <div className="attachment-row">
                {attachments.map((file) => (
                  <span className="attachment-chip" key={file.url}>
                    {file.fileName}
                    <button type="button" title="移除附件" onClick={() => setAttachments((current) => current.filter((item) => item.url !== file.url))}>
                      <X size={14} />
                    </button>
                  </span>
                ))}
              </div>
            )}
            <div className="composer-bottom-row">
              <div className="capability-row" aria-label="常用能力">
                <label className="capability-add" title="上传图片或文件">
                  <Plus size={19} />
                  <input type="file" multiple onChange={(e) => uploadFiles(e.target.files)} />
                </label>
                <span className="capability-divider" />
                {primaryCapabilities.map((item) => (
                  <button className="capability-button" type="button" key={item.key} onClick={() => applyCapability(item.key)}>
                    {item.icon}
                    {item.label}
                  </button>
                ))}
                <div className="capability-more">
                  <button
                    className={isToolMenuOpen ? 'capability-button active' : 'capability-button'}
                    type="button"
                    onClick={() => setIsToolMenuOpen((value) => !value)}
                  >
                    <MoreHorizontal size={17} />
                    更多
                  </button>
                  {isToolMenuOpen && (
                    <div className="capability-menu">
                      {moreCapabilities.map((item) => (
                        <button type="button" key={item.key} onClick={() => applyCapability(item.key)}>
                          {item.icon}
                          {item.label}
                        </button>
                      ))}
                    </div>
                  )}
                </div>
              </div>
              <div className="composer-submit">
                {uploadStatus && <span>{uploadStatus}</span>}
                <button className="send-circle" type="submit" disabled={isStreaming} title="发送"><Send size={22} /></button>
              </div>
            </div>
            <div className="style-preset-row compact-style-row" aria-label="内容风格">
              {stylePresets.map((preset) => (
                <button
                  key={preset.value}
                  className={agentOptions.stylePreset === preset.value ? 'style-preset active' : 'style-preset'}
                  type="button"
                  onClick={() => setAgentOptions({ ...agentOptions, stylePreset: preset.value, temperature: preset.temperature })}
                >
                  {preset.label}
                </button>
              ))}
            </div>
          </div>
        </form>
          </>
        )}
        </>
        )}
      </section>

      {isSettingsOpen && (
        <div className="settings-layer">
          <button className="settings-backdrop" type="button" onClick={closeSettings} aria-label="关闭设置" />
          <aside className="settings-drawer">
            <div className="drawer-head">
              <div>
                <strong>运行设置</strong>
                <span>{hasApiKey ? `API Key 已配置：${apiKeyPreview}` : '配置 Agent 参数和模型'}</span>
              </div>
              <button type="button" onClick={closeSettings}><X size={18} /></button>
            </div>
            {activePage !== 'chat' && (
              <button className="settings-return" type="button" onClick={goToChat}>
                <ArrowLeft size={16} />
                返回前台
              </button>
            )}

            <div className="settings-card account-panel">
              <div className="settings-section-head">
                <span><KeyRound size={18} />账号安全</span>
                <p>修改当前登录账号的密码。</p>
              </div>
              <label>当前密码<input value={currentPassword} onChange={(e) => setCurrentPassword(e.target.value)} type="password" autoComplete="current-password" /></label>
              <label>新密码<input value={newPassword} onChange={(e) => setNewPassword(e.target.value)} type="password" autoComplete="new-password" /></label>
              <button className="ghost-button" type="button" onClick={changeOwnPassword}>修改密码</button>
              {passwordStatus && <p className="hint">{passwordStatus}</p>}
            </div>

            <form className="settings-card provider-form" onSubmit={saveProvider}>
              <div className="settings-section-head">
                <span><Settings size={18} />个人 API 配置</span>
                <p>支持 OpenAI 兼容接口。保存后 Key 会脱敏显示。</p>
              </div>
              <label>配置名称<input value={provider.name} placeholder="例如：OpenAI、Azure OpenAI、公司模型网关" onChange={(e) => setProvider({ ...provider, name: e.target.value })} /></label>
              <label>Base URL<input value={provider.baseUrl} placeholder="例如：https://api.openai.com/v1" onChange={(e) => setProvider({ ...provider, baseUrl: e.target.value })} /></label>
              <label>API Key<input value={provider.apiKey} placeholder={hasApiKey ? `已配置：${apiKeyPreview}。如需替换，请输入新 Key。` : '填写 API Key 后可获取模型列表'} onChange={(e) => setProvider({ ...provider, apiKey: e.target.value })} type="password" /></label>
              {hasApiKey && <div className="key-badge">API Key 已配置：{apiKeyPreview}</div>}
              <div className="provider-test-row">
                <button className="ghost-button" type="button" onClick={fetchModels}><ListRestart size={18} />获取模型</button>
                <button className="ghost-button" type="button" onClick={() => testProvider('chat')}>测试聊天</button>
                <button className="ghost-button" type="button" onClick={() => testProvider('image')}>测试生图</button>
              </div>
              {modelStatus && <p className="hint">{modelStatus}</p>}
              {providerTestStatus && <p className="hint">{providerTestStatus}</p>}
              <label>
                聊天模型
                <input value={provider.chatModelName} list={chatModelListId} placeholder="输入模型名，或从获取到的模型里选择" onChange={(e) => setProvider({ ...provider, chatModelName: e.target.value })} />
                <datalist id={chatModelListId}>{modelOptions.map((model) => <option key={model} value={model} />)}</datalist>
              </label>
              <label>
                生图模型
                <input value={provider.imageModelName} list={imageModelListId} placeholder="输入生图模型名，或从获取到的模型里选择" onChange={(e) => setProvider({ ...provider, imageModelName: e.target.value })} />
                <datalist id={imageModelListId}>{modelOptions.map((model) => <option key={model} value={model} />)}</datalist>
              </label>
              <button className="primary-button" type="submit">保存配置</button>
              {providerStatus && <p className="hint">{providerStatus}</p>}
            </form>

            <div className="artifact-preview">
              <div className="panel-title">当前产物</div>
              <p>第一版会把 Agent 输出直接保存在会话里。下一步可以把标题、正文、配图 prompt 拆成结构化文章和素材库。</p>
            </div>
          </aside>
        </div>
      )}
    </main>
    {toastMessage && <div className="global-toast">{toastMessage}</div>}
    {confirmDialog && (
      <div className="confirm-backdrop" onClick={() => closeConfirm(false)}>
        <section className="confirm-card" role="dialog" aria-modal="true" aria-labelledby="confirm-title" onClick={(event) => event.stopPropagation()}>
          <div className={confirmDialog.tone === 'warning' ? 'confirm-icon warning' : 'confirm-icon danger'}>
            <AlertTriangle size={22} />
          </div>
          <div className="confirm-content">
            <h2 id="confirm-title">{confirmDialog.title}</h2>
            <p>{confirmDialog.message}</p>
          </div>
          <div className="confirm-actions">
            <button className="ghost-button" type="button" onClick={() => closeConfirm(false)}>
              {confirmDialog.cancelText || '取消'}
            </button>
            <button className={confirmDialog.tone === 'warning' ? 'primary-button warning' : 'primary-button danger'} type="button" onClick={() => closeConfirm(true)} autoFocus>
              {confirmDialog.confirmText || '确定'}
            </button>
          </div>
        </section>
      </div>
    )}
    {passwordResetUser && (
      <div className="modal-backdrop" onClick={() => setPasswordResetUser(null)}>
        <div className="modal-card password-reset-modal" onClick={(event) => event.stopPropagation()}>
          <header className="modal-header">
            <div>
              <h2>重置密码</h2>
              <p>{passwordResetUser.email}</p>
            </div>
            <button type="button" onClick={() => setPasswordResetUser(null)}><X size={16} /></button>
          </header>
          <div className="modal-body password-reset-body">
            <label>
              新密码
              <input
                value={passwordResetValue}
                type="password"
                autoComplete="new-password"
                autoFocus
                placeholder="至少 6 位"
                onChange={(event) => {
                  setPasswordResetValue(event.target.value)
                  setPasswordResetStatus('')
                }}
                onKeyDown={(event) => {
                  if (event.key === 'Enter') {
                    event.preventDefault()
                    void resetAdminPassword()
                  }
                }}
              />
            </label>
            {passwordResetStatus && <p className="hint">{passwordResetStatus}</p>}
          </div>
          <div className="modal-footer">
            <button className="ghost-button" type="button" onClick={() => setPasswordResetUser(null)}>取消</button>
            <button className="primary-button" type="button" onClick={() => resetAdminPassword()}>保存</button>
          </div>
        </div>
      </div>
    )}
    {articleVersions && (
      <div className="modal-backdrop" onClick={() => setArticleVersions(null)}>
        <div className="modal-card modal-wide version-modal" onClick={(event) => event.stopPropagation()}>
          <header className="preview-modal-header">
            <div>
              <strong>{articleVersionTitle || '文章版本'}</strong>
              <span>{articleVersionsLoading ? '正在加载版本' : `共 ${articleVersions.length} 个版本`}</span>
            </div>
            <button className="preview-modal-close" type="button" onClick={() => setArticleVersions(null)}><X size={16} /></button>
          </header>
          <div className="version-list">
            {articleVersionsLoading ? (
              <div className="task-loading">正在整理版本...</div>
            ) : articleVersions.length === 0 ? (
              <div className="task-loading">暂无版本记录</div>
            ) : articleVersions.map((item) => (
              <article className="version-item" key={item.id}>
                <div className="version-meta">
                  <strong>v{item.version} · {item.title}</strong>
                  <div>
                    <span>{formatMessageTime(item.createdAt)}</span>
                    <button className="ghost-button" type="button" disabled={restoringVersionId === item.id} onClick={() => restoreArticleVersion(item)}>
                      {restoringVersionId === item.id ? '恢复中' : '恢复为最新版'}
                    </button>
                  </div>
                </div>
                <p>{item.excerpt}</p>
                <details>
                  <summary>预览全文</summary>
                  <div className="markdown-body">
                    <ReactMarkdown remarkPlugins={[remarkGfm]}>{stripImagePrompts(item.body)}</ReactMarkdown>
                  </div>
                </details>
              </article>
            ))}
          </div>
        </div>
      </div>
    )}
    {lightboxImage && (
      <div className="image-lightbox" onClick={() => setLightboxImage(null)}>
        <button className="image-lightbox-close" type="button" onClick={() => setLightboxImage(null)}><X size={18} /></button>
        <img src={lightboxImage.src} alt={lightboxImage.alt ?? ''} onClick={(event) => event.stopPropagation()} />
      </div>
    )}
    </>
  )
}

export default App
