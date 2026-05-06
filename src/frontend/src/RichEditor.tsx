import { useEditor, EditorContent } from '@tiptap/react'
import StarterKit from '@tiptap/starter-kit'
import Image from '@tiptap/extension-image'
import Link from '@tiptap/extension-link'
import { Table } from '@tiptap/extension-table'
import { TableRow } from '@tiptap/extension-table-row'
import { TableCell } from '@tiptap/extension-table-cell'
import { TableHeader } from '@tiptap/extension-table-header'
import Placeholder from '@tiptap/extension-placeholder'
import { marked } from 'marked'
import TurndownService from 'turndown'
import { useEffect, useRef, useCallback, forwardRef, useImperativeHandle } from 'react'

const turndown = new TurndownService({
  headingStyle: 'atx',
  codeBlockStyle: 'fenced',
  bulletListMarker: '-',
})

turndown.addRule('imageWithComment', {
  filter: (node) => node.nodeName === 'IMG',
  replacement: (_content, node) => {
    const el = node as HTMLImageElement
    const alt = el.getAttribute('alt') || ''
    const src = el.getAttribute('src') || ''
    const prompt = el.getAttribute('data-prompt') || ''
    const comment = prompt ? `<!-- image-prompt:${prompt} -->\n` : ''
    return `\n\n${comment}![${alt}](${src})\n\n`
  },
})

turndown.addRule('tableCell', {
  filter: ['th', 'td'],
  replacement: (content) => ` ${content.trim().replace(/\n/g, ' ')} |`,
})

turndown.addRule('tableRow', {
  filter: 'tr',
  replacement: (content) => `|${content}\n`,
})

turndown.addRule('tableHead', {
  filter: 'thead',
  replacement: (content) => {
    const cols = (content.match(/\|/g) || []).length - 1
    const separator = '|' + ' --- |'.repeat(cols)
    return `${content}${separator}\n`
  },
})

turndown.addRule('table', {
  filter: 'table',
  replacement: (_content, node) => {
    const el = node as HTMLTableElement
    const thead = el.querySelector('thead')
    const tbody = el.querySelector('tbody')
    let result = ''
    if (thead) result += turndown.turndown(thead.outerHTML)
    if (tbody) {
      for (const row of Array.from(tbody.rows)) {
        result += turndown.turndown(row.outerHTML)
      }
    }
    return `\n\n${result}\n`
  },
})

function markdownToHtml(md: string): string {
  const withoutComments = md.replace(
    /<!--\s*image\s*-\s*prompt:([A-Za-z0-9+/=]+)\s*-->\s*!\[([^\]]*)\]\(([^)]+)\)/gi,
    (_match, prompt, alt, src) => `<img src="${src}" alt="${alt}" data-prompt="${prompt}" />`
  )
  return marked.parse(withoutComments, { async: false }) as string
}

function htmlToMarkdown(html: string): string {
  return turndown.turndown(html)
    .replace(/\n{3,}/g, '\n\n')
    .trim()
}

export interface RichEditorHandle {
  getMarkdown: () => string
  setMarkdown: (md: string) => void
  getHTML: () => string
  focus: () => void
  insertImage: (src: string, alt: string, prompt?: string) => void
  getSelectedText: () => string
  toggleHeading: (level: 1 | 2 | 3) => void
  toggleBold: () => void
  toggleItalic: () => void
  toggleBulletList: () => void
  toggleBlockquote: () => void
  toggleCodeBlock: () => void
  setHorizontalRule: () => void
  insertLink: (href: string) => void
  insertTable: (rows?: number, cols?: number) => void
  undo: () => void
  redo: () => void
  searchAndHighlight: (text: string) => boolean
}

interface RichEditorProps {
  content: string
  onChange?: (markdown: string) => void
  onSelectionChange?: (text: string, from: number, to: number) => void
}

