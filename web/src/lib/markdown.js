import { marked } from 'marked'
import DOMPurify from 'dompurify'

marked.setOptions({ breaks: true, gfm: true })

/** Markdown → 消毒后的 HTML（流式打字机期间反复调用） */
export function renderMarkdown(text) {
  const raw = marked.parse(text || '')
  return DOMPurify.sanitize(raw, { ADD_ATTR: ['target'] })
}
