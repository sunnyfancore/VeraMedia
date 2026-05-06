export type IntentMode = 'auto' | 'chat' | 'article' | 'document' | 'image' | 'article_image'
export type AssistantBlockType = 'detect' | 'layout' | 'title' | 'lead' | 'image'

export type AssistantBlock = {
  id: number
  type: AssistantBlockType
  items?: string[]
  text?: string
  images?: string[]
  error?: string
  loading?: boolean
  prompt?: string
  style?: string
  ratio?: string
}

export const assistantBlockLabels: Record<AssistantBlockType, string> = {
  detect: '内容检测',
  layout: '智能排版',
  title: '智能标题',
  lead: '提取导语',
  image: 'AI 配图',
}

export const assistantBlockLoadingText: Record<AssistantBlockType, string> = {
  detect: '正在检测中...',
  layout: '正在排版中...',
  title: '正在生成标题...',
  lead: '正在提取导语...',
  image: '图片生成中...',
}

export const intentModeOptions: { value: IntentMode; label: string; title: string }[] = [
  { value: 'auto', label: '自动', title: '由 AI 自动判断任务类型' },
  { value: 'chat', label: '问答', title: '普通问答或讨论' },
  { value: 'article', label: '文章', title: '生成文章卡片' },
  { value: 'document', label: '文档', title: '流式输出文档、方案、报告' },
  { value: 'image', label: '生图', title: '直接生成图片' },
  { value: 'article_image', label: '只配图', title: '给已有文章配图，不改正文' },
]

export const stripImagePrompts = (text: string) =>
  text
    .replace(/<!--\s*image\s*-\s*prompt:[\s\S]*?-->\s*/gi, '')
    .replace(/<!--\s*image\s*-\s*prompt:[^\n]*/gi, '')

const referenceHeadingPattern = '(?:参考资料|参考来源|引用来源|资料来源|参考文献|参考链接|来源链接|References|Sources)'

export const stripReferenceSections = (text: string) => {
  return text
    .replace(new RegExp(`\\n{2,}---\\s*\\n{1,3}#{1,6}\\s*${referenceHeadingPattern}\\s*\\n+[\\s\\S]*$`, 'i'), '')
    .replace(new RegExp(`\\n{2,}#{1,6}\\s*${referenceHeadingPattern}\\s*\\n+[\\s\\S]*$`, 'i'), '')
    .replace(new RegExp(`\\n{2,}${referenceHeadingPattern}\\s*\\n+[\\s\\S]*$`, 'i'), '')
    .replace(new RegExp(`\\n{2,}${referenceHeadingPattern}\\s*[:：]\\s*[\\s\\S]*$`, 'i'), '')
    .trimEnd()
}

export const cleanArticleContent = (text: string) =>
  stripReferenceSections(stripImagePrompts(text)).trim()

export const extractReferenceSection = (text: string) => {
  const clean = stripImagePrompts(text).trimEnd()
  const patterns = [
    new RegExp(`\\n{2,}---\\s*\\n{1,3}#{1,6}\\s*${referenceHeadingPattern}\\s*\\n+([\\s\\S]*)$`, 'i'),
    new RegExp(`\\n{2,}#{1,6}\\s*${referenceHeadingPattern}\\s*\\n+([\\s\\S]*)$`, 'i'),
    new RegExp(`\\n{2,}${referenceHeadingPattern}\\s*\\n+([\\s\\S]*)$`, 'i'),
    new RegExp(`\\n{2,}${referenceHeadingPattern}\\s*[:：]\\s*([\\s\\S]*)$`, 'i'),
  ]

  for (const pattern of patterns) {
    const match = clean.match(pattern)
    const referenceContent = match?.[1]?.trim()
    if (referenceContent) return referenceContent
  }

  return ''
}

export const isArticleMessageMode = (mode: string | null | undefined) =>
  mode === 'article' || mode === 'article_image'

export const hasArticleIntent = (content: string) => {
  const normalized = content.replace(/\s+/g, '')
  if (/(?:文档|方案|报告|说明|清单|表格|邮件|简历|合同|计划|总结|教程|手册|SOP|PRD)/i.test(normalized)) {
    return false
  }

  const articleNouns = '(?:文章|稿子|稿件|推文|公众号(?:文章|图文)?|小红书图文|新闻稿|软文|长文)'
  const asksForArticle = new RegExp(`(?:写|生成|创作|出|做|根据).{0,12}${articleNouns}`).test(normalized)
    || new RegExp(`${articleNouns}.{0,12}(?:写|生成|创作|出|做|改写|续写|润色|排版|配图|优化|修改|成文)`).test(normalized)
  const asksToEditArticle = new RegExp(`(?:改写|续写|润色|排版|配图|优化|修改).{0,12}${articleNouns}`).test(normalized)
    || /(?:这一篇|这篇|原文|正文).{0,12}(?:改写|续写|润色|排版|配图|优化|修改)/.test(normalized)
  return asksForArticle || asksToEditArticle
}

