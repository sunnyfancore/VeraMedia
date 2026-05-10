import { useEffect, useRef, useState, useCallback } from 'react'
import type { ClipboardEvent, CSSProperties, DragEvent, FormEvent, KeyboardEvent, MouseEvent as ReactMouseEvent, ReactNode } from 'react'
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
  ArrowDown,
  Bold,
  Bot,
  Bookmark,
  Braces,
  Check,
  ChevronDown,
  Copy,
  Cpu,
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
  MoreHorizontal,
  Music,
  PanelRightOpen,
  Plus,
  Presentation,
  Quote,
  Redo2,
  RotateCcw,
  Save,
  Send,
  Settings,
  Sparkles,
  Table as TableIcon,
  Trash2,
  Undo2,
  UploadCloud,
  UserPlus,
  Users,
  Video,
  X,
  Zap,
  Languages,
  CircleHelp,
  ChartColumn,
  Volume2,
  Play,
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
type CapabilityKey = 'quick' | 'write' | 'image' | 'code' | 'translate' | 'research' | 'qa' | 'data' | 'super' | 'ppt'
type ThinkingMode = 'quick' | 'think' | 'expert'

const thinkingModes: Array<{ key: ThinkingMode; label: string; description: string; icon: ReactNode }> = [
  { key: 'quick', label: '快速', description: '适用于大部分情况', icon: <Zap size={17} /> },
  { key: 'think', label: '思考', description: '擅长解决更难的问题', icon: <Cpu size={17} /> },
  { key: 'expert', label: '专家', description: '研究级智能模型', icon: <Sparkles size={17} /> },
] as const

const thinkingModeLabels: Record<ThinkingMode, string> = {
  quick: '快速',
  think: '思考',
  expert: '专家',
}

const isThinkingMode = (value: string): value is ThinkingMode =>
  value === 'quick' || value === 'think' || value === 'expert'

const primaryCapabilities: Array<{ key: CapabilityKey; label: string; icon: ReactNode }> = [
  { key: 'write', label: '帮我写作', icon: <FileText size={17} /> },
  { key: 'image', label: '图像生成', icon: <ImagePlus size={17} /> },
  { key: 'code', label: '编程', icon: <Braces size={17} /> },
  { key: 'translate', label: '翻译', icon: <Languages size={17} /> },
]

const moreCapabilities: Array<{ key: CapabilityKey; label: string; icon: ReactNode }> = [
  { key: 'research', label: '深入研究', icon: <Globe2 size={17} /> },
  { key: 'qa', label: '解题答疑', icon: <CircleHelp size={17} /> },
  { key: 'data', label: '数据分析', icon: <ChartColumn size={17} /> },
  { key: 'super', label: '超能模式', icon: <Sparkles size={17} /> },
  { key: 'ppt', label: 'PPT 生成', icon: <Presentation size={17} /> },
]

const imageRatios = ['1:1', '4:3', '3:4', '16:9', '9:16'] as const
const imageStyles = ['默认', '写实摄影', '商业海报', '插画', '3D 渲染', '国潮'] as const
const writingTypeOptions = ['公众号文章', '小红书笔记', '新闻稿', '短视频脚本', '商务文档', '社媒文案'] as const
const writingLengthOptions = ['短篇', '中等', '长篇', '深度长文'] as const
const codeLanguageOptions = ['自动识别', 'TypeScript', 'JavaScript', 'Python', 'C#', 'Java', 'Go', 'SQL'] as const
const codeTaskOptions = ['生成/修复', '解释代码', '排查报错', '重构优化', '写测试', '接口调试'] as const
const targetLanguages = ['中文（简体）', '英文', '日文', '韩文', '西班牙文'] as const
const translateModeOptions = ['自然表达', '忠实直译', '商务正式', '口语本地化'] as const
const researchDepthOptions = ['标准', '深度', '竞品分析', '资料综述', '行动方案'] as const
const qaModeOptions = ['逐步讲解', '只给答案', '先提示后答案', '举一反三'] as const
const dataOutputOptions = ['洞察+表格', '只要结论', '详细分析', '可视化建议', '清洗建议'] as const
type PptVideoSlide = { index: number; title: string; notes: string }
type PptVideoEncoderState = {
  encoder?: string
  mode?: string
  label?: string
  device?: string
}
type PptVideoSettings = {
  voice: string
  dubbingMode: string
  dialogueHostVoice: string
  dialogueGuestVoice: string
  dialogueNarratorVoice: string
  speed: string
  bgmName: string
  volume: number
  resolution: string
  secondsPerSlide: string
}
type PptSpecSlide = {
  title?: string
  subtitle?: string
  layout?: string
  bullets?: string[]
  points?: string[]
  visual?: string
  visualSuggestion?: string
  notes?: string
  narration?: string
}
type PptSpec = {
  title?: string
  subtitle?: string
  audience?: string
  theme?: string
  slides?: PptSpecSlide[]
}
const pptVideoVoiceOptions = [
  // ── 在线 Edge TTS（高品质在线语音）──
  { value: 'zh', label: '中文女声·晓晓（在线）' },
  { value: 'zh-m', label: '中文男声·云希（在线）' },
  { value: 'zh-news', label: '中文新闻·云扬（在线）' },
  { value: 'zh-story', label: '中文故事·晓伊（在线）' },
  { value: 'zh-gentle', label: '中文温柔·晓辰（在线）' },
  { value: 'zh-cheerful', label: '中文活泼·晓萱（在线）' },
  { value: 'zh-boy', label: '中文少年·云枫（在线）' },
  { value: 'zh-senior', label: '中文沉稳·云健（在线）' },
  { value: 'en', label: 'English Female · Jenny（在线）' },
  { value: 'en-m', label: 'English Male · Guy（在线）' },
  { value: 'en-aria', label: 'English · Aria（在线）' },
  { value: 'en-davis', label: 'English · Davis（在线）' },
  { value: 'en-gb', label: 'English UK · Sonia（在线）' },
  { value: 'en-gb-m', label: 'English UK · Ryan（在线）' },
  { value: 'ja', label: '日本語・七海（在线）' },
  { value: 'ja-m', label: '日本語・圭太（在线）' },
  { value: 'ko', label: '한국어 · 선히（在线）' },
  { value: 'ko-m', label: '한국어 · 인준（在线）' },
] as const
const pptVideoSpeedOptions = ['0.75x', '1.0x', '1.25x', '1.5x'] as const
const pptVideoResolutionOptions = [
  { value: '720p', label: '标清 720p（1280x720）' },
  { value: '1080p', label: '高清 1080p（1920x1080）' },
  { value: '2k', label: '超清 2K（2560x1440）' },
  { value: '480p', label: '流畅 480p（854x480）' },
] as const
const readPptVideoEncoder = (status: {
  videoEncoder?: string
  videoEncoderMode?: string
  videoEncoderLabel?: string
  videoEncoderDevice?: string
}): PptVideoEncoderState | null => {
  if (!status.videoEncoder && !status.videoEncoderLabel) return null
  return {
    encoder: status.videoEncoder,
    mode: status.videoEncoderMode,
    label: status.videoEncoderLabel,
    device: status.videoEncoderDevice,
  }
}

const getPptVideoProgressDetail = (phase: string, status: string) => {
  const trimmedPhase = phase.trim()
  const trimmedStatus = status.trim()
  if (!trimmedStatus) return ''
  if (trimmedPhase && trimmedStatus.startsWith(trimmedPhase)) {
    return trimmedStatus.slice(trimmedPhase.length).trim()
  }
  return trimmedStatus
}

const pptDialogueLinePattern = /^\s*(?:[-*]\s*)?([\p{L}\p{N}_\-\s]{1,24})\s*[:\uFF1A]\s*(.+)$/u

const normalizePptDialogueSpeaker = (speaker: string) =>
  speaker.replace(/[\[\]\u3010\u3011]/g, '').trim()

const detectPptDialogueSpeakers = (slides: PptVideoSlide[]) => {
  const seen = new Set<string>()
  const speakers: string[] = []
  for (const slide of slides) {
    for (const line of slide.notes.split(/\r?\n/)) {
      const match = line.match(pptDialogueLinePattern)
      if (!match) continue
      const speaker = normalizePptDialogueSpeaker(match[1])
      if (!speaker || seen.has(speaker)) continue
      seen.add(speaker)
      speakers.push(speaker)
    }
  }
  return speakers
}

const isPptDialogueScript = (notes: string) =>
  notes.split(/\r?\n/).some((line) => pptDialogueLinePattern.test(line))

const splitPptNarrationSentences = (notes: string) => notes
  .replace(/\r/g, '')
  .replace(/([\u3002\uFF01\uFF1F!?\uFF1B;])/gu, '$1\n')
  .split(/\n+/)
  .map((item) => item.trim())
  .filter(Boolean)

const createPptDialogueDraft = (notes: string) => {
  const trimmed = notes.trim()
  if (!trimmed || isPptDialogueScript(trimmed)) return notes

  const host = '\u4e3b\u6301\u4eba'
  const guest = '\u5609\u5bbe'
  const sentences = splitPptNarrationSentences(trimmed)
  if (sentences.length <= 1) return `${host}\uFF1A${trimmed}`

  return sentences
    .map((sentence, index) => `${index % 2 === 0 ? host : guest}\uFF1A${sentence}`)
    .join('\n')
}

const getPptDialogueVoiceForSpeaker = (
  speaker: string,
  index: number,
  settings: PptVideoSettings,
  roleVoices: Record<string, string> = {},
) => {
  const directVoice = roleVoices[speaker] || roleVoices[normalizePptDialogueSpeaker(speaker)]
  if (directVoice) return directVoice

  const normalized = normalizePptDialogueSpeaker(speaker).toLowerCase()
  if (normalized.includes('\u65c1\u767d') || normalized.includes('narrator')) return settings.dialogueNarratorVoice
  if (normalized.includes('\u4e3b\u6301') || normalized.includes('\u4e3b\u8bb2') || normalized.includes('host')) return settings.dialogueHostVoice
  if (normalized.includes('\u5609\u5bbe') || normalized.includes('\u540c\u4e8b') || normalized.includes('\u5ba2\u6237') || normalized.includes('guest')) return settings.dialogueGuestVoice
  return index % 2 === 0 ? settings.dialogueHostVoice : settings.dialogueGuestVoice
}

const isPptBuiltInDialogueSpeaker = (speaker: string) => {
  const normalized = normalizePptDialogueSpeaker(speaker).toLowerCase()
  return normalized.includes('\u65c1\u767d')
    || normalized.includes('narrator')
    || normalized.includes('\u4e3b\u6301')
    || normalized.includes('\u4e3b\u8bb2')
    || normalized.includes('host')
    || normalized.includes('\u5609\u5bbe')
    || normalized.includes('\u540c\u4e8b')
    || normalized.includes('\u5ba2\u6237')
    || normalized.includes('guest')
}

const buildPptVideoDialogueVoices = (
  slides: PptVideoSlide[],
  settings: PptVideoSettings,
  roleVoices: Record<string, string> = {},
) => {
  const voices: Record<string, string> = {
    ['\u4e3b\u6301\u4eba']: settings.dialogueHostVoice,
    ['\u4e3b\u8bb2\u4eba']: settings.dialogueHostVoice,
    Host: settings.dialogueHostVoice,
    ['\u65c1\u767d']: settings.dialogueNarratorVoice,
    Narrator: settings.dialogueNarratorVoice,
    ['\u5609\u5bbe']: settings.dialogueGuestVoice,
    ['\u540c\u4e8b']: settings.dialogueGuestVoice,
    ['\u5ba2\u6237']: settings.dialogueGuestVoice,
    Guest: settings.dialogueGuestVoice,
  }

  detectPptDialogueSpeakers(slides).forEach((speaker, index) => {
    voices[speaker] = getPptDialogueVoiceForSpeaker(speaker, index, settings, roleVoices)
  })
  return voices
}

const imageTemplates = [
  { value: 'none', label: '模板', prompt: '' },
  { value: 'cover', label: '封面图', prompt: '生成一张适合中文内容平台的封面图，主体明确，画面有传播感。' },
  { value: 'poster', label: '营销海报', prompt: '生成一张商业营销海报，构图简洁，突出核心产品或主题。' },
  { value: 'scene', label: '场景图', prompt: '生成一张真实场景图，人物、环境和光线自然，有生活细节。' },
] as const

