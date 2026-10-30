import { keepAmounts, renderMarkdown } from './markdown';

const NBSP = String.fromCharCode(0xa0);

describe('renderMarkdown', () => {
  it('markdown_strips_script_and_event_handlers', () => {
    const html = renderMarkdown(
      [
        '**Bold** and <script>alert(1)</script> text.',
        '',
        '<img src="x" onerror="alert(2)"> <a href="https://evil.example" onclick="alert(3)">raw</a>',
        '',
        '[click](javascript:alert(4)) [data](data:text/html;base64,PHNjcmlwdD4=) [vb](vbscript:msgbox(1))',
        '',
        '<iframe src="https://evil.example"></iframe><style>body{display:none}</style>',
        '',
        '![pic](https://evil.example/track.png)',
      ].join('\n'),
    );
    const doc = new DOMParser().parseFromString(`<div>${html}</div>`, 'text/html');

    expect(doc.querySelector('strong')?.textContent).toBe('Bold');
    expect(doc.querySelectorAll('script, img, iframe, style, object, embed, form').length).toBe(0);
    const attributes = [...doc.querySelectorAll('*')].flatMap((el) => [...el.attributes].map((a) => a.name));
    expect(attributes.filter((name) => name.startsWith('on'))).toEqual([]);
    for (const link of doc.querySelectorAll('a')) {
      expect(link.getAttribute('href') ?? '').not.toMatch(/^\s*(javascript|data|vbscript):/i);
    }
    // Raw HTML is shown as text, never run.
    expect(doc.body.textContent).toContain('<script>alert(1)</script>');
  });

  it('links_open_in_a_new_tab_with_noopener_noreferrer', () => {
    const doc = new DOMParser().parseFromString(renderMarkdown('See [the policy](https://example.com/policy).'), 'text/html');
    const link = doc.querySelector('a')!;
    expect(link.getAttribute('href')).toBe('https://example.com/policy');
    expect(link.getAttribute('rel')).toBe('noopener noreferrer');
    expect(link.getAttribute('target')).toBe('_blank');
  });

  it('renders tables, code and lists', () => {
    const doc = new DOMParser().parseFromString(
      renderMarkdown('| Amount | Approvers |\n|---|---|\n| R 150 000 | Manager |\n\n```\nconst x = 1;\n```\n\n- one\n- two'),
      'text/html',
    );
    expect(doc.querySelector('table th')?.textContent).toBe('Amount');
    expect(doc.querySelector('td')?.textContent).toBe(`R${NBSP}150${NBSP}000`);
    expect(doc.querySelector('pre code')?.textContent).toContain('const x = 1;');
    expect(doc.querySelectorAll('li').length).toBe(2);
  });

  it('an empty text renders nothing', () => {
    expect(renderMarkdown('')).toBe('');
  });

  it('amounts_stay_on_one_line', () => {
    expect(keepAmounts('Budget of IT-200: R 900 000,00; spent R 0,00. Over R 20 000 up to R200 000.')).toBe(
      `Budget of IT-200: R${NBSP}900${NBSP}000,00; spent R${NBSP}0,00. Over R${NBSP}20${NBSP}000 up to R200${NBSP}000.`,
    );
  });
});
