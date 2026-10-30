import DOMPurify, { type Config } from 'dompurify';
import { Marked } from 'marked';

/**
 * Markdown of an answer to safe HTML (plan 05 ruling 16). The model's text is untrusted: a poisoned policy document or question can
 * make it write HTML. So, in two layers:
 * 1. `marked` renders no raw HTML at all: an HTML block or tag is shown as text. Images are shown as their alt text (no tracking pixels).
 * 2. DOMPurify then keeps only the tags and attributes Markdown produces, and links only to http(s), a path or an anchor
 *    (no `javascript:`, `data:` ...). Every link opens in a new tab with `rel="noopener noreferrer"`.
 * Angular's own sanitizer still runs on `[innerHTML]` after this; with this whitelist it has nothing left to remove.
 */
const marked = new Marked({
  gfm: true,
  breaks: false,
  renderer: {
    html: ({ text }) => escapeHtml(text),
    image: ({ text }) => escapeHtml(text),
  },
});

const PURIFY_CONFIG: Config = {
  ALLOWED_TAGS: [
    'p', 'br', 'hr', 'strong', 'b', 'em', 'i', 'del', 's', 'code', 'pre', 'blockquote',
    'ul', 'ol', 'li', 'h1', 'h2', 'h3', 'h4', 'h5', 'h6',
    'table', 'thead', 'tbody', 'tr', 'th', 'td', 'a',
  ],
  ALLOWED_ATTR: ['href', 'title', 'align', 'start'],
  ALLOWED_URI_REGEXP: /^(?:https?:|\/(?!\/)|#)/i,
  ALLOW_DATA_ATTR: false,
  ALLOW_ARIA_ATTR: false,
};

let purifier: ReturnType<typeof DOMPurify> | null = null;

/** One DOMPurify of our own, so its hook does not change how anything else in the page sanitizes. */
function getPurifier(): ReturnType<typeof DOMPurify> {
  if (!purifier) {
    purifier = DOMPurify(window);
    purifier.addHook('afterSanitizeAttributes', (node) => {
      if ((node as Element).tagName === 'A') {
        (node as Element).setAttribute('target', '_blank');
        (node as Element).setAttribute('rel', 'noopener noreferrer');
      }
    });
  }
  return purifier;
}

export function renderMarkdown(markdown: string): string {
  if (markdown.trim() === '') {
    return '';
  }
  const html = marked.parse(markdown, { async: false });
  return getPurifier().sanitize(html, PURIFY_CONFIG);
}

function escapeHtml(text: string): string {
  return text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;').replace(/'/g, '&#39;');
}