const capabilityLabels: Record<CapabilityKey, string> = {
  quick: '快速',
  write: '帮我写作',
  image: '图像生成',
  code: '编程',
  translate: '翻译',
  research: '深入研究',
  qa: '解题答疑',
  data: '数据分析',
  super: '超能模式',
  ppt: 'PPT 生成',
}

const capabilityIcons: Record<CapabilityKey, ReactNode> = {
  quick: <Zap size={17} />,
  write: <FileText size={17} />,
  image: <ImagePlus size={17} />,
  code: <Braces size={17} />,
  translate: <Languages size={17} />,
  research: <Globe2 size={17} />,
  qa: <CircleHelp size={17} />,
  data: <ChartColumn size={17} />,
  super: <Sparkles size={17} />,
  ppt: <Presentation size={17} />,
}

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

const acceptedAttachmentExtensions = [
  '.png', '.jpg', '.jpeg', '.webp', '.gif',
  '.pdf', '.doc', '.docx', '.xls', '.xlsx', '.csv', '.ppt', '.pptx',
  '.txt', '.md', '.json',
  '.js', '.jsx', '.ts', '.tsx', '.py', '.cs', '.java', '.go', '.rs', '.c', '.cpp', '.h', '.sql', '.html', '.css',
]
const acceptedAttachmentTypes = [
  'image/png',
  'image/jpeg',
  'image/webp',
  'image/gif',
  'application/pdf',
  'application/msword',
  'application/vnd.openxmlformats-officedocument.wordprocessingml.document',
  'application/vnd.ms-excel',
  'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet',
  'text/csv',
  'application/vnd.ms-powerpoint',
  'application/vnd.openxmlformats-officedocument.presentationml.presentation',
  'text/plain',
  'text/markdown',
  'application/json',
]
const attachmentAccept = [...acceptedAttachmentTypes, ...acceptedAttachmentExtensions].join(',')
const maxAttachmentSize = 10 * 1024 * 1024
const maxAttachmentCount = 8
const maxAttachmentTotalSize = 30 * 1024 * 1024

const formatFileSize = (size: number) => {
  if (size >= 1024 * 1024) return `${(size / 1024 / 1024).toFixed(size >= 10 * 1024 * 1024 ? 0 : 1)}MB`
  if (size >= 1024) return `${Math.max(1, Math.round(size / 1024))}KB`
  return `${size}B`
}

const getFileExtension = (fileName: string) => {
  const index = fileName.lastIndexOf('.')
  return index >= 0 ? fileName.slice(index).toLowerCase() : ''
}