export const hasArticleShape = (content: string) => {
  const clean = cleanArticleContent(content)
  const headings = clean.match(/^##\s+\S+/gm)?.length ?? 0
  return (clean.length > 700 && /^#\s+\S+/m.test(clean) && headings >= 2)
    || (clean.length > 900 && headings >= 2)
    || /\{\{image:[^}]+}}/i.test(clean)
    || /!\[[^\]]*]\([^)]+\)/.test(clean)
}

export const isArticleLike = (content: string, previousUserContent: string) =>
  hasArticleShape(content) && hasArticleIntent(previousUserContent)

export const getDocumentTitle = (content: string) => {
  const heading = content.split('\n').find((line) => line.trim().replace(/^#+\s*/, '').length > 4)
  return heading?.replace(/^#+\s*/, '').replace(/\*\*/g, '').trim().slice(0, 42) || '生成的内容文档'
}

export function splitDocumentDraft(content: string) {
  const match = content.match(/^(#{1,3})\s+(.+)$/m)
  if (!match || match.index === undefined) {
    return { title: '', body: content, level: '#' }
  }

  const before = content.slice(0, match.index).trim()
  const afterStart = match.index + match[0].length
  const after = content.slice(afterStart).replace(/^\n+/, '')
  const body = [before, after].filter(Boolean).join('\n\n')
  return {
    title: match[2].replace(/\*\*/g, '').trim(),
    body,
    level: match[1],
  }
}

export const composeDocumentDraft = (title: string, body: string, level = '#') => {
  const safeTitle = title.trim()
  if (!safeTitle) return body
  return `${level} ${safeTitle}\n\n${body.replace(/^\n+/, '')}`
}

export const getDocumentExcerpt = (content: string) => cleanArticleContent(content)
  .replace(/!\[[^\]]*]\([^)]+\)/g, '')
  .replace(/\[[^\]]+]\([^)]+\)/g, '')
  .replace(/\{\{image:[^}]+}}/g, '')
  .replace(/[#*_`>-]/g, '')
  .replace(/\s+/g, ' ')
  .trim()
  .slice(0, 120)

export const getContentTextLength = (content: string) => cleanArticleContent(content)
  .replace(/!\[[^\]]*]\([^)]+\)/g, '')
  .replace(/[#*_`>()!\s-]/g, '')
  .replaceAll('[', '')
  .replaceAll(']', '')
  .length

export const getGeneratingImageTitle = (content: string) =>
  content.match(/^正在生成图片[:：]\s*(.+?)\s*$/m)?.[1]?.trim() ?? ''

export const formatMessageTime = (value: string) => {
  const date = new Date(value)
  if (Number.isNaN(date.getTime())) return ''
  return date.toLocaleString('zh-CN', {
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
  })
}

export type ServerSentEvent = {
  event: string
  data: unknown
}

export function parseServerSentEvent(eventText: string): ServerSentEvent | null {
  const lines = eventText.split(/\r?\n/)
  const event = lines.find((line) => line.startsWith('event: '))?.slice(7).trim()
  const dataText = lines
    .filter((line) => line.startsWith('data: '))
    .map((line) => line.slice(6))
    .join('\n')
    .trim()

  if (!event || !dataText) return null

  try {
    return { event, data: JSON.parse(dataText) }
  } catch {
    return null
  }
}

export async function readServerSentEvents(
  response: Response,
  onEvent: (event: ServerSentEvent) => void,
) {
  if (!response.body) return

  const reader = response.body.getReader()
  const decoder = new TextDecoder()
  let buffer = ''

  while (true) {
    const { value, done } = await reader.read()
    if (done) break
    buffer += decoder.decode(value, { stream: true })
    const events = buffer.split('\n\n')
    buffer = events.pop() ?? ''
    for (const eventText of events) {
      const parsed = parseServerSentEvent(eventText)
      if (parsed) onEvent(parsed)
    }
  }

  if (buffer.trim()) {
    const parsed = parseServerSentEvent(buffer)
    if (parsed) onEvent(parsed)
  }
}