const RichEditor = forwardRef<RichEditorHandle, RichEditorProps>(
  ({ content, onChange, onSelectionChange }, ref) => {
    const initialHtml = useRef(markdownToHtml(content))
    const suppressUpdate = useRef(false)

    const editor = useEditor({
      extensions: [
        StarterKit.configure({
          heading: { levels: [1, 2, 3] },
          codeBlock: { HTMLAttributes: { class: 'code-block' } },
        }),
        Image.configure({
          HTMLAttributes: { class: 'editor-image' },
          allowBase64: true,
        }),
        Link.configure({
          openOnClick: false,
          HTMLAttributes: { class: 'editor-link' },
        }),
        Table.configure({ resizable: false }),
        TableRow,
        TableCell,
        TableHeader,
        Placeholder.configure({
          placeholder: '请输入正文...',
        }),
      ],
      content: initialHtml.current,
      onUpdate: ({ editor: e }) => {
        if (suppressUpdate.current) return
        onChange?.(htmlToMarkdown(e.getHTML()))
      },
      onSelectionUpdate: ({ editor: e }) => {
        const { from, to } = e.state.selection
        const text = e.state.doc.textBetween(from, to, '\n')
        onSelectionChange?.(text, from, to)
      },
    })

    const setMarkdown = useCallback(
      (md: string) => {
        if (!editor) return
        suppressUpdate.current = true
        editor.commands.setContent(markdownToHtml(md))
        suppressUpdate.current = false
      },
      [editor],
    )

    useEffect(() => {
      if (!editor) return
      const currentMd = htmlToMarkdown(editor.getHTML())
      if (currentMd !== content) {
        setMarkdown(content)
      }
    }, [content, editor, setMarkdown])

    useImperativeHandle(
      ref,
      () => ({
        getMarkdown: () => (editor ? htmlToMarkdown(editor.getHTML()) : ''),
        setMarkdown,
        getHTML: () => editor?.getHTML() || '',
        focus: () => editor?.commands.focus(),
        insertImage: (src, alt) => {
          if (!editor) return
          editor.chain().focus().setImage({ src, alt: alt || '' }).run()
        },
        getSelectedText: () => {
          if (!editor) return ''
          const { from, to } = editor.state.selection
          return editor.state.doc.textBetween(from, to, '\n')
        },
        toggleHeading: (level) => {
          editor?.chain().focus().toggleHeading({ level }).run()
        },
        toggleBold: () => {
          editor?.chain().focus().toggleBold().run()
        },
        toggleItalic: () => {
          editor?.chain().focus().toggleItalic().run()
        },
        toggleBulletList: () => {
          editor?.chain().focus().toggleBulletList().run()
        },
        toggleBlockquote: () => {
          editor?.chain().focus().toggleBlockquote().run()
        },
        toggleCodeBlock: () => {
          editor?.chain().focus().toggleCodeBlock().run()
        },
        setHorizontalRule: () => {
          editor?.chain().focus().setHorizontalRule().run()
        },
        insertLink: (href) => {
          editor?.chain().focus().setLink({ href }).run()
        },
        insertTable: (rows = 3, cols = 3) => {
          editor?.chain().focus().insertTable({ rows, cols, withHeaderRow: true }).run()
        },
        undo: () => {
          editor?.chain().focus().undo().run()
        },
        redo: () => {
          editor?.chain().focus().redo().run()
        },
        searchAndHighlight: (text) => {
          if (!editor) return false
          const doc = editor.state.doc
          let found = false
          doc.descendants((node, pos) => {
            if (found || !node.isText) return
            const idx = (node.text ?? '').indexOf(text)
            if (idx >= 0) {
              const from = pos + idx
              const to = from + text.length
              editor.chain().focus().setTextSelection({ from, to }).run()
              const domEl = editor.view.domAtPos(from)?.node as HTMLElement | null
              domEl?.scrollIntoView?.({ behavior: 'smooth', block: 'center' })
              found = true
            }
          })
          if (!found) {
            const plain = text.replace(/\s+/g, ' ').trim()
            const words = plain.split(' ').filter(Boolean)
            if (words.length > 0) {
              const firstWord = words[0].slice(0, 8)
              doc.descendants((node, pos) => {
                if (found || !node.isText) return
                const idx = (node.text ?? '').indexOf(firstWord)
                if (idx >= 0) {
                  editor.chain().focus().setTextSelection(pos + idx).run()
                  const domEl = editor.view.domAtPos(pos + idx)?.node as HTMLElement | null
                  domEl?.scrollIntoView?.({ behavior: 'smooth', block: 'center' })
                  found = true
                }
              })
            }
          }
          return found
        },
      }),
      [editor, setMarkdown],
    )

    return <EditorContent editor={editor} className="rich-editor-content" />
  },
)

RichEditor.displayName = 'RichEditor'
export default RichEditor