const sanitizeDownloadFileName = (value: string, extension: 'docx' | 'pptx' | 'mp4') => {
  const withoutExtension = value.replace(/\.[^.]+$/, '')
  const stem = withoutExtension
    .normalize('NFKC')
    .replace(/[\\/:*?"<>|“”‘’«»]+/g, ' ')
    .replace(/[^\p{L}\p{N}\s_-]/gu, ' ')
    .replace(/\s+/g, ' ')
    .trim()
    .slice(0, 48)
    .trim() || 'VeraMedia'
  return `${stem}.${extension}`
}

const readDownloadFileName = (response: Response, extension: 'docx' | 'pptx' | 'mp4', fallbackTitle: string) => {
  const disposition = response.headers.get('content-disposition') || ''
  const encoded = disposition.match(/filename\*\s*=\s*(?:UTF-8'')?([^;]+)/i)?.[1]
  const plain = disposition.match(/filename\s*=\s*"?([^";]+)"?/i)?.[1]
  const rawName = encoded || plain || fallbackTitle
  const unquoted = rawName.trim().replace(/^["']|["']$/g, '')
  try {
    return sanitizeDownloadFileName(decodeURIComponent(unquoted), extension)
  } catch {
    return sanitizeDownloadFileName(unquoted, extension)
  }
}

const getPptSpecTitle = (content: string) =>
  content.match(/"title"\s*:\s*"([^"]{2,120})"/)?.[1]?.trim() || ''

const getAttachmentKind = (file: Pick<Attachment, 'fileName' | 'contentType'>) => {
  const ext = getFileExtension(file.fileName)
  if (file.contentType.startsWith('image/') || ['.png', '.jpg', '.jpeg', '.webp', '.gif'].includes(ext)) return 'Image'
  if (ext === '.pdf' || file.contentType === 'application/pdf') return 'PDF'
  if (['.doc', '.docx'].includes(ext)) return 'Word'
  if (['.xls', '.xlsx', '.csv'].includes(ext)) return 'Sheet'
  if (['.ppt', '.pptx'].includes(ext)) return 'PPT'
  if (['.js', '.jsx', '.ts', '.tsx', '.py', '.cs', '.java', '.go', '.rs', '.c', '.cpp', '.h', '.sql', '.html', '.css'].includes(ext)) return 'Code'
  if (['.txt', '.md', '.json'].includes(ext)) return 'Text'
  return 'File'
}

const isSupportedAttachment = (file: File) => {
  const ext = getFileExtension(file.name)
  return acceptedAttachmentExtensions.includes(ext) || acceptedAttachmentTypes.includes(file.type)
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
  const [isThinkingMenuOpen, setIsThinkingMenuOpen] = useState(false)
  const [openToolbarSelect, setOpenToolbarSelect] = useState<string | null>(null)
  const [toolbarSelectPosition, setToolbarSelectPosition] = useState<{ left: number; bottom: number; minWidth: number } | null>(null)
  const [thinkingMenuPosition, setThinkingMenuPosition] = useState<{ left: number; bottom: number; minWidth: number } | null>(null)
  const [isComposerDragging, setIsComposerDragging] = useState(false)
  const [showScrollToBottom, setShowScrollToBottom] = useState(false)

  const [activePage, setActivePage] = useState<'chat' | 'admin' | 'editor' | 'tasks' | 'assets' | 'images' | 'pptVideo'>('chat')
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
  const [pptVideoBusy, setPptVideoBusy] = useState(false)
  const [pptVideoPreviewBusy, setPptVideoPreviewBusy] = useState(false)
  const [pptVideoDialogueBusy, setPptVideoDialogueBusy] = useState(false)
  const [pptVideoStatus, setPptVideoStatus] = useState('')
  const [pptVideoPhase, setPptVideoPhase] = useState('')
  const [pptVideoFile, setPptVideoFile] = useState<File | null>(null)
  const [pptVideoBgmFile, setPptVideoBgmFile] = useState<File | null>(null)
  const [pptVideoSlides, setPptVideoSlides] = useState<PptVideoSlide[]>([])
  const [pptVideoPreviewId, setPptVideoPreviewId] = useState<string | null>(null)
  const [pptVideoProgress, setPptVideoProgress] = useState(0)
  const [pptVideoLightboxSrc, setPptVideoLightboxSrc] = useState<string | null>(null)
  const [voiceSamplePlaying, setVoiceSamplePlaying] = useState(false)
  const voiceSampleRef = useRef<HTMLAudioElement | null>(null)
  const [pptVideoDownload, setPptVideoDownload] = useState<{ url: string; name: string } | null>(null)
  const [pptVideoEncoder, setPptVideoEncoder] = useState<PptVideoEncoderState | null>(null)
  const [pptVideoElapsed, setPptVideoElapsed] = useState(0)
  const [pptVideoSettings, setPptVideoSettings] = useState<PptVideoSettings>({
    voice: 'zh',
    dubbingMode: 'single',
    dialogueHostVoice: 'zh',
    dialogueGuestVoice: 'zh-m',
    dialogueNarratorVoice: 'zh-story',
    speed: '1.0x',
    bgmName: '',
    volume: 30,
    resolution: '720p',
    secondsPerSlide: '5',
  })
  const [pptVideoRoleVoices, setPptVideoRoleVoices] = useState<Record<string, string>>({})
  const pptVideoProgressDetail = getPptVideoProgressDetail(pptVideoPhase, pptVideoStatus)
  const pptVideoProgressInlineDetail = /^\([^)]*\)$/.test(pptVideoProgressDetail) ? pptVideoProgressDetail : ''
  const pptVideoProgressMessage = pptVideoProgressInlineDetail
    ? ''
    : pptVideoProgressDetail || (!pptVideoPhase && !pptVideoStatus ? '就绪，等待操作...' : '')
  const pptVideoProgressTitle = `${pptVideoPhase || '处理进度'}${pptVideoProgressInlineDetail ? ` ${pptVideoProgressInlineDetail}` : ''}`
  const pptVideoDialogueSpeakers = detectPptDialogueSpeakers(pptVideoSlides)
  const pptVideoCustomDialogueSpeakers = pptVideoDialogueSpeakers.filter((speaker) => !isPptBuiltInDialogueSpeaker(speaker))
  const pptVideoCustomDialogueSpeakerKey = pptVideoCustomDialogueSpeakers.join('\u0001')
  const pptVideoDialogueVoiceEntries = pptVideoCustomDialogueSpeakers.map((speaker, index) => ({
    speaker,
    voice: getPptDialogueVoiceForSpeaker(speaker, index, pptVideoSettings, pptVideoRoleVoices),
  }))
  useEffect(() => {
    setPptVideoRoleVoices((current) => {
      const active = new Set(pptVideoCustomDialogueSpeakers)
      let changed = false
      const next: Record<string, string> = {}
      for (const [speaker, voice] of Object.entries(current)) {
        if (!active.has(speaker)) {
          changed = true
          continue
        }
        next[speaker] = voice
      }
      return changed ? next : current
    })
  }, [pptVideoCustomDialogueSpeakerKey])
  const [articleVersions, setArticleVersions] = useState<ArticleVersion[] | null>(null)
  const [articleVersionTitle, setArticleVersionTitle] = useState('')
  const [articleVersionProjectId, setArticleVersionProjectId] = useState<number | null>(null)
  const [articleVersionsLoading, setArticleVersionsLoading] = useState(false)
  const [restoringVersionId, setRestoringVersionId] = useState<number | null>(null)
  const [agentOptions, setAgentOptions] = useState({
    thinkingMode: 'quick' as ThinkingMode,
    platform: 'wechat',
    outputFormat: 'article',
    temperature: 0.7,
    imageCount: 3,
    enableWebSearch: true,
    showThinking: true,
    intentMode: 'auto' as IntentMode,
    stylePreset: 'balanced' as StylePreset,
    imageRatio: '1:1',
    imageStyle: '默认',
    imageTemplate: 'none',
    capability: 'quick' as CapabilityKey,
    capabilityParams: {
      writingType: '公众号文章',
      writingLength: '中等',
      codeLanguage: '自动识别',
      codeTask: '生成/修复',
      targetLanguage: '英文',
      translateMode: '自然表达',
      researchDepth: '标准',
      qaMode: '逐步讲解',
      dataOutput: '洞察+表格',
    } as Record<string, string>,
  })

  const bottomRef = useRef<HTMLDivElement | null>(null)
  const messagesRef = useRef<HTMLDivElement | null>(null)
  const composerRef = useRef<HTMLFormElement | null>(null)
  const chatPaneRef = useRef<HTMLElement | null>(null)
  const savedScrollRef = useRef<number | null>(null)
  const chatScrollTopRef = useRef<number | null>(null)
  const shouldRestoreChatScrollRef = useRef(false)
  const shouldAnchorChatBottomRef = useRef(false)
  const isChatAtBottomRef = useRef(true)
  const prevMessageCountRef = useRef(0)
  const richEditorRef = useRef<RichEditorHandle | null>(null)
  const sendingRef = useRef(false)
  const recoverRunningJobsRef = useRef<(id?: number | null) => void>(() => undefined)
  const ensureJobMessageRef = useRef<(job: GenerationJob) => void>(() => undefined)
  const connectGenerationJobRef = useRef<(job: GenerationJob) => void>(() => undefined)
  const [composerMetrics, setComposerMetrics] = useState({ height: 126, centerX: 0 })

  useEffect(() => { conversationIdRef.current = conversationId }, [conversationId])
  useEffect(() => { previewMessageRef.current = previewMessage }, [previewMessage])
  useEffect(() => () => {
    if (pptVideoDownload) window.URL.revokeObjectURL(pptVideoDownload.url)
  }, [pptVideoDownload])

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
    if (activePage === 'chat' && !isSettingsOpen && (added || isStreaming) && isChatAtBottomRef.current) {
      bottomRef.current?.scrollIntoView({ behavior: 'smooth' })
    }
  }, [messages, isStreaming, activePage, isSettingsOpen])

  const updateChatBottomState = useCallback(() => {
    const node = messagesRef.current
    if (!node) return
    const distance = node.scrollHeight - node.scrollTop - node.clientHeight
    const atBottom = distance < 96
    isChatAtBottomRef.current = atBottom
    setShowScrollToBottom(!atBottom)
  }, [])

  useEffect(() => {
    if (activePage !== 'chat') return
    updateChatBottomState()
  }, [activePage, messages.length, updateChatBottomState])

  useEffect(() => {
    if (activePage !== 'chat') return
    const composerNode = composerRef.current
    const paneNode = chatPaneRef.current
    if (!composerNode || !paneNode) return

    const update = () => {
      const composerRect = composerNode.getBoundingClientRect()
      const paneRect = paneNode.getBoundingClientRect()
      const cardRect = composerNode.querySelector<HTMLElement>('.composer-card')?.getBoundingClientRect() ?? composerRect
      const next = {
        height: Math.ceil(composerRect.height),
        centerX: Math.round(cardRect.left + cardRect.width / 2 - paneRect.left),
      }
      setComposerMetrics((current) => (
        Math.abs(current.height - next.height) > 1 || Math.abs(current.centerX - next.centerX) > 1 ? next : current
      ))
    }
    update()
    const observer = new ResizeObserver(update)
    observer.observe(composerNode)
    const cardNode = composerNode.querySelector<HTMLElement>('.composer-card')
    if (cardNode) observer.observe(cardNode)
    window.addEventListener('resize', update)
    return () => {
      observer.disconnect()
      window.removeEventListener('resize', update)
    }
  }, [activePage])

  function scrollChatToBottom() {
    bottomRef.current?.scrollIntoView({ behavior: 'smooth' })
    isChatAtBottomRef.current = true
    setShowScrollToBottom(false)
  }

  const anchorChatToBottom = useCallback(() => {
    const node = messagesRef.current
    if (!node) return
    node.scrollTop = node.scrollHeight
    isChatAtBottomRef.current = true
    setShowScrollToBottom(false)
  }, [])

  const requestAnchorChatToBottom = useCallback(() => {
    shouldAnchorChatBottomRef.current = true
  }, [])

  const rememberChatScroll = useCallback(() => {
    if (messagesRef.current) {
      chatScrollTopRef.current = messagesRef.current.scrollTop
    }
  }, [])

  const requestRestoreChatScroll = useCallback(() => {
    shouldRestoreChatScrollRef.current = true
  }, [])

  useLayoutEffect(() => {
    if (activePage !== 'chat' || !shouldAnchorChatBottomRef.current) return
    shouldAnchorChatBottomRef.current = false
    anchorChatToBottom()
    const frame = window.requestAnimationFrame(anchorChatToBottom)
    const timers = [80, 260, 700].map((delay) => window.setTimeout(anchorChatToBottom, delay))
    return () => {
      window.cancelAnimationFrame(frame)
      timers.forEach((timer) => window.clearTimeout(timer))
    }
  }, [activePage, messages.length, anchorChatToBottom])

  useLayoutEffect(() => {
    if (activePage !== 'chat' || !shouldRestoreChatScrollRef.current) return
    shouldRestoreChatScrollRef.current = false
    if (shouldAnchorChatBottomRef.current) return
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
    requestAnchorChatToBottom()
    setConversationId(id)
    setMessages(await request<Message[]>(`/api/conversations/${id}/messages`))
    recoverRunningJobsRef.current(id)
  }, [request, requestAnchorChatToBottom])

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
    const incoming = Array.from(files)
    const remainingSlots = maxAttachmentCount - attachments.length
    if (remainingSlots <= 0) {
      showToast(`最多上传 ${maxAttachmentCount} 个附件`)
      return
    }

    const supported = incoming.filter(isSupportedAttachment)
    const rejectedTypeCount = incoming.length - supported.length
    const sized = supported.filter((file) => file.size <= maxAttachmentSize)
    const rejectedSizeCount = supported.length - sized.length
    const selected = sized.slice(0, remainingSlots)
    const currentTotal = attachments.reduce((sum, file) => sum + file.size, 0)
    const accepted: File[] = []
    let nextTotal = currentTotal
    for (const file of selected) {
      if (nextTotal + file.size > maxAttachmentTotalSize) break
      accepted.push(file)
      nextTotal += file.size
    }

    if (rejectedTypeCount > 0) showToast('已跳过不支持的文件格式')
    if (rejectedSizeCount > 0) showToast(`单个附件不能超过 ${formatFileSize(maxAttachmentSize)}`)
    if (sized.length > selected.length) showToast(`最多上传 ${maxAttachmentCount} 个附件`)
    if (selected.length > accepted.length) showToast(`附件总大小不能超过 ${formatFileSize(maxAttachmentTotalSize)}`)
    if (accepted.length === 0) return

    setUploadStatus('上传中...')
    try {
      const uploaded = await uploadAttachments(accepted)
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
    chatScrollTopRef.current = null
    shouldRestoreChatScrollRef.current = false
    requestAnchorChatToBottom()
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

  function openPptVideoPage() {
    rememberChatScroll()
    setIsMobileNavOpen(false)
    setActivePage('pptVideo')
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

    setIsToolMenuOpen(false)
    setIsThinkingMenuOpen(false)
    setAgentOptions((current) => {
      const next = { ...current, capability: key }
      if (key === 'quick') {
        next.intentMode = 'auto'
        next.outputFormat = 'article'
        next.thinkingMode = 'quick'
        next.enableWebSearch = true
      }
      if (key === 'write') {
        next.intentMode = 'article'
        next.outputFormat = 'article'
      }
      if (key === 'image') next.intentMode = 'image'
      if (key === 'code') next.intentMode = 'code' as IntentMode
      if (key === 'translate') next.intentMode = 'translate' as IntentMode
      if (key === 'research') next.intentMode = 'research' as IntentMode
      if (key === 'qa') next.intentMode = 'chat'
      if (key === 'data') next.intentMode = 'table' as IntentMode
      if (key === 'ppt') {
        next.intentMode = 'document'
        next.outputFormat = 'pptx'
        next.thinkingMode = 'expert'
        next.temperature = Math.max(next.temperature || 0.7, 0.78)
        next.capabilityParams = { ...next.capabilityParams }
        delete next.capabilityParams.pptPages
        delete next.capabilityParams.pptAudience
        delete next.capabilityParams.pptDesign
        delete next.capabilityParams.pptMode
        delete next.capabilityParams.pptNarration
      }
      if (key === 'research' || key === 'super') {
        if (key === 'super') next.intentMode = 'document'
        next.thinkingMode = key === 'super' ? 'expert' : 'think'
        next.enableWebSearch = true
      }
      return next
    })
    setDraft(source)
  }

  function leaveImageMode() {
    setAgentOptions((current) => ({ ...current, intentMode: 'auto', capability: 'quick' }))
  }

  function leaveCapabilityMode() {
    setIsThinkingMenuOpen(false)
    setThinkingMenuPosition(null)
    setAgentOptions((current) => ({ ...current, intentMode: 'auto', capability: 'quick', outputFormat: 'article', thinkingMode: 'quick', enableWebSearch: true }))
  }

  function updateCapabilityParam(key: string, value: string) {
    setAgentOptions((current) => ({
      ...current,
      capabilityParams: {
        ...current.capabilityParams,
        [key]: value,
      },
    }))
  }

  function selectThinkingMode(mode: ThinkingMode) {
    setIsThinkingMenuOpen(false)
    setThinkingMenuPosition(null)
    setAgentOptions((current) => ({ ...current, thinkingMode: mode }))
  }

  function renderThinkingSelector() {
    const mode = isThinkingMode(agentOptions.thinkingMode) ? agentOptions.thinkingMode : 'quick'
    const selected = thinkingModes.find((item) => item.key === mode) ?? thinkingModes[0]

    return (
      <div className="thinking-mode-control">
        <button
          className={isThinkingMenuOpen ? 'capability-button active' : 'capability-button'}
          type="button"
          onClick={(event) => {
            const closing = isThinkingMenuOpen
            setIsToolMenuOpen(false)
            setOpenToolbarSelect(null)
            setToolbarSelectPosition(null)
            if (closing) {
              setIsThinkingMenuOpen(false)
              setThinkingMenuPosition(null)
              return
            }
            const rect = event.currentTarget.getBoundingClientRect()
            setThinkingMenuPosition({
              left: Math.round(rect.left + rect.width / 2),
              bottom: Math.round(window.innerHeight - rect.top + 10),
              minWidth: Math.max(216, Math.round(rect.width + 112)),
            })
            setIsThinkingMenuOpen(true)
          }}
          title="选择思考层级"
        >
          {selected.icon}
          {thinkingModeLabels[mode]}
          <ChevronDown className="thinking-mode-chevron" size={15} />
        </button>
        {isThinkingMenuOpen && (
          <div
            className="thinking-mode-menu"
            style={thinkingMenuPosition ? ({
              left: `${thinkingMenuPosition.left}px`,
              bottom: `${thinkingMenuPosition.bottom}px`,
              minWidth: `${thinkingMenuPosition.minWidth}px`,
            } as CSSProperties) : undefined}
          >
            {thinkingModes.map((item) => (
              <button className={mode === item.key ? 'active' : ''} type="button" key={item.key} onClick={() => selectThinkingMode(item.key)}>
                {item.icon}
                <span>
                  <strong>{item.label}</strong>
                  <small>{item.description}</small>
                </span>
                {mode === item.key && <Check size={17} />}
              </button>
            ))}
          </div>
        )}
      </div>
    )
  }

  function renderToolbarSelect(
    id: string,
    value: string,
    options: readonly (string | { value: string; label: string })[],
    onChange: (value: string) => void,
    icon?: ReactNode,
    title?: string,
  ) {
    const isOpen = openToolbarSelect === id
    const normalizedOptions = options.map((option) => typeof option === 'string' ? { value: option, label: option } : option)
    const displayValue = normalizedOptions.find((option) => option.value === value)?.label ?? value
    const openMenu = (event: ReactMouseEvent<HTMLButtonElement>) => {
      const closing = openToolbarSelect === id
      setIsToolMenuOpen(false)
      setIsThinkingMenuOpen(false)
      setThinkingMenuPosition(null)
      if (closing) {
        setOpenToolbarSelect(null)
        setToolbarSelectPosition(null)
        return
      }
      const rect = event.currentTarget.getBoundingClientRect()
      setToolbarSelectPosition({
        left: Math.round(rect.left + rect.width / 2),
        bottom: Math.round(window.innerHeight - rect.top + 10),
        minWidth: Math.max(156, Math.round(rect.width + 44)),
      })
      setOpenToolbarSelect(id)
    }

    return (
      <span className="toolbar-select" title={title}>
        <button
          className={isOpen ? 'toolbar-select-trigger active' : 'toolbar-select-trigger'}
          type="button"
          onClick={openMenu}
        >
          {icon}
          <strong>{displayValue}</strong>
          <ChevronDown size={15} />
        </button>
        {isOpen && (
          <div
            className="toolbar-select-menu"
            style={toolbarSelectPosition ? ({
              left: `${toolbarSelectPosition.left}px`,
              bottom: `${toolbarSelectPosition.bottom}px`,
              minWidth: `${toolbarSelectPosition.minWidth}px`,
            } as CSSProperties) : undefined}
          >
            {normalizedOptions.map((option) => (
              <button
                className={option.value === value ? 'active' : ''}
                type="button"
                key={option.value}
                onClick={() => {
                  onChange(option.value)
                  setOpenToolbarSelect(null)
                  setToolbarSelectPosition(null)
                }}
              >
                <span>{option.label}</span>
                {option.value === value && <Check size={15} />}
              </button>
            ))}
          </div>
        )}
      </span>
    )
  }

  function applyImageTemplate(value: string) {
    const template = imageTemplates.find((item) => item.value === value)
    setAgentOptions((current) => ({ ...current, imageTemplate: value }))
    if (!template?.prompt) return
    setDraft((current) => current.trim() ? `${template.prompt}\n\n${current}` : template.prompt)
  }

  function renderCapabilityTools() {
    const capability = agentOptions.capability
    const params = agentOptions.capabilityParams
    if (capability === 'image') {
      return (
        <div className="capability-row image-tool-row" aria-label="图像生成设置">
          <span className="image-mode-chip">
            <ImagePlus size={17} />
            图像生成
            <button type="button" title="退出图像生成" onClick={leaveImageMode}><X size={14} /></button>
          </span>
          <label className="capability-button image-reference-button" title="上传参考图">
            <LinkIcon size={17} />
            参考图
            <input type="file" accept="image/*" multiple onChange={(e) => uploadFiles(e.target.files)} />
          </label>
          {renderToolbarSelect(
            'image-model',
            provider.imageModelName || 'Seedream 4.5',
            [provider.imageModelName || 'Seedream 4.5'],
            () => undefined,
            <Sparkles size={17} />,
            '当前图像模型',
          )}
          {renderToolbarSelect(
            'image-ratio',
            agentOptions.imageRatio,
            imageRatios,
            (imageRatio) => setAgentOptions({ ...agentOptions, imageRatio }),
            <PanelRightOpen size={17} />,
            '图片比例',
          )}
          {renderToolbarSelect(
            'image-style',
            agentOptions.imageStyle,
            imageStyles,
            (imageStyle) => setAgentOptions({ ...agentOptions, imageStyle }),
            <Sparkles size={17} />,
            '图片风格',
          )}
          {renderToolbarSelect(
            'image-template',
            agentOptions.imageTemplate,
            imageTemplates.map((template) => ({ value: template.value, label: template.label })),
            applyImageTemplate,
            undefined,
            '图片模板',
          )}
        </div>
      )
    }

    if (capability === 'quick') return null

    if (capability === 'write') {
      return (
        <div className="capability-row image-tool-row" aria-label="帮我写作设置">
          <span className="image-mode-chip">
            <FileText size={17} />
            帮我写作
            <button type="button" title="退出帮我写作" onClick={leaveCapabilityMode}><X size={14} /></button>
          </span>
          {renderToolbarSelect(
            'writing-type',
            params.writingType,
            writingTypeOptions,
            (value) => updateCapabilityParam('writingType', value),
            <FileText size={17} />,
            '写作类型',
          )}
          {renderToolbarSelect(
            'writing-length',
            params.writingLength,
            writingLengthOptions,
            (value) => updateCapabilityParam('writingLength', value),
            <List size={17} />,
            '篇幅',
          )}
          {renderThinkingSelector()}
          <label className="capability-button image-reference-button" title="上传文件">
            <LinkIcon size={17} />
            上传文件
            <input type="file" accept={attachmentAccept} multiple onChange={(e) => uploadFiles(e.target.files)} />
          </label>
        </div>
      )
    }

    if (capability === 'code') {
      return (
        <div className="capability-row image-tool-row" aria-label="编程设置">
          <span className="image-mode-chip">
            <Braces size={17} />
            编程
            <button type="button" title="退出编程" onClick={leaveCapabilityMode}><X size={14} /></button>
          </span>
          <label className="capability-button image-reference-button" title="上传文件">
            <LinkIcon size={17} />
            上传文件
            <input type="file" accept={attachmentAccept} multiple onChange={(e) => uploadFiles(e.target.files)} />
          </label>
          {renderToolbarSelect(
            'code-language',
            params.codeLanguage,
            codeLanguageOptions,
            (value) => updateCapabilityParam('codeLanguage', value),
            <Braces size={17} />,
            '代码语言',
          )}
          {renderToolbarSelect(
            'code-task',
            params.codeTask,
            codeTaskOptions,
            (value) => updateCapabilityParam('codeTask', value),
            <List size={17} />,
            '任务类型',
          )}
          {renderThinkingSelector()}
        </div>
      )
    }

    if (capability === 'translate') {
      return (
        <div className="capability-row image-tool-row" aria-label="翻译设置">
          <span className="image-mode-chip">
            <Languages size={17} />
            翻译
            <button type="button" title="退出翻译" onClick={leaveCapabilityMode}><X size={14} /></button>
          </span>
          {renderToolbarSelect(
            'target-language',
            params.targetLanguage,
            targetLanguages,
            (value) => updateCapabilityParam('targetLanguage', value),
            <Languages size={17} />,
            '目标语言',
          )}
          {renderToolbarSelect(
            'translate-mode',
            params.translateMode,
            translateModeOptions,
            (value) => updateCapabilityParam('translateMode', value),
            <FileText size={17} />,
            '翻译风格',
          )}
        </div>
      )
    }

    if (capability === 'research') {
      return (
        <div className="capability-row image-tool-row" aria-label="深入研究设置">
          <span className="image-mode-chip">
            <Globe2 size={17} />
            深入研究
            <button type="button" title="退出深入研究" onClick={leaveCapabilityMode}><X size={14} /></button>
          </span>
          {renderToolbarSelect(
            'research-depth',
            params.researchDepth,
            researchDepthOptions,
            (value) => updateCapabilityParam('researchDepth', value),
            <Globe2 size={17} />,
            '研究深度',
          )}
          {renderThinkingSelector()}
        </div>
      )
    }

    if (capability === 'qa') {
      return (
        <div className="capability-row image-tool-row" aria-label="解题答疑设置">
          <span className="image-mode-chip">
            <CircleHelp size={17} />
            解题答疑
            <button type="button" title="退出解题答疑" onClick={leaveCapabilityMode}><X size={14} /></button>
          </span>
          <label className="capability-button image-reference-button" title="上传题目图片">
            <LinkIcon size={17} />
            上传题目图片
            <input type="file" accept="image/*" multiple onChange={(e) => uploadFiles(e.target.files)} />
          </label>
          {renderToolbarSelect(
            'qa-mode',
            params.qaMode,
            qaModeOptions,
            (value) => updateCapabilityParam('qaMode', value),
            <CircleHelp size={17} />,
            '答疑方式',
          )}
          {renderThinkingSelector()}
        </div>
      )
    }

    if (capability === 'data') {
      return (
        <div className="capability-row image-tool-row" aria-label="数据分析设置">
          <span className="image-mode-chip">
            <ChartColumn size={17} />
            数据分析
            <button type="button" title="退出数据分析" onClick={leaveCapabilityMode}><X size={14} /></button>
          </span>
          {renderThinkingSelector()}
          {renderToolbarSelect(
            'data-output',
            params.dataOutput,
            dataOutputOptions,
            (value) => updateCapabilityParam('dataOutput', value),
            <ChartColumn size={17} />,
            '输出方式',
          )}
          <label className="capability-button image-reference-button" title="上传文件">
            <LinkIcon size={17} />
            上传文件
            <input type="file" accept={attachmentAccept} multiple onChange={(e) => uploadFiles(e.target.files)} />
          </label>
        </div>
      )
    }

    if (capability === 'super') {
      return (
        <div className="capability-row image-tool-row" aria-label="超能模式设置">
          <span className="image-mode-chip">
            <Sparkles size={17} />
            超能模式
            <small>Beta</small>
            <button type="button" title="退出超能模式" onClick={leaveCapabilityMode}><X size={14} /></button>
          </span>
          <label className="capability-button image-reference-button" title="上传文件">
            <LinkIcon size={17} />
            上传文件
            <input type="file" accept={attachmentAccept} multiple onChange={(e) => uploadFiles(e.target.files)} />
          </label>
          {renderThinkingSelector()}
        </div>
      )
    }

    if (capability === 'ppt') {
      return (
        <div className="capability-row image-tool-row" aria-label="PPT AI 定制">
          <span className="image-mode-chip">
            <Presentation size={17} />
            PPT AI 定制
            <button type="button" title="退出 PPT 生成" onClick={leaveCapabilityMode}><X size={14} /></button>
          </span>
          <label className="capability-button image-reference-button" title="上传文件">
            <LinkIcon size={17} />
            上传文件
            <input type="file" accept={attachmentAccept} multiple onChange={(e) => uploadFiles(e.target.files)} />
          </label>
        </div>
      )
    }

    return (
      <div className="capability-row image-tool-row" aria-label={`${capabilityLabels[capability]}设置`}>
        <span className="image-mode-chip">
          {capabilityIcons[capability]}
          {capabilityLabels[capability]}
          <button type="button" title="退出当前能力" onClick={leaveCapabilityMode}><X size={14} /></button>
        </span>
      </div>
    )
  }

  function renderAttachmentPreview(file: Attachment) {
    const kind = getAttachmentKind(file)
    const isImage = kind === 'Image'
    const icon = kind === 'Image' ? <ImagePlus size={22} />
      : kind === 'PPT' ? <Presentation size={22} />
      : kind === 'Sheet' ? <TableIcon size={22} />
      : kind === 'Code' ? <Braces size={22} />
      : <FileText size={22} />

    return (
      <article className="attachment-card" key={file.url}>
        <div className={isImage ? 'attachment-thumb image' : 'attachment-thumb'}>
          {isImage ? <img src={file.url} alt={file.fileName} /> : icon}
        </div>
        <div className="attachment-info">
          <strong title={file.fileName}>{file.fileName}</strong>
          <span>{kind} · {formatFileSize(file.size)}</span>
        </div>
        <button type="button" title="移除附件" onClick={() => setAttachments((current) => current.filter((item) => item.url !== file.url))}>
          <X size={14} />
        </button>
      </article>
    )
  }

  async function submitMessage(content: string) {
    const normalizedContent = content || buildAttachmentOnlyPrompt()
    if (!normalizedContent || isStreaming || sendingRef.current) return

    sendingRef.current = true
    setDraft('')
    try {
      const requestOptions = agentOptions.capability === 'ppt'
        ? {
            ...agentOptions,
            capabilityParams: Object.fromEntries(
              Object.entries(agentOptions.capabilityParams).filter(([key]) => !key.startsWith('ppt')),
            ),
          }
        : agentOptions
      const job = await request<GenerationJob>('/api/conversations/jobs', {
        method: 'POST',
        body: JSON.stringify({ content: normalizedContent, conversationId, attachments, options: requestOptions }),
      })
      setConversationId(job.conversationId)
      setStreamingMessageId(job.assistantMessageId)
      setMessages((current) => [
        ...current,
        { id: job.userMessageId, role: 'user', content: normalizedContent, createdAt: new Date().toISOString() },
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

  function buildAttachmentOnlyPrompt() {
    if (attachments.length === 0) return ''
    const names = attachments.map((item) => item.fileName).join('、')
    switch (agentOptions.capability) {
      case 'data':
        return `请分析我上传的数据文件：${names}。输出关键指标、异常点、趋势洞察和下一步建议。`
      case 'write':
        return `请基于我上传的资料写作成文：${names}。`
      case 'code':
        return `请分析我上传的代码或技术文件：${names}，指出问题并给出修改建议。`
      case 'translate':
        return `请将我上传文件中的主要内容翻译为${agentOptions.capabilityParams.targetLanguage || '目标语言'}：${names}。`
      case 'qa':
        return `请解答我上传的题目图片或文件：${names}。`
      case 'research':
        return `请基于我上传的资料做深入研究和结构化整理：${names}。`
      case 'ppt':
        return `请基于我上传的资料制作一份由 AI 自主定制的成品级 PPT：${names}。`
      case 'image':
        return `请参考我上传的图片生成新图片：${names}。`
      default:
        return `请处理我上传的附件：${names}。`
    }
  }

  async function retryFromMessage(message: Message) {
    await submitMessage(message.content)
  }

  async function readErrorMessage(response: Response, fallback: string) {
    const contentType = response.headers.get('content-type') || ''
    const prefix = `${fallback}（HTTP ${response.status}${response.statusText ? ` ${response.statusText}` : ''}）`
    if (contentType.includes('application/json')) {
      const body = await response.json().catch(() => ({}))
      const parts = [
        typeof body.message === 'string' ? body.message : '',
        typeof body.requestId === 'string' && body.requestId ? `诊断编号：${body.requestId}` : '',
        typeof body.stage === 'string' && body.stage ? `失败阶段：${body.stage}` : '',
        typeof body.stderr === 'string' && body.stderr ? `诊断日志：\n${body.stderr}` : '',
        typeof body.detail === 'string' && body.detail ? `详情：\n${body.detail}` : '',
      ].filter((item) => item.trim())
      return parts.length > 0 ? `${prefix}\n\n${parts.join('\n\n')}` : prefix
    }

    const text = await response.text().catch(() => '')
    return text.trim() ? `${prefix}\n\n${text.trim()}` : prefix
  }

  async function downloadMessageExport(message: Message, format: 'docx' | 'pptx' | 'mp4') {
    const cid = conversationIdRef.current
    if (!cid || isMessageGenerating(message)) return

    try {
      if (format === 'mp4') {
        await downloadMessageVideo(message)
        return
      }

      const response = await fetchWithAuth(`${API_BASE}/api/conversations/${cid}/messages/${message.id}/export?format=${format}`)
      if (!response.ok) {
        throw new Error(await readErrorMessage(response, '导出失败'))
      }

      const blob = await response.blob()
      const url = window.URL.createObjectURL(blob)
      const link = document.createElement('a')
      const fallbackTitle = format === 'pptx'
        ? getPptSpecTitle(message.content) || getCleanDocumentTitle(cleanArticleContent(message.content))
        : getCleanDocumentTitle(cleanArticleContent(message.content))
      link.href = url
      link.download = readDownloadFileName(response, format, fallbackTitle)
      document.body.appendChild(link)
      link.click()
      link.remove()
      setTimeout(() => window.URL.revokeObjectURL(url), 5000)
      showToast(format === 'pptx' ? 'PPTX 已生成' : 'DOCX 已生成')
    } catch (error) {
      showToast(error instanceof Error ? error.message : '导出失败')
    }
  }

  async function downloadMessageVideo(message: Message) {
    const cid = conversationIdRef.current
    if (!cid) return

    const startTime = Date.now()
    const timer = window.setInterval(() => setPptVideoElapsed(Math.floor((Date.now() - startTime) / 1000)), 1000)
    setPptVideoBusy(true)
    setPptVideoProgress(5)
    setPptVideoPhase('提交视频任务')
    setPptVideoStatus('正在从 PPT 方案生成视频...')
    setPptVideoEncoder(null)
    setPptVideoDownload((current) => {
      if (current) window.URL.revokeObjectURL(current.url)
      return null
    })

    try {
      const submitResponse = await fetchWithAuth(`${API_BASE}/api/conversations/${cid}/messages/${message.id}/export-video`, { method: 'POST' })
      if (!submitResponse.ok) {
        throw new Error(await readErrorMessage(submitResponse, '提交视频任务失败'))
      }

      const { taskId } = await submitResponse.json() as { taskId: string }
      setPptVideoPhase('排队中')
      setPptVideoProgress(10)

      await new Promise<void>((resolve, reject) => {
        const pollInterval = window.setInterval(async () => {
          try {
            const statusResponse = await fetchWithAuth(`${API_BASE}/api/conversations/ppt-video/${taskId}/status`)
            if (!statusResponse.ok) {
              window.clearInterval(pollInterval)
              reject(new Error('获取任务状态失败'))
              return
            }
            const status = await statusResponse.json() as {
              status: string
              stage: string
              progress: number
              slideIndex: number
              slideTotal: number
              error?: string
              fileName?: string
              videoEncoder?: string
              videoEncoderMode?: string
              videoEncoderLabel?: string
              videoEncoderDevice?: string
            }

            setPptVideoPhase(status.stage || '处理中')
            setPptVideoProgress(status.progress)
            setPptVideoEncoder(readPptVideoEncoder(status))
            const slideInfo = status.slideTotal > 0 ? ` (${status.slideIndex}/${status.slideTotal})` : ''
            setPptVideoStatus(`${status.stage || '处理中'}${slideInfo}`)

            if (status.status === 'completed') {
              window.clearInterval(pollInterval)
              const downloadResponse = await fetchWithAuth(`${API_BASE}/api/conversations/ppt-video/${taskId}/download`)
              if (!downloadResponse.ok) {
                reject(new Error('下载视频失败'))
                return
              }
              const blob = await downloadResponse.blob()
              const url = window.URL.createObjectURL(blob)
              const videoName = status.fileName || 'video.mp4'
              setPptVideoDownload({ url, name: videoName })
              setPptVideoProgress(100)
              setPptVideoElapsed(Math.floor((Date.now() - startTime) / 1000))
              setPptVideoPhase('转换完成')
              setPptVideoStatus('视频已生成，可直接下载')
              const link = document.createElement('a')
              link.href = url
              link.download = videoName
              document.body.appendChild(link)
              link.click()
              link.remove()
              showToast('PPT 视频已生成')
              resolve()
            } else if (status.status === 'failed') {
              window.clearInterval(pollInterval)
              reject(new Error(status.error || '转换失败'))
            }
          } catch (err) {
            window.clearInterval(pollInterval)
            reject(err instanceof Error ? err : new Error('轮询失败'))
          }
        }, 2000)
      })
    } catch (error) {
      const message = error instanceof Error ? error.message : '视频生成失败'
      setPptVideoPhase('转换失败')
      setPptVideoStatus(message)
      showToast(message, 10000)
    } finally {
      window.clearInterval(timer)
      setPptVideoBusy(false)
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
      }, 500)
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
    .replace(/^(?:这里写|请写)\s*.{0,10}(?:的|的)?(?:文章)?标题[，,：:\s]*/g, '')
    .replace(/\s*[，,]\s*\d{1,2}[-~]\d{1,2}\s*字.*$/, '')
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
  const isPptMessage = (message: Message) => {
    if (message.role !== 'assistant') return false
    const mode = getMessageMode(message)
    return mode === 'ppt' || /```(?:ppt-spec|veramedia-ppt)/i.test(message.content) || /PPT_SPEC\s*[:：]/i.test(message.content)
  }
  const getGeneratingImageDisplayTitle = (message: Message) =>
    getGeneratingImageTitle(message.content) || messageImageTitles[message.id] || '图片'
  const getPageTitle = () => {
    if (activePage === 'admin') return '后台管理'
    if (activePage === 'tasks') return '任务中心'
    if (activePage === 'assets') return '资产库'
    if (activePage === 'images') return '图片资产'
    if (activePage === 'pptVideo') return 'PPT 转视频'
    return 'AI 工作台'
  }
  const getPageSubtitle = () => {
    if (activePage === 'admin') return '管理注册策略、邮箱验证码服务和用户账号权限。'
    if (activePage === 'tasks') return '追踪生成进度、失败原因和历史结果。'
    if (activePage === 'assets') return '沉淀文章资产，方便复用与继续编辑。'
    if (activePage === 'images') return '集中管理生成图片、配图和可复用视觉素材。'
    if (activePage === 'pptVideo') return '上传 PPT，读取备注生成演讲音频，并转换为可下载视频。'
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
  const selectPptVideoFile = async (files: FileList | null) => {
    const file = files?.[0]
    if (!file) return
    const extension = file.name.slice(file.name.lastIndexOf('.')).toLowerCase()
    if (extension !== '.ppt' && extension !== '.pptx') {
      showToast('请选择 PPT 或 PPTX 文件')
      return
    }

    if (file.size > 100 * 1024 * 1024) {
      showToast('PPT 文件不能超过 100MB')
      return
    }

    setPptVideoFile(file)
    setPptVideoSlides([])
    setPptVideoRoleVoices({})
    setPptVideoPreviewId(null)
    setPptVideoEncoder(null)
    setPptVideoPreviewBusy(true)
    setPptVideoProgress(0)
    setPptVideoPhase('')
    setPptVideoDownload((current) => {
      if (current) window.URL.revokeObjectURL(current.url)
      return null
    })
    setPptVideoStatus('正在上传 PPT 文件...')
    setPptVideoElapsed(0)

    const uploadStart = Date.now()
    const uploadTimer = window.setInterval(() => setPptVideoElapsed(Math.floor((Date.now() - uploadStart) / 1000)), 1000)

    try {
      const form = new FormData()
      form.append('file', file)
      setPptVideoProgress(5)
      const response = await fetchWithAuth(`${API_BASE}/api/conversations/ppt-video/preview`, {
        method: 'POST',
        body: form,
      })
      if (!response.ok) {
        throw new Error(await readErrorMessage(response, 'PPT 预览失败'))
      }

      const { taskId } = await response.json() as { taskId: string }
      setPptVideoPhase('读取备注')
      setPptVideoStatus('正在读取 PPT 备注并渲染幻灯片...')
      setPptVideoProgress(10)

      // Poll task status (same pattern as video generation)
      await new Promise<void>((resolve, reject) => {
        const pollInterval = window.setInterval(async () => {
          try {
            const statusResponse = await fetchWithAuth(`${API_BASE}/api/conversations/ppt-video/${taskId}/status`)
            if (!statusResponse.ok) return
            const status = await statusResponse.json() as {
              status: string
              stage: string
              progress: number
              slideIndex: number
              slideTotal: number
              error?: string
              previewId?: string
              previewSlides?: PptVideoSlide[]
            }

            setPptVideoPhase(status.stage || '处理中')
            setPptVideoProgress(status.progress)
            const slideInfo = status.slideTotal > 0 ? ` (${status.slideTotal} 页)` : ''
            setPptVideoStatus(`${status.stage || '处理中'}${slideInfo}`)

            if (status.status === 'completed') {
              window.clearInterval(pollInterval)
              const slides = status.previewSlides || []
              setPptVideoSlides(slides)
              setPptVideoPreviewId(status.previewId || null)
              const elapsed = Math.floor((Date.now() - uploadStart) / 1000)
              setPptVideoStatus(`已加载 ${slides.length} 页 (${elapsed}s)`)
              setPptVideoProgress(100)
              setPptVideoPhase('解析完成')
              showToast('PPT 已加载')
              resolve()
            } else if (status.status === 'failed') {
              window.clearInterval(pollInterval)
              reject(new Error(status.error || '预览解析失败'))
            }
          } catch {
            // transient error, keep polling
          }
        }, 1500)
      })
    } catch (error) {
      const message = error instanceof Error ? error.message : 'PPT 预览失败'
      const title = file.name.replace(/\.[^.]+$/, '') || 'PPT 已选择'
      setPptVideoSlides([{
        index: 1,
        title,
        notes: `未能自动读取备注。可以在这里手动填写旁白，或留空生成静音视频。\n预览诊断：${message}`,
      }])
      setPptVideoStatus('预览读取失败，已切换为基础模式；仍可继续转换或手动填写备注。')
      showToast('预览读取失败，已切换为基础模式', 6000)
    } finally {
      window.clearInterval(uploadTimer)
      setPptVideoElapsed(0)
      setPptVideoPreviewBusy(false)
      setPptVideoProgress((current) => current === 100 ? current : 0)
    }
  }
  const updatePptVideoSlideNotes = (index: number, notes: string) => {
    setPptVideoSlides((current) => current.map((slide) => slide.index === index ? { ...slide, notes } : slide))
  }

  const updatePptVideoSetting = <K extends keyof PptVideoSettings>(key: K, value: PptVideoSettings[K]) => {
    setPptVideoSettings((current) => ({ ...current, [key]: value }))
  }

  const updatePptVideoRoleVoice = (speaker: string, voice: string) => {
    setPptVideoRoleVoices((current) => ({ ...current, [speaker]: voice }))
  }

  const convertPptVideoSlideToDialogue = (index: number) => {
    setPptVideoSettings((current) => ({ ...current, dubbingMode: 'dialogue' }))
    setPptVideoSlides((current) => current.map((slide) =>
      slide.index === index ? { ...slide, notes: createPptDialogueDraft(slide.notes) } : slide))
  }

  const convertAllPptVideoNotesToDialogue = () => {
    setPptVideoSettings((current) => ({ ...current, dubbingMode: 'dialogue' }))
    setPptVideoSlides((current) => current.map((slide) => ({ ...slide, notes: createPptDialogueDraft(slide.notes) })))
  }

  const generatePptVideoDialogueScript = async () => {
    if (pptVideoSlides.length === 0) {
      showToast('请先选择并读取 PPT')
      return
    }

    const sourceSlides = pptVideoSlides
    const batchSize = 2
    let aiGeneratedCount = 0
    let fallbackCount = 0

    const applyGeneratedNotes = (notesBySlide: Map<number, string>) => {
      setPptVideoSlides((current) => current.map((slide) => ({
        ...slide,
        notes: notesBySlide.get(slide.index) || slide.notes,
      })))
    }

    const applyFallbackNotes = (slides: PptVideoSlide[]) => {
      const fallbackNotes = new Map(slides.map((slide) => [slide.index, createPptDialogueDraft(slide.notes)]))
      fallbackCount += slides.length
      applyGeneratedNotes(fallbackNotes)
    }

    setPptVideoDialogueBusy(true)
    setPptVideoSettings((current) => ({ ...current, dubbingMode: 'dialogue' }))
    try {
      for (let start = 0; start < sourceSlides.length; start += batchSize) {
        const batch = sourceSlides.slice(start, start + batchSize)
        const done = Math.min(start + batch.length, sourceSlides.length)
        setPptVideoStatus(`正在优化对话稿 (${done}/${sourceSlides.length})`)

        try {
          const response = await fetchWithAuth(`${API_BASE}/api/conversations/ppt-video/dialogue-script`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
              style: '自然、专业、像两位真实讲解者在围绕页面内容交流',
              slides: batch.map((slide) => ({
                index: slide.index,
                title: slide.title,
                notes: slide.notes,
              })),
            }),
          })
          if (!response.ok) {
            applyFallbackNotes(batch)
            continue
          }

          const data = await response.json() as {
            slides?: Array<{ index: number; notes?: string }>
            aiGenerated?: boolean
          }
          const notesBySlide = new Map((data.slides || [])
            .filter((slide) => slide.index > 0 && slide.notes)
            .map((slide) => [slide.index, slide.notes || '']))

          if (notesBySlide.size === 0) {
            applyFallbackNotes(batch)
            continue
          }

          if (data.aiGenerated) aiGeneratedCount += notesBySlide.size
          else fallbackCount += notesBySlide.size
          applyGeneratedNotes(notesBySlide)
        } catch {
          applyFallbackNotes(batch)
        }
      }

      setPptVideoStatus(aiGeneratedCount > 0
        ? `对话稿已优化（AI ${aiGeneratedCount} 页，本地 ${fallbackCount} 页）`
        : '对话稿已使用本地规则生成')
      showToast(aiGeneratedCount > 0
        ? `对话稿已分批优化，AI 完成 ${aiGeneratedCount} 页`
        : 'AI 响应较慢，已使用本地规则生成对话稿', 7000)
    } catch {
      applyFallbackNotes(sourceSlides)
      setPptVideoStatus('对话稿已使用本地规则生成')
      showToast('AI 优化暂不可用，已切换为本地对话稿', 8000)
    } finally {
      setPptVideoDialogueBusy(false)
    }
  }

  const playVoiceSample = async () => {
    if (voiceSampleRef.current) {
      voiceSampleRef.current.pause()
      voiceSampleRef.current = null
      setVoiceSamplePlaying(false)
    }
    try {
      setVoiceSamplePlaying(true)
      const speed = pptVideoSettings.speed.replace('x', '')
      const rate = speed === '0.75' ? '-25%' : speed === '1.25' ? '+25%' : speed === '1.5' ? '+50%' : '+0%'
      const url = `${API_BASE}/api/conversations/ppt-video/voice-sample?voice=${encodeURIComponent(pptVideoSettings.voice)}&speed=${encodeURIComponent(rate)}`
      const audio = new Audio(url)
      voiceSampleRef.current = audio
      audio.onended = () => { setVoiceSamplePlaying(false); voiceSampleRef.current = null }
      audio.onerror = () => { setVoiceSamplePlaying(false); voiceSampleRef.current = null }
      await audio.play()
    } catch {
      setVoiceSamplePlaying(false)
    }
  }
  const convertPptToVideo = async () => {
    const file = pptVideoFile
    if (!file) {
      showToast('请先选择 PPT 文件')
      return
    }

    setPptVideoBusy(true)
    setPptVideoProgress(5)
    setPptVideoPhase('提交转换任务')
    setPptVideoElapsed(0)
    setPptVideoStatus('正在上传文件并提交转换任务...')
    setPptVideoEncoder(null)
    setPptVideoDownload((current) => {
      if (current) window.URL.revokeObjectURL(current.url)
      return null
    })

    const convertStart = Date.now()
    const convertTimer = window.setInterval(() => setPptVideoElapsed(Math.floor((Date.now() - convertStart) / 1000)), 1000)

    try {
      const form = new FormData()
      form.append('file', file)
      form.append('secondsPerSlide', pptVideoSettings.secondsPerSlide)
      form.append('voice', pptVideoSettings.voice)
      form.append('speed', pptVideoSettings.speed.replace('x', ''))
      form.append('resolution', pptVideoSettings.resolution)
      form.append('volume', String(pptVideoSettings.volume))
      form.append('dubbingMode', pptVideoSettings.dubbingMode)
      if (pptVideoSettings.dubbingMode === 'dialogue') {
        form.append('dialogueVoicesJson', JSON.stringify(buildPptVideoDialogueVoices(pptVideoSlides, pptVideoSettings, pptVideoRoleVoices)))
      }
      form.append('notesJson', JSON.stringify(Object.fromEntries(pptVideoSlides.map((slide) => [slide.index, slide.notes]))))
      if (pptVideoPreviewId) form.append('previewId', pptVideoPreviewId)
      if (pptVideoBgmFile) form.append('bgm', pptVideoBgmFile)

      setPptVideoProgress(10)
      const submitResponse = await fetchWithAuth(`${API_BASE}/api/conversations/ppt-video`, {
        method: 'POST',
        body: form,
      })
      if (!submitResponse.ok) {
        throw new Error(await readErrorMessage(submitResponse, '提交转换任务失败'))
      }

      const { taskId } = await submitResponse.json() as { taskId: string }
      setPptVideoPhase('排队中')
      setPptVideoStatus('任务已提交，正在等待处理...')
      setPptVideoProgress(12)

      // Poll task status every 2 seconds
      await new Promise<void>((resolve, reject) => {
        const pollInterval = window.setInterval(async () => {
          try {
            const statusResponse = await fetchWithAuth(`${API_BASE}/api/conversations/ppt-video/${taskId}/status`)
            if (!statusResponse.ok) {
              window.clearInterval(pollInterval)
              reject(new Error('获取任务状态失败'))
              return
            }
            const status = await statusResponse.json() as {
              status: string
              stage: string
              progress: number
              slideIndex: number
              slideTotal: number
              error?: string
              fileName?: string
              videoEncoder?: string
              videoEncoderMode?: string
              videoEncoderLabel?: string
              videoEncoderDevice?: string
            }

            setPptVideoPhase(status.stage || '处理中')
            setPptVideoProgress(status.progress)
            setPptVideoEncoder(readPptVideoEncoder(status))
            const slideInfo = status.slideTotal > 0 ? ` (${status.slideIndex}/${status.slideTotal})` : ''
            setPptVideoStatus(`${status.stage || '处理中'}${slideInfo}`)

            if (status.status === 'completed') {
              window.clearInterval(pollInterval)
              // Download the video
              const downloadResponse = await fetchWithAuth(`${API_BASE}/api/conversations/ppt-video/${taskId}/download`)
              if (!downloadResponse.ok) {
                reject(new Error('下载视频失败'))
                return
              }
              const blob = await downloadResponse.blob()
              const url = window.URL.createObjectURL(blob)
              const title = file.name.replace(/\.[^.]+$/, '').replace(/[\\/:*?"<>|""«»]+/g, '').slice(0, 48) || 'PPT视频'
              const videoName = status.fileName || `${title}.mp4`
              setPptVideoDownload({ url, name: videoName })
              setPptVideoElapsed(Math.floor((Date.now() - convertStart) / 1000))
              setPptVideoProgress(100)
              setPptVideoPhase('转换完成')
              setPptVideoStatus('视频已生成，可直接下载')
              const link = document.createElement('a')
              link.href = url
              link.download = videoName
              document.body.appendChild(link)
              link.click()
              link.remove()
              showToast('PPT 视频已生成')
              resolve()
            } else if (status.status === 'failed') {
              window.clearInterval(pollInterval)
              reject(new Error(status.error || '转换失败'))
            }
          } catch (err) {
            // Don't stop polling on transient errors
          }
        }, 2000)
      })
    } catch (error) {
      const message = error instanceof Error ? error.message : 'PPT 转视频失败'
      setPptVideoPhase('转换失败')
      setPptVideoStatus(message)
      showToast(message, 10000)
    } finally {
      window.clearInterval(convertTimer)
      setPptVideoBusy(false)
      setPptVideoProgress((current) => current === 100 ? current : 0)
    }
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
    if (copied) {
      setCopiedMessageId(message.id)
      window.setTimeout(() => setCopiedMessageId((current) => current === message.id ? null : current), 1800)
    }
    showToast(copied ? '消息已复制' : '当前浏览器不支持直接复制')
  }

  const speakMessageContent = (message: Message) => {
    if (speakingMessageId === message.id && 'speechSynthesis' in window) {
      if (isSpeechPaused) {
        window.speechSynthesis.resume()
        setIsSpeechPaused(false)
        showToast('继续朗读')
      } else {
        window.speechSynthesis.pause()
        setIsSpeechPaused(true)
        showToast('已暂停朗读')
      }
      return
    }

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
    utterance.onend = () => {
      setSpeakingMessageId((current) => current === message.id ? null : current)
      setIsSpeechPaused(false)
    }
    utterance.onerror = () => {
      setSpeakingMessageId((current) => current === message.id ? null : current)
      setIsSpeechPaused(false)
    }
    setSpeakingMessageId(message.id)
    setIsSpeechPaused(false)
    window.speechSynthesis.speak(utterance)
    showToast('开始朗读')
  }

  const askFollowupFromMessage = (message: Message) => {
    setOpenMessageMenuId(null)
    setDraft(`请基于上面这条回答继续展开：\n\n${getMessagePlainText(message).slice(0, 800)}\n\n我的追问是：`)
  }

  const toggleReferencePanel = (message: Message) => {
    setOpenReferenceMessageIds((current) => {
      const next = new Set(current)
      if (next.has(message.id)) {
        next.delete(message.id)
      } else {
        next.add(message.id)
      }
      return next
    })
  }

  const reportMessage = async (message: Message) => {
    setOpenMessageMenuId(null)
    const cid = conversationIdRef.current
    if (!cid) return
    try {
      await request(`/api/conversations/${cid}/messages/${message.id}/feedback`, {
        method: 'POST',
        body: JSON.stringify({ type: 'report', detail: '用户在消息菜单点击反馈与举报。' }),
      })
      showToast('反馈已记录')
    } catch (error) {
      showToast(error instanceof Error ? error.message : '反馈提交失败')
    }
  }
  const [copyFeedback, setCopyFeedback] = useState('')
  const [copyTitleFeedback, setCopyTitleFeedback] = useState('')
  const [openMessageMenuId, setOpenMessageMenuId] = useState<number | null>(null)
  const [copiedMessageId, setCopiedMessageId] = useState<number | null>(null)
  const [speakingMessageId, setSpeakingMessageId] = useState<number | null>(null)
  const [isSpeechPaused, setIsSpeechPaused] = useState(false)
  const [openReferenceMessageIds, setOpenReferenceMessageIds] = useState<Set<number>>(new Set())
  const [showShareModal, setShowShareModal] = useState(false)
  const [shareExpiry, setShareExpiry] = useState('24h')
  const [shareUrl, setShareUrl] = useState('')
  const [shareLoading, setShareLoading] = useState(false)

  useEffect(() => {
    return () => {
      if ('speechSynthesis' in window) {
        window.speechSynthesis.cancel()
      }
    }
  }, [])

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
    const safeName = (alt || 'veramedia-image').replace(/[\\/:*?"<>|""«»]+/g, '-').slice(0, 40)
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
  const getReferenceSourceCount = (referenceContent: string) => {
    const content = referenceContent.trim()
    if (!content) return 0
    const sources = parseReferenceSources(content)
    return sources.length || content.split('\n').filter((line) => line.trim()).length
  }

  const renderReferencePopover = (referenceContent: string) => {
    const content = referenceContent.trim()
    if (!content) return null
    const sources = parseReferenceSources(content)

    return (
      <div className="article-reference-popover">
        <div className="article-reference-popover-head">
          <span>参考资料</span>
          {sources.length > 0 && <small>{sources.length} 条</small>}
        </div>
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
      </div>
    )
  }
  const extractPptSpec = (content: string): { spec: PptSpec | null; cleaned: string } => {
    const candidates: Array<{ raw: string; json: string }> = []
    const fenced = /```(?:ppt-spec|veramedia-ppt|json)?[^\n`]*\n?([\s\S]*?)```/gi
    let match: RegExpExecArray | null
    while ((match = fenced.exec(content))) {
      candidates.push({ raw: match[0], json: match[1].trim() })
    }

    const marker = content.match(/PPT_SPEC\s*[:：]\s*(\{[\s\S]*\})/i)
    if (marker) candidates.push({ raw: marker[0], json: marker[1].trim() })
    const jsonStart = content.indexOf('{')
    const jsonEnd = content.lastIndexOf('}')
    if (jsonStart >= 0 && jsonEnd > jsonStart) {
      candidates.push({ raw: content.slice(jsonStart, jsonEnd + 1), json: content.slice(jsonStart, jsonEnd + 1).trim() })
    }

    for (const candidate of candidates) {
      try {
        const parsed = JSON.parse(candidate.json) as PptSpec
        if (Array.isArray(parsed.slides) && parsed.slides.length > 0) {
          return { spec: parsed, cleaned: content.replace(candidate.raw, '').trim() }
        }
      } catch {
        // Ignore non-spec JSON blocks.
      }
    }

    return { spec: null, cleaned: content }
  }

  const renderPptSpecCard = (message: Message, spec: PptSpec, cleaned: string) => {
    const slides = spec.slides || []
    const previewSlides = slides.slice(0, 6)
    const asString = (v: unknown) => typeof v === 'string' ? v.trim() : ''
    const title = asString(spec.title) || getCleanDocumentTitle(message.content) || 'PPT 制作方案'
    const subtitle = asString(spec.subtitle)
    const audience = asString(spec.audience)
    const theme = asString(spec.theme)
    const layoutLabels: Record<string, string> = {
      cover: '封面',
      agenda: '目录',
      section: '章节',
      summary: '总结',
      'data-card': '数据卡',
      process: '流程',
      timeline: '时间线',
      'two-column': '双栏',
      'title-content': '内容页',
    }
    const getLayoutLabel = (layout?: unknown) => {
      const value = (typeof layout === 'string' ? layout : 'title-content').toLowerCase()
      const key = Object.keys(layoutLabels).find((item) => value.includes(item))
      return key ? layoutLabels[key] : '内容页'
    }

    return (
      <div className="ppt-spec-result">
        {cleaned && !/^已基于|^基于|^下面是|^以下是/.test(cleaned) && <p className="ppt-spec-note">{cleaned}</p>}
        <section className="ppt-spec-card">
          <div className="ppt-spec-head">
            <span><Presentation size={16} /> PPT 方案</span>
            <strong>{title}</strong>
            {subtitle && <p>{subtitle}</p>}
            <div>
              <em>{slides.length} 页</em>
              {audience && <em>{audience}</em>}
              {theme && <em>{theme}</em>}
              <em>{slides.filter((slide) => asString(slide.notes || slide.narration)).length} 页备注</em>
            </div>
          </div>
          <div className="ppt-spec-slide-grid">
            {previewSlides.map((slide, index) => {
              const bullets = slide.bullets || slide.points || []
              return (
                <article className="ppt-spec-slide" key={`${slide.title || 'slide'}-${index}`}>
                  <span>{index + 1}</span>
                  <div className="ppt-spec-slide-title">
                    <strong>{slide.title || `第 ${index + 1} 页`}</strong>
                    <em>{getLayoutLabel(slide.layout)}</em>
                  </div>
                  {asString(slide.subtitle) && <p>{asString(slide.subtitle)}</p>}
                  {bullets.length > 0 && <ul>{bullets.slice(0, 3).map((item) => <li key={item}>{item}</li>)}</ul>}
                </article>
              )
            })}
          </div>
          {slides.length > previewSlides.length && <p className="ppt-spec-more">还有 {slides.length - previewSlides.length} 页已规划，可直接导出完整 PPT。</p>}
          <div className="ppt-spec-actions">
            <button type="button" onClick={() => downloadMessageExport(message, 'pptx')}><Download size={14} />下载 PPT</button>
          </div>
        </section>
      </div>
    )
  }

  const renderPptGeneratingCard = (message: Message) => {
    const content = stripReferenceSections(stripImagePrompts(message.content)).trim()
    const titleMatch = content.match(/"title"\s*:\s*"([^"]{2,80})"/)
    const themeMatch = content.match(/"theme"\s*:\s*"([^"]{2,40})"/)
    const slideMatches = content.match(/"layout"\s*:/g)
    const notesMatches = content.match(/"notes"\s*:/g)
    const slideCount = slideMatches?.length || 0
    const notesCount = notesMatches?.length || 0
    const title = titleMatch?.[1] || getCleanDocumentTitle(content) || '正在制作 PPT'
    const theme = themeMatch?.[1] || 'AI 视觉方案'

    return (
      <div className="ppt-spec-result">
        <section className="ppt-spec-card ppt-spec-generating">
          <div className="ppt-spec-head">
            <span><Presentation size={16} /> PPT 制作中</span>
            <strong>{title}</strong>
            <p>正在规划页面结构、视觉版式、图表表达和演讲备注，完成后会自动切换为可下载的 PPT 方案卡片。</p>
            <div>
              <em>{theme}</em>
              <em>{slideCount > 0 ? `已规划 ${slideCount} 页` : '规划页面中'}</em>
              <em>{notesCount > 0 ? `${notesCount} 页备注` : '生成备注中'}</em>
            </div>
          </div>
          <div className="ppt-generating-steps">
            {['提炼内容结构', '设计页面版式', '生成视觉说明', '补充演讲备注'].map((item, index) => (
              <div className="ppt-generating-step" key={item}>
                <span>{index + 1}</span>
                <strong>{item}</strong>
                <i />
              </div>
            ))}
          </div>
        </section>
      </div>
    )
  }

  const renderTextMessage = (message: Message) => {
    const rawDisplayContent = stripReferenceSections(stripImagePrompts(message.content)).trim()
    const pptSpecResult = message.role === 'assistant' ? extractPptSpec(rawDisplayContent) : { spec: null, cleaned: rawDisplayContent }
    const displayContent = pptSpecResult.spec ? pptSpecResult.cleaned : rawDisplayContent
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
          ) : pptSpecResult.spec ? (
            renderPptSpecCard(message, pptSpecResult.spec, displayContent)
          ) : isPptMessage(message) && isMessageGenerating(message) ? (
            renderPptGeneratingCard(message)
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
      </>
    )
  }

  const renderMessageActions = (message: Message) => {
    if (isMessageGenerating(message)) return null
    const isAssistant = message.role === 'assistant'
    const referenceCount = isAssistant ? getReferenceSourceCount(extractReferenceSection(message.content)) : 0
    const isReferenceOpen = openReferenceMessageIds.has(message.id)
    const isSpeaking = speakingMessageId === message.id
    const hasPptSpec = isAssistant && Boolean(extractPptSpec(stripReferenceSections(stripImagePrompts(message.content)).trim()).spec)
    const showAssistantMore = isAssistant && !hasPptSpec

    return (
      <div className="message-action-row" aria-label="消息操作">
        <button className={copiedMessageId === message.id ? 'active' : ''} type="button" title="复制" onClick={() => void copyMessageContent(message)}>
          {copiedMessageId === message.id ? <Check size={16} /> : <Copy size={16} />}
        </button>
        {isAssistant && (
          <>
            <button className={isSpeaking ? 'active speech-action' : 'speech-action'} type="button" title={isSpeaking && !isSpeechPaused ? '暂停朗读' : '朗读'} onClick={() => speakMessageContent(message)}>
              {isSpeaking ? (
                <span className={isSpeechPaused ? 'speech-bars paused' : 'speech-bars'} aria-hidden="true">
                  <i />
                  <i />
                  <i />
                </span>
              ) : (
                <Volume2 size={16} />
              )}
            </button>
          </>
        )}
        {hasPptSpec && (
          <>
            <button className="pill-action" type="button" title="下载 PPT" onClick={() => void downloadMessageExport(message, 'pptx')}>
              <Presentation size={15} />
              下载
            </button>
          </>
        )}
        {!isAssistant && (
          <button type="button" title="重新生成" onClick={() => void retryFromMessage(message)} disabled={isStreaming}>
            <RotateCcw size={16} />
          </button>
        )}
        {isAssistant && referenceCount > 0 && (
          <div className="reference-action-wrap">
            <button className={isReferenceOpen ? 'reference-toggle active' : 'reference-toggle'} type="button" onClick={() => toggleReferencePanel(message)} title={isReferenceOpen ? '收起参考资料' : '展开参考资料'}>
              参考 {referenceCount} 篇资料
            </button>
            {isReferenceOpen && renderReferencePopover(extractReferenceSection(message.content))}
          </div>
        )}
        {showAssistantMore && (
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
                <button type="button" onClick={() => { setOpenMessageMenuId(null); void downloadMessageExport(message, 'mp4') }}>
                  <Video size={16} />
                  导出视频
                </button>
                <button type="button" onClick={() => askFollowupFromMessage(message)}>
                  <MessageSquarePlus size={16} />
                  基于此追问
                </button>
                <button type="button" onClick={() => void reportMessage(message)}>
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
          <div className="brand-mark"><span /></div>
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
        <button className={activePage === 'pptVideo' ? 'new-chat active' : 'new-chat'} onClick={openPptVideoPage}>
          <Video size={18} />
          PPT 转视频
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
          <span className="sidebar-user-copy">
            <strong>{user.displayName || user.email || 'SunnyFan'}</strong>
            <small>个人设置</small>
          </span>
          <Settings size={17} />
        </button>
      </aside>
      <button className="mobile-nav-backdrop" type="button" aria-label="关闭会话列表" onClick={() => setIsMobileNavOpen(false)} />

      <section
        ref={chatPaneRef}
        className={activePage === 'editor' ? 'chat-pane editor-active' : 'chat-pane'}
        style={activePage === 'chat' ? ({
          '--composer-height': `${composerMetrics.height}px`,
          '--composer-center-x': composerMetrics.centerX > 0 ? `${composerMetrics.centerX}px` : '50%',
        } as CSSProperties) : undefined}
      >
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
          <button className={activePage === 'pptVideo' ? 'top-settings mobile-only-nav-action active' : 'top-settings mobile-only-nav-action'} type="button" onClick={activePage === 'pptVideo' ? goToChat : openPptVideoPage}>
            {activePage === 'pptVideo' ? <ArrowLeft size={18} /> : <Video size={18} />}
            {activePage === 'pptVideo' ? '返回前台' : '转视频'}
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
        ) : activePage === 'pptVideo' ? (
          <div className="ppt-video-page">
            <section className="ppt-video-studio" aria-label="PPT 转视频">
              <div className="ppt-video-grid">
                <div className="ppt-video-panel ppt-file-panel">
                  <h3><UploadCloud size={15} /> 文件选择</h3>
                  <div className="ppt-file-row">
                    <label className={pptVideoBusy || pptVideoPreviewBusy ? 'ppt-file-button disabled' : 'ppt-file-button'}>
                      选择 PPT
                      <input
                        type="file"
                        accept=".ppt,.pptx,application/vnd.ms-powerpoint,application/vnd.openxmlformats-officedocument.presentationml.presentation"
                        disabled={pptVideoBusy || pptVideoPreviewBusy}
                        onChange={(event) => {
                          void selectPptVideoFile(event.target.files)
                          event.currentTarget.value = ''
                        }}
                      />
                    </label>
                    <span title={pptVideoFile?.name}>{pptVideoFile ? pptVideoFile.name : '未选择文件'}</span>
                  </div>
                </div>

                <div className="ppt-video-panel ppt-voice-panel">
                  <h3><Volume2 size={15} /> 语音设置</h3>
                  <div className="ppt-voice-primary-grid">
                    <label className="ppt-voice-field ppt-voice-field-wide">
                      音色
                      <div className="ppt-voice-row">
                        <select value={pptVideoSettings.voice} onChange={(event) => updatePptVideoSetting('voice', event.target.value)}>
                          {pptVideoVoiceOptions.map((item) => <option key={item.value} value={item.value}>{item.label}</option>)}
                        </select>
                        <button type="button" className={`ppt-voice-sample${voiceSamplePlaying ? ' playing' : ''}`} onClick={() => void playVoiceSample()} title="试听音色">
                          <Play size={13} />
                        </button>
                      </div>
                    </label>
                    <label className="ppt-voice-field">
                      配音
                      <select value={pptVideoSettings.dubbingMode} onChange={(event) => updatePptVideoSetting('dubbingMode', event.target.value)}>
                        <option value="single">单人旁白</option>
                        <option value="dialogue">对话配音</option>
                      </select>
                    </label>
                    <label className="ppt-voice-field">
                      语速
                      <select value={pptVideoSettings.speed} onChange={(event) => updatePptVideoSetting('speed', event.target.value)}>
                        {pptVideoSpeedOptions.map((item) => <option key={item} value={item}>{item}{item === '1.0x' ? '（正常）' : ''}</option>)}
                      </select>
                    </label>
                  </div>
                  {pptVideoSettings.dubbingMode === 'dialogue' && (
                    <div className="ppt-dialogue-voice-grid">
                      <label>
                        主持人
                        <select value={pptVideoSettings.dialogueHostVoice} onChange={(event) => updatePptVideoSetting('dialogueHostVoice', event.target.value)}>
                          {pptVideoVoiceOptions.map((item) => <option key={item.value} value={item.value}>{item.label}</option>)}
                        </select>
                      </label>
                      <label>
                        嘉宾
                        <select value={pptVideoSettings.dialogueGuestVoice} onChange={(event) => updatePptVideoSetting('dialogueGuestVoice', event.target.value)}>
                          {pptVideoVoiceOptions.map((item) => <option key={item.value} value={item.value}>{item.label}</option>)}
                        </select>
                      </label>
                      <label>
                        旁白
                        <select value={pptVideoSettings.dialogueNarratorVoice} onChange={(event) => updatePptVideoSetting('dialogueNarratorVoice', event.target.value)}>
                          {pptVideoVoiceOptions.map((item) => <option key={item.value} value={item.value}>{item.label}</option>)}
                        </select>
                      </label>
                      {pptVideoDialogueVoiceEntries.length > 0 && (
                        <div className="ppt-dialogue-role-voices">
                          {pptVideoDialogueVoiceEntries.map((entry) => (
                            <label key={entry.speaker} title={entry.speaker}>
                              <span>{entry.speaker}</span>
                              <select value={entry.voice} onChange={(event) => updatePptVideoRoleVoice(entry.speaker, event.target.value)}>
                                {pptVideoVoiceOptions.map((item) => <option key={item.value} value={item.value}>{item.label}</option>)}
                              </select>
                            </label>
                          ))}
                        </div>
                      )}
                    </div>
                  )}
                </div>

                <div className="ppt-video-panel ppt-audio-panel">
                  <h3><Music size={15} /> 音频设置</h3>
                  <div className="ppt-bgm-row">
                    <span>BGM</span>
                    <strong title={pptVideoBgmFile?.name}>{pptVideoBgmFile?.name || '未选择'}</strong>
                    <label className="ppt-bgm-button">
                      选择
                      <input
                        type="file"
                        accept="audio/*,.mp3,.wav,.m4a,.aac,.ogg"
                        disabled={pptVideoBusy}
                        onChange={(event) => {
                          const file = event.target.files?.[0] || null
                          setPptVideoBgmFile(file)
                          updatePptVideoSetting('bgmName', file?.name || '')
                          event.currentTarget.value = ''
                        }}
                      />
                    </label>
                    <button type="button" disabled={!pptVideoBgmFile || pptVideoBusy} onClick={() => {
                      setPptVideoBgmFile(null)
                      updatePptVideoSetting('bgmName', '')
                    }}>清除</button>
                  </div>
                  <label className="ppt-range-row">
                    音量
                    <input type="range" min="0" max="100" value={pptVideoSettings.volume} onChange={(event) => updatePptVideoSetting('volume', Number(event.target.value))} />
                    <span>{pptVideoSettings.volume}%</span>
                  </label>
                </div>

                <div className="ppt-video-panel ppt-output-panel">
                  <h3><Download size={15} /> 输出设置</h3>
                  <label>
                    分辨率
                    <select value={pptVideoSettings.resolution} onChange={(event) => updatePptVideoSetting('resolution', event.target.value)}>
                      {pptVideoResolutionOptions.map((item) => <option key={item.value} value={item.value}>{item.label}</option>)}
                    </select>
                  </label>
                  <label>
                    无备注页时长
                    <select value={pptVideoSettings.secondsPerSlide} onChange={(event) => updatePptVideoSetting('secondsPerSlide', event.target.value)}>
                      <option value="3">3 秒</option>
                      <option value="5">5 秒</option>
                      <option value="8">8 秒</option>
                      <option value="10">10 秒</option>
                    </select>
                  </label>
                  <button className="ppt-convert-button" type="button" disabled={!pptVideoFile || pptVideoBusy || pptVideoPreviewBusy || pptVideoDialogueBusy} onClick={() => void convertPptToVideo()}>
                    {pptVideoBusy ? <span className="ppt-button-spinner" aria-hidden="true" /> : <Video size={15} />}
                    {pptVideoBusy ? '正在转换' : '开始转换'}
                  </button>
                </div>
              </div>

              <div className="ppt-video-preview">
                <div className="ppt-video-preview-head">
                  <strong>幻灯片预览 · 演讲稿编辑</strong>
                  <span>可直接编辑每页备注，转换时会使用这里的文字生成语音</span>
                  {pptVideoSettings.dubbingMode === 'dialogue' && pptVideoSlides.length > 0 && (
                    <div className="ppt-dialogue-actions">
                      {pptVideoDialogueSpeakers.length > 0 && (
                        <span className="ppt-dialogue-roles">角色：{pptVideoDialogueSpeakers.slice(0, 4).join('、')}{pptVideoDialogueSpeakers.length > 4 ? ` +${pptVideoDialogueSpeakers.length - 4}` : ''}</span>
                      )}
                      <button type="button" onClick={() => void generatePptVideoDialogueScript()} disabled={pptVideoDialogueBusy || pptVideoBusy || pptVideoPreviewBusy}>
                        {pptVideoDialogueBusy ? <span className="ppt-button-spinner small" aria-hidden="true" /> : <Sparkles size={13} />}
                        AI 优化对话稿
                      </button>
                      <button type="button" onClick={convertAllPptVideoNotesToDialogue} disabled={pptVideoDialogueBusy || pptVideoBusy || pptVideoPreviewBusy}>
                        <Users size={13} />
                        全部转对话
                      </button>
                    </div>
                  )}
                </div>
                {pptVideoSlides.length === 0 ? (
                  <div className={pptVideoPreviewBusy ? 'ppt-video-empty is-loading' : 'ppt-video-empty'}>
                    {pptVideoPreviewBusy && <span className="ppt-video-loader" aria-hidden="true" />}
                    <strong>{pptVideoPreviewBusy ? '正在读取 PPT…' : '等待选择 PPT'}</strong>
                    <p>{pptVideoStatus || '请选择 PPT 文件，加载后这里会显示各页标题与完整演讲稿内容。'}</p>
                  </div>
                ) : (
                  <div className="ppt-viewer-list">
                    <div className="ppt-viewer-list-head">
                      <strong>共 {pptVideoSlides.length} 页</strong>
                      {pptVideoStatus && <span className="ppt-viewer-parse-time">{pptVideoStatus.replace(/^已加载 \d+ 页\s*/, '')}</span>}
                    </div>
                    {pptVideoSlides.map((slide) => (
                      <div className="ppt-viewer-row" key={slide.index}>
                        <div className="ppt-viewer-row-img">
                          {pptVideoPreviewId && (
                            <img
                              src={`${API_BASE}/api/conversations/ppt-video/preview/${pptVideoPreviewId}/slide/${slide.index}`}
                              alt={`第 ${slide.index} 页`}
                              loading="lazy"
                              onClick={() => setPptVideoLightboxSrc(`${API_BASE}/api/conversations/ppt-video/preview/${pptVideoPreviewId}/slide/${slide.index}`)}
                            />
                          )}
                          <span>{slide.index}</span>
                        </div>
                        <div className="ppt-viewer-row-notes">
                          <div className="ppt-notes-title-row">
                            <strong>{slide.title}</strong>
                            {pptVideoSettings.dubbingMode === 'dialogue' && (
                              <button type="button" onClick={() => convertPptVideoSlideToDialogue(slide.index)} disabled={pptVideoDialogueBusy || pptVideoBusy || pptVideoPreviewBusy}>
                                <Users size={12} />
                                转对话
                              </button>
                            )}
                          </div>
                          <textarea
                            value={slide.notes}
                            placeholder="这一页没有备注。可在这里补充旁白；留空则生成静音片段。"
                            onChange={(event) => updatePptVideoSlideNotes(slide.index, event.target.value)}
                          />
                        </div>
                      </div>
                    ))}
                  </div>
                )}
              </div>

              <div className="ppt-video-progress">
                <div className="ppt-progress-head">
                  <div className="ppt-progress-title">
                    {pptVideoEncoder && (
                      <div className={`ppt-encoder-badge ${pptVideoEncoder.mode === 'gpu' ? 'gpu' : 'cpu'}`}>
                        {pptVideoEncoder.mode === 'gpu' ? <Zap size={14} /> : <Cpu size={14} />}
                        <strong>{pptVideoEncoder.mode === 'gpu' ? 'GPU' : 'CPU'}</strong>
                      </div>
                    )}
                    <strong>{pptVideoProgressTitle}</strong>
                    {pptVideoProgressMessage && <p>{pptVideoProgressMessage}</p>}
                  </div>
                  <div className="ppt-progress-meta">
                    <div className="ppt-progress-actions">
                      {pptVideoDownload && (
                        <a className="ppt-download-link" href={pptVideoDownload.url} download={pptVideoDownload.name}>
                          <Download size={14} />
                          下载视频
                        </a>
                      )}
                    </div>
                    <span>{pptVideoElapsed > 0 && (pptVideoBusy || pptVideoPreviewBusy || pptVideoProgress === 100) ? `${Math.floor(pptVideoElapsed / 60)}:${String(pptVideoElapsed % 60).padStart(2, '0')}` : pptVideoBusy || pptVideoPreviewBusy ? '处理中' : `${pptVideoProgress}%`}</span>
                  </div>
                </div>
                <div className={pptVideoBusy || pptVideoPreviewBusy ? 'ppt-progress-track active' : 'ppt-progress-track'}>
                  <i style={{ width: `${pptVideoProgress}%` }} />
                </div>
              </div>
            </section>

            {pptVideoLightboxSrc && (
              <div className="ppt-lightbox" onClick={() => setPptVideoLightboxSrc(null)}>
                <img src={pptVideoLightboxSrc} alt="幻灯片预览" onClick={(e) => e.stopPropagation()} />
                <button className="ppt-lightbox-close" onClick={() => setPptVideoLightboxSrc(null)}>
                  <X size={20} />
                </button>
              </div>
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
        <div
          className="messages"
          ref={messagesRef}
          onScroll={updateChatBottomState}
          onLoadCapture={() => {
            if (isChatAtBottomRef.current) anchorChatToBottom()
          }}
        >
          {messages.length === 0 && (
            <div className="empty-state">
              <span className="empty-brand-mark"><span /></span>
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
                          </div>
                        )}
                      </div>
                    </div>
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
        {showScrollToBottom && activePage === 'chat' && (
          <button className="scroll-bottom-button" type="button" onClick={scrollChatToBottom} title="回到底部" aria-label="回到底部">
            <ArrowDown size={22} />
          </button>
        )}

        <form className="composer" ref={composerRef} onSubmit={sendMessage}>
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
              placeholder={agentOptions.capability === 'image' ? '描述你想要的图片' : agentOptions.capability === 'write' ? '输入主题和写作要求' : agentOptions.capability === 'code' ? '输入“@”唤起常用语，或粘贴代码快速提问' : agentOptions.capability === 'translate' ? '输入要翻译的文本' : agentOptions.capability === 'research' ? '输入主题和报告要求' : agentOptions.capability === 'qa' ? '输入题目，或粘贴拖拽题目图片' : agentOptions.capability === 'data' ? '请输入对于上传数据的任何分析处理要求' : agentOptions.capability === 'super' ? '输入问题或任务' : agentOptions.capability === 'ppt' ? '输入主题、资料和目标，AI 将自主定制整套 PPT' : '发消息，粘贴图片，或拖入文件...'}
            />
            {attachments.length > 0 && (
              <div className="attachment-row">
                {attachments.map((file) => renderAttachmentPreview(file))}
              </div>
            )}
            <div className="composer-bottom-row">
              {renderCapabilityTools() ?? (
                <div className="capability-row" aria-label="常用能力">
                  <label className="capability-add" title="上传图片或文件">
                    <Plus size={19} />
                    <input type="file" accept={attachmentAccept} multiple onChange={(e) => uploadFiles(e.target.files)} />
                  </label>
                  <span className="capability-divider" />
                  {renderThinkingSelector()}
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
                      onClick={() => {
                        setIsThinkingMenuOpen(false)
                        setThinkingMenuPosition(null)
                        setOpenToolbarSelect(null)
                        setToolbarSelectPosition(null)
                        setIsToolMenuOpen((value) => !value)
                      }}
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
              )}
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
