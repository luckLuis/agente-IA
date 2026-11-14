// Validación del código C# que genera el copiloto: lo compila y ejecuta sus pruebas xUnit en el servidor
// (solo desarrollo) y, si algo falla, arma la petición de corrección.

const API_BASE = '/api';   // ajustar si la API cuelga de otra ruta
export const MAX_AUTO_FIXES = 2;

const TYPE_NAME = /(?:public\s+|internal\s+|sealed\s+|static\s+|abstract\s+|partial\s+)*(?:class|record|interface|enum|struct)\s+(\w+)/;
const LOOKS_LIKE_CSHARP = /\b(class|record|interface|enum|struct|namespace)\b/;
const LOOKS_LIKE_SQL = /^\s*(select|insert|update|delete|exec|create|alter|declare|use)\b|\b(create\s+(or\s+alter\s+)?proc(edure)?|alter\s+proc(edure)?)\b/i;

/** Convierte los bloques de código de una respuesta en archivos .cs. Devuelve [] si no hay C#. */
export function extractCSharpFiles(blocks) {
  const files = [];
  const used = new Set();
  blocks.forEach((raw, index) => {
    const code = raw.trim();
    if (!code || LOOKS_LIKE_SQL.test(code) || !LOOKS_LIKE_CSHARP.test(code)) return;
    let name = (TYPE_NAME.exec(code)?.[1] ?? `Bloque${index + 1}`).replace(/[^A-Za-z0-9_]/g, '');
    while (used.has(name.toLowerCase())) name += '_';
    used.add(name.toLowerCase());
    files.push({ name: `${name}.cs`, content: code });
  });
  return files;
}

export const hasTests = (files) => files.some((f) => /\[(Fact|Theory)\b/.test(f.content));

export async function verifyMigration(files, attempt = 0) {
  const response = await fetch(`${API_BASE}/migration/verify`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ files, attempt })
  });
  if (!response.ok) {
    let message = `Error ${response.status}`;
    try { const body = await response.json(); message = body.message ?? body.detail ?? message; } catch { /* sin cuerpo JSON */ }
    throw new Error(message);
  }
  return response.json();
}

export const isGreen = (report) => report.compiled && report.failed === 0 && report.passed > 0;

/** HTML del resultado. */
export function renderVerifyReport(report, esc) {
  if (!report.compiled) {
    return `<div class="verify bad"><b>No compila</b>${report.timedOut ? ' (se canceló por tiempo)' : ''}
      <pre>${esc(report.buildErrors.join('\n') || 'Sin detalle.')}</pre></div>`;
  }
  const failed = report.tests.filter((t) => t.outcome === 'Failed');
  const head = isGreen(report)
    ? `<b>✔ ${report.passed} prueba(s) pasaron</b>`
    : `<b>${report.failed ? '✖' : '⚠'} ${report.passed} pasaron · ${report.failed} fallaron${report.skipped ? ` · ${report.skipped} omitidas` : ''}</b>`;
  const failures = failed.map((t) => `<li><code>${esc(t.name)}</code>${t.message ? `<pre>${esc(t.message)}</pre>` : ''}</li>`).join('');
  const notes = report.notes.map((n) => `<div class="verify-note">${esc(n)}</div>`).join('');
  return `<div class="verify ${isGreen(report) ? 'ok' : 'bad'}">${head}${failures ? `<ul>${failures}</ul>` : ''}${notes}</div>`;
}

/** Petición para que el copiloto corrija. Es corta: las reglas (sin SQL en C#, pruebas) ya viajan en el prompt del sistema. */
export function buildFixPrompt(report, attempt, maxLength = 1500) {
  let text = `Migra de nuevo este procedimiento a C# (solo la lógica, sin SQL en el código) y sus pruebas xUnit, corrigiendo estos errores de la validación (intento ${attempt}):\n`;
  if (!report.compiled) {
    text += `NO COMPILA:\n${report.buildErrors.join('\n')}`;
  } else {
    text += 'PRUEBAS FALLIDAS:\n' + report.tests
      .filter((t) => t.outcome === 'Failed')
      .map((t) => `- ${t.name}: ${(t.message ?? '').replace(/\s+/g, ' ').trim()}`)
      .join('\n');
  }
  return text.length > maxLength ? `${text.slice(0, maxLength - 1)}…` : text;
}
