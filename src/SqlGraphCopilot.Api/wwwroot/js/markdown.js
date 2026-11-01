// Markdown mínimo y SEGURO: todo el texto se escapa antes de aplicar formato.
// (El original insertaba la respuesta del servidor con innerHTML sin escapar: XSS.)

const ESCAPES = { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' };
export const escapeHtml = (text) => String(text ?? '').replace(/[&<>"']/g, (c) => ESCAPES[c]);

function inline(text) {
  // 1) Se apartan los segmentos de código, 2) se escapa todo, 3) se aplica negrita/cursiva
  //    (así **negrita con `código` dentro** funciona), 4) se reinsertan los códigos escapados.
  const codes = [];
  const withPlaceholders = String(text).replace(/`([^`]+)`/g, (_, code) => {
    codes.push(code);
    return `\u0000${codes.length - 1}\u0000`;
  });

  return escapeHtml(withPlaceholders)
    .replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>')
    .replace(/(^|[\s(])_([^_\n]+)_(?=$|[\s).,;:])/g, '$1<em>$2</em>')
    .replace(/\u0000(\d+)\u0000/g, (_, index) => `<code>${escapeHtml(codes[Number(index)])}</code>`);
}

function isTableSeparator(line) {
  return /^\s*\|?\s*:?-{3,}:?\s*(\|\s*:?-{3,}:?\s*)*\|?\s*$/.test(line);
}

function splitRow(line) {
  return line.trim().replace(/^\|/, '').replace(/\|$/, '').split('|').map((c) => c.trim());
}

export function renderMarkdown(source) {
  const lines = String(source ?? '').replace(/\r\n/g, '\n').split('\n');
  const html = [];
  let i = 0;

  while (i < lines.length) {
    const line = lines[i];

    if (/^\s*$/.test(line)) { i++; continue; }

    if (line.startsWith('```')) {
      const code = [];
      i++;
      while (i < lines.length && !lines[i].startsWith('```')) code.push(lines[i++]);
      i++;
      html.push(`<pre><code>${escapeHtml(code.join('\n'))}</code></pre>`);
      continue;
    }

    const heading = /^(#{1,4})\s+(.*)$/.exec(line);
    if (heading) {
      const level = Math.min(heading[1].length + 2, 4);
      html.push(`<h${level}>${inline(heading[2])}</h${level}>`);
      i++;
      continue;
    }

    if (line.trim().startsWith('|') && i + 1 < lines.length && isTableSeparator(lines[i + 1])) {
      const header = splitRow(line);
      i += 2;
      const rows = [];
      while (i < lines.length && lines[i].trim().startsWith('|')) rows.push(splitRow(lines[i++]));
      html.push(
        '<table><thead><tr>' + header.map((h) => `<th>${inline(h)}</th>`).join('') + '</tr></thead><tbody>' +
        rows.map((r) => '<tr>' + r.map((c) => `<td>${inline(c)}</td>`).join('') + '</tr>').join('') +
        '</tbody></table>'
      );
      continue;
    }

    if (/^\s*>\s?/.test(line)) {
      const quote = [];
      while (i < lines.length && /^\s*>\s?/.test(lines[i])) quote.push(lines[i++].replace(/^\s*>\s?/, ''));
      html.push(`<blockquote>${inline(quote.join(' '))}</blockquote>`);
      continue;
    }

    if (/^\s*[-*]\s+/.test(line)) {
      const items = [];
      while (i < lines.length && /^\s*[-*]\s+/.test(lines[i])) items.push(lines[i++].replace(/^\s*[-*]\s+/, ''));
      html.push('<ul>' + items.map((t) => `<li>${inline(t)}</li>`).join('') + '</ul>');
      continue;
    }

    if (/^\s*\d+[.)]\s+/.test(line)) {
      const items = [];
      while (i < lines.length && /^\s*\d+[.)]\s+/.test(lines[i])) items.push(lines[i++].replace(/^\s*\d+[.)]\s+/, ''));
      html.push('<ol>' + items.map((t) => `<li>${inline(t)}</li>`).join('') + '</ol>');
      continue;
    }

    const paragraph = [];
    while (i < lines.length && !/^\s*$/.test(lines[i]) && !/^(#{1,4}\s|```|\s*[-*]\s+|\s*\d+[.)]\s+|\s*>|\s*\|)/.test(lines[i])) {
      paragraph.push(lines[i++]);
    }
    if (paragraph.length === 0) { paragraph.push(lines[i++]); }
    html.push(`<p>${paragraph.map(inline).join('<br>')}</p>`);
  }

  return html.join('');
}
