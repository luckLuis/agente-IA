// Panel «Estadísticas»: consumo de tokens, tiempo, ahorro y calidad del agente (datos de GET /api/stats).

const API_BASE = '/api';   // ajustar si la API cuelga de otra ruta

const nf = new Intl.NumberFormat('es');
const fmt = (n) => nf.format(Math.round(n ?? 0));
const fmtMs = (ms) => (ms >= 1000 ? `${(ms / 1000).toFixed(1)} s` : `${Math.round(ms)} ms`);
const fmtMin = (min) => {
  if (min >= 60) return `${Math.floor(min / 60)} h ${Math.round(min % 60)} min`;
  return `${(min ?? 0).toFixed(min < 10 ? 1 : 0)} min`;
};
const fmtUsd = (n) => (n == null ? '—' : `US$ ${n < 0.01 && n > 0 ? n.toFixed(4) : n.toFixed(2)}`);

async function getJson(path, options) {
  const response = await fetch(`${API_BASE}${path}`, options);
  if (!response.ok) throw new Error(`Error ${response.status}`);
  return response;
}

const INTENT_LABEL = { migrate: 'Migrar a C#', explain: 'Explicar lógica', impact: 'Impacto', analysis: 'Análisis', general: 'General' };
const ENGINE_LABEL = (engine) => (engine === 'deterministic' ? 'Grafo (sin LLM)' : engine === 'cache' ? 'Caché' : 'LLM');

