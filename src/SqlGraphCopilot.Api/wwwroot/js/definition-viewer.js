// Visor del código T-SQL de un objeto: lo pide a la API, lo resalta y ofrece copiarlo.

import { highlightSql } from './sql-highlight.js';

const API_BASE = '/api';

/** Lee el código del objeto (solo metadata del catálogo). Lanza Error con el mensaje de la API si falla. */
export async function fetchDefinition({ instance, database, schema, name }) {
  const query = new URLSearchParams({ instance, database, schema, name });
  const response = await fetch(`${API_BASE}/objects/definition?${query}`);
  if (!response.ok) {
    let message = `Error ${response.status}`;
    try { message = (await response.json()).message ?? message; } catch { /* sin cuerpo JSON */ }
    throw new Error(message);
  }
  return response.json();
}

/**
 * Dibuja el código con números de línea y un botón «Copiar código».
 * onCopy(ok) se llama tras intentar copiar, para que la app muestre un aviso.
 */
export function mountDefinition(container, report, { onCopy } = {}) {
  const code = String(report.definition ?? '').replace(/\r\n?/g, '\n');
  const count = code.split('\n').length;
  const gutter = Array.from({ length: count }, (_, i) => i + 1).join('\n');

  container.innerHTML = `
    <div class="def-toolbar">
      <span class="detail-sub">${count.toLocaleString('es')} línea(s)${report.truncated ? ' · recortado por tamaño' : ''}</span>
      <button type="button" class="btn ghost small" data-def-copy>Copiar código</button>
    </div>
    <div class="def-scroll" tabindex="0" aria-label="Código T-SQL">
      <pre class="def-gutter" aria-hidden="true">${gutter}</pre>
      <pre class="def-code"><code>${highlightSql(code)}</code></pre>
    </div>`;

  container.querySelector('[data-def-copy]').addEventListener('click', async () => {
    let ok = true;
    try { await navigator.clipboard.writeText(code); } catch { ok = false; }
    onCopy?.(ok);
  });
}