export function renderStats(s, esc) {
  const tokensSaved = s.tokensSavedByMinify + s.tokensSavedByAvoidingLlm;
  const approx = s.tokensEstimated ? ' <span class="stat-tag" title="El proveedor no informó el consumo real; se estima por caracteres">estimado</span>' : '';
  const card = (value, label, hint = '', kind = '') =>
    `<div class="stat-card ${kind}"><b>${value}</b><span>${esc(label)}</span>${hint ? `<small>${hint}</small>` : ''}</div>`;

  if (s.queries === 0 && s.verification.runs === 0) {
    return '<div class="muted-block">Aún no hay consultas registradas. Pregunte al copiloto o migre un SP y vuelva aquí.</div>';
  }

  const hero = `
    <div class="stat-grid hero">
      ${card(fmtMin(s.savedMinutes), 'Tiempo ahorrado', `frente a ${fmtMin(s.manualMinutes)} a mano (supuesto)`, 'good')}
      ${card(fmt(tokensSaved), 'Tokens ahorrados', `${fmt(s.tokensSavedByMinify)} por minificar T-SQL · ${fmt(s.tokensSavedByAvoidingLlm)} sin llamar al LLM`, 'good')}
      ${card(`${s.withoutLlmPercent}%`, 'Respuestas sin LLM', `${s.deterministic} del grafo · ${s.cacheHits} de caché`, 'good')}
      ${card(s.verification.runs ? `${s.verification.firstTryRate}%` : '—', 'Migraciones correctas al 1.er intento', s.verification.runs ? `${s.verification.runs} validaciones · ${s.verification.greenAfterFix} se corrigieron solas` : 'sin validaciones aún')}
    </div>`;

  const usage = `
    <h4>Consumo</h4>
    <div class="stat-grid">
      ${card(fmt(s.inputTokens + s.outputTokens), 'Tokens usados' + approx, `${fmt(s.inputTokens)} entrada · ${fmt(s.outputTokens)} salida`)}
      ${card(fmtUsd(s.costUsd), 'Costo estimado', s.costUsd == null ? 'configure los precios en Usage' : `ahorro ≈ ${fmtUsd(s.savedUsd)}`)}
      ${card(fmt(s.queries), 'Consultas', `${s.llmCalls} con LLM`)}
    </div>
    <h4>Rendimiento</h4>
    <div class="stat-grid">
      ${card(fmtMs(s.avgLatencyMs), 'Respuesta media', `p95: ${fmtMs(s.p95LatencyMs)}`)}
      ${card(fmtMs(s.avgDeterministicLatencyMs), 'Con el grafo', 'sin LLM')}
      ${card(fmtMs(s.avgLlmLatencyMs), 'Con LLM', '')}
    </div>`;

  const byIntent = s.byIntent.length ? `
    <h4>Por tipo de tarea</h4>
    <table class="data-table"><thead><tr><th>Tarea</th><th class="num">Veces</th><th class="num">Media</th><th class="num">Tokens</th><th class="num">Ahorro</th></tr></thead><tbody>
      ${s.byIntent.map((i) => `<tr><td>${esc(INTENT_LABEL[i.intent] ?? i.intent)}</td><td class="num">${i.count}</td><td class="num">${fmtMs(i.avgMs)}</td><td class="num">${fmt(i.tokens)}</td><td class="num">${fmtMin(i.savedMinutes)}</td></tr>`).join('')}
    </tbody></table>` : '';

  const recent = s.recent.length ? `
    <h4>Últimas consultas</h4>
    <table class="data-table"><thead><tr><th>Hora</th><th>Tarea</th><th>Motor</th><th class="num">Tiempo</th><th class="num">Tokens</th></tr></thead><tbody>
      ${s.recent.map((e) => `<tr><td>${esc(new Date(e.at).toLocaleTimeString())}</td><td>${esc(INTENT_LABEL[e.intent] ?? e.intent)}</td><td>${esc(ENGINE_LABEL(e.engine))}</td><td class="num">${fmtMs(e.latencyMs)}</td><td class="num">${fmt(e.inputTokens + e.outputTokens)}</td></tr>`).join('')}
    </tbody></table>` : '';

  const minutes = Object.entries(s.assumptions.manualMinutes).map(([k, v]) => `${esc(INTENT_LABEL[k] ?? k)}: ${v} min`).join(' · ');
  const assumptions = `
    <details class="stat-assumptions"><summary>Cómo se calcula</summary>
      <p>El <b>tiempo ahorrado</b> compara lo que tardaría hacer la tarea a mano (supuestos editables en <code>Usage:ManualMinutes</code> — ${minutes}) con el tiempo real de respuesta.
      Es una estimación, no una medición.</p>
      <p>Los <b>tokens ahorrados</b> suman el T-SQL que se quita al minificar (comentarios y espacios) y ${fmt(s.assumptions.avoidedTokensPerDeterministicAnswer)} tokens estimados por cada respuesta que el grafo resolvió sin LLM.</p>
      <p>Los tokens son <b>${s.tokensEstimated ? 'estimados (≈ ' + s.assumptions.charsPerToken + ' caracteres por token)' : 'reales'}</b>${s.tokensEstimated ? ' hasta que el cliente del LLM informe el consumo del proveedor' : ''}.</p>
    </details>`;

  return hero + usage + byIntent + recent + assumptions;
}

export async function loadStatsPanel(container, escapeHtml) {
  container.innerHTML = '<div class="muted-block">Cargando estadísticas…</div>';
  try {
    const summary = await (await getJson('/stats')).json();
    container.innerHTML = `
      <div class="actions stats-actions">
        <button type="button" class="btn ghost small" data-stats="refresh">Actualizar</button>
        <a class="btn ghost small" href="${API_BASE}/stats/export.csv" download>Exportar CSV</a>
        <button type="button" class="btn ghost small" data-stats="reset">Reiniciar</button>
      </div>
      ${renderStats(summary, escapeHtml)}`;
    container.querySelector('[data-stats="refresh"]').addEventListener('click', () => loadStatsPanel(container, escapeHtml));
    container.querySelector('[data-stats="reset"]').addEventListener('click', async () => {
      if (!confirm('¿Borrar todas las estadísticas registradas?')) return;
      await getJson('/stats/reset', { method: 'POST' });
      loadStatsPanel(container, escapeHtml);
    });
  } catch (error) {
    container.innerHTML = `<div class="msg error">${escapeHtml(error.message)}</div>`;
  }
}
