import { api } from './api.js';
import { renderMarkdown, escapeHtml } from './markdown.js';
import { GraphView, KIND_STYLE, kindLabel, kindColor } from './graph-view.js';

// ---- Estado ------------------------------------------------------------------------------

const state = {
  instance: null,
  database: null,
  selectedId: null,
  search: { text: '', kinds: new Set(), page: 1, totalPages: 0 },
  busy: false
};

const $ = (id) => document.getElementById(id);
const el = {
  scopeForm: $('scope-form'),
  instanceSelect: $('instance-select'),
  databaseSelect: $('database-select'),
  loadBtn: $('load-btn'),
  indexStatus: $('index-status'),
  statusObjects: $('status-objects'),
  statusCoverage: $('status-coverage'),
  deepScanBtn: $('deep-scan-btn'),
  refreshBtn: $('refresh-btn'),
  searchInput: $('search-input'),
  searchKinds: $('search-kinds'),
  searchResults: $('search-results'),
  searchMore: $('search-more'),
  kindFilters: $('kind-filters'),
  hopRange: $('hop-range'),
  hopValue: $('hop-value'),
  directionSelect: $('direction-select'),
  clusterSelect: $('cluster-select'),
  physicsToggle: $('physics-toggle'),
  legendNodes: $('legend-nodes'),
  graphTitle: $('graph-title'),
  graphCounts: $('graph-counts'),
  hubsBtn: $('hubs-btn'),
  fitBtn: $('fit-btn'),
  clearBtn: $('clear-btn'),
  network: $('network'),
  emptyState: $('empty-state'),
  banner: $('banner'),
  loading: $('loading'),
  loadingText: $('loading-text'),
  detailEmpty: $('detail-empty'),
  detailContent: $('detail-content'),
  impactContent: $('impact-content'),
  analysisContent: $('analysis-content'),
  chatSuggestions: $('chat-suggestions'),
  chatLog: $('chat-log'),
  chatForm: $('chat-form'),
  chatInput: $('chat-input'),
  chatSend: $('chat-send'),
  dialog: $('external-dialog'),
  dialogTitle: $('external-title'),
  dialogBody: $('external-body'),
  dialogError: $('external-error'),
  dialogConfirm: $('external-confirm'),
  toast: $('toast')
};

const STORAGE_KEY = 'sqlgraphcopilot.scope';
const SUGGESTIONS = [
  'Dame un diagnóstico general',
  '¿Cuáles son los puntos únicos de fallo?',
  '¿Hay SPs duplicados?',
  '¿Dónde puede haber bloqueos?',
  '¿Qué rompe este objeto?',
  'Migra este objeto a C#'
];

let graph;

// ---- Arranque ----------------------------------------------------------------------------

document.addEventListener('DOMContentLoaded', init);

async function init() {
  graph = new GraphView(el.network, {
    onSelect: (id) => selectNode(id, { fromGraph: true }),
    onDeselect: () => { state.selectedId = null; },
    onExpand: (id) => expandNode(id),
    onCountsChanged: updateCounts
  });

  buildKindControls();
  buildSuggestions();
  wireEvents();

  try {
    const instances = await api.instances();
    fillSelect(el.instanceSelect, instances.map((i) => ({ value: i.name, text: `${i.name} · ${i.environment}` })), 'Seleccione instancia');
    if (instances.length === 0) {
      el.instanceSelect.innerHTML = '<option value="">Sin instancias configuradas</option>';
      toast('Configure SqlGraphCopilot:Instances en appsettings / user-secrets.', 'error');
      return;
    }
    const saved = readSavedScope();
    if (saved && instances.some((i) => i.name === saved.instance)) {
      el.instanceSelect.value = saved.instance;
      await onInstanceChange(saved.database);
    }
  } catch (error) {
    toast(`No se pudieron leer las instancias: ${error.message}`, 'error');
  }
}

function wireEvents() {
  el.instanceSelect.addEventListener('change', () => onInstanceChange());
  el.databaseSelect.addEventListener('change', () => { el.loadBtn.disabled = !el.databaseSelect.value; });
  el.scopeForm.addEventListener('submit', (event) => { event.preventDefault(); loadScope(); });

  el.deepScanBtn.addEventListener('click', runDeepScan);
  el.refreshBtn.addEventListener('click', refreshScope);
  el.hubsBtn.addEventListener('click', loadHubs);
  el.fitBtn.addEventListener('click', () => graph.fit());
  el.clearBtn.addEventListener('click', () => { graph.clear(); updateCounts(); showEmpty(true); renderDetail(null); });

  let searchTimer;
  el.searchInput.addEventListener('input', () => {
    clearTimeout(searchTimer);
    searchTimer = setTimeout(() => runSearch(true), 280);
  });
  el.searchMore.addEventListener('click', () => runSearch(false));

  el.hopRange.addEventListener('input', () => {
    const value = Number(el.hopRange.value);
    const all = value >= Number(el.hopRange.max);
    el.hopValue.textContent = all ? 'todos' : value;
    graph.setFilters({ maxHops: all ? Infinity : value });
    updateCounts();
  });
  el.directionSelect.addEventListener('change', () => { graph.setFilters({ direction: el.directionSelect.value }); updateCounts(); });
  el.clusterSelect.addEventListener('change', () => { graph.setFilters({ clusterMode: el.clusterSelect.value }); updateCounts(); });
  el.physicsToggle.addEventListener('change', () => graph.setFilters({ physics: el.physicsToggle.checked }));

  document.querySelectorAll('.tabs button').forEach((button) =>
    button.addEventListener('click', () => switchTab(button.dataset.tab)));

  document.querySelectorAll('[data-analysis]').forEach((button) =>
    button.addEventListener('click', () => runAnalysis(button.dataset.analysis, button)));

  el.chatForm.addEventListener('submit', (event) => { event.preventDefault(); sendChat(); });
  el.chatInput.addEventListener('keydown', (event) => {
    if (event.key === 'Enter' && !event.shiftKey) { event.preventDefault(); sendChat(); }
  });

  document.addEventListener('keydown', (event) => {
    if (event.key === 'Escape' && !el.dialog.open) graph.clearHighlight();
  });

  // Delegación: cualquier elemento con data-node-id enfoca ese nodo (resultados, tablas, listas).
  document.body.addEventListener('click', (event) => {
    const target = event.target.closest('[data-node-id]');
    if (target) focusNode(target.dataset.nodeId);
  });
}

// ---- Selección de alcance (instancia / base) ---------------------------------------------

async function onInstanceChange(preselectDatabase) {
  const instance = el.instanceSelect.value;
  el.databaseSelect.disabled = true;
  el.loadBtn.disabled = true;
  if (!instance) {
    fillSelect(el.databaseSelect, [], 'Seleccione instancia');
    return;
  }

  el.databaseSelect.innerHTML = '<option value="">Leyendo bases…</option>';
  try {
    const databases = await api.databases(instance);
    fillSelect(el.databaseSelect, databases.map((d) => ({ value: d.name, text: d.name })), 'Seleccione base');
    el.databaseSelect.disabled = false;
    if (preselectDatabase && databases.some((d) => d.name === preselectDatabase)) {
      el.databaseSelect.value = preselectDatabase;
      el.loadBtn.disabled = false;
    }
  } catch (error) {
    fillSelect(el.databaseSelect, [], 'Error al leer bases');
    toast(error.message, 'error');
  }
}

async function loadScope() {
  const instance = el.instanceSelect.value;
  const database = el.databaseSelect.value;
  if (!instance || !database) return;

  state.instance = instance;
  state.database = database;
  state.selectedId = null;
  saveScope();

  setLoading('Leyendo catálogo de la base… (la primera vez puede tardar unos segundos)');
  try {
    const summary = await api.summary(instance, database);
    renderStatus(summary);
    graph.clear();
    renderDetail(null);
    el.analysisContent.innerHTML = '';
    await loadHubs();
    enableScopeControls(true);
    el.graphTitle.textContent = `${database} · ${instance}`;
    runSearch(true);
    addChatMessage('system', `Base ${database} cargada. Pregunte por un objeto o un tema.`);
  } catch (error) {
    toast(error.message, 'error');
  } finally {
    setLoading(null);
  }
}

async function loadHubs() {
  if (!state.instance) return;
  setLoading('Cargando los objetos más referenciados…');
  try {
    const subgraph = await api.hubs(state.instance, state.database, 30);
    graph.merge(subgraph);
    showEmpty(graph.size === 0);
    showBanner(subgraph.message);
    updateCounts();
    setTimeout(() => graph.fit(), 600);
  } catch (error) {
    toast(error.message, 'error');
  } finally {
    setLoading(null);
  }
}

async function refreshScope() {
  if (!state.instance) return;
  await api.refresh(state.instance, state.database);
  toast('Catálogo releído.', 'ok');
  await loadScope();
}

async function runDeepScan() {
  if (!state.instance) return;
  const ok = window.confirm('El escaneo profundo lee y analiza TODAS las definiciones de la base (solo metadata). Puede tardar varios minutos en bases grandes. ¿Continuar?');
  if (!ok) return;

  setLoading('Escaneo profundo en curso: clasificando lee / escribe / ejecuta…');
  el.deepScanBtn.disabled = true;
  try {
    const result = await api.deepScan(state.instance, state.database);
    toast(`Escaneo completo: ${result.modulesParsed} módulos en ${(result.elapsedMilliseconds / 1000).toFixed(1)} s` +
      (result.modulesWithErrors ? ` (${result.modulesWithErrors} con errores de parseo)` : '') + '.', 'ok');
    renderStatus(await api.summary(state.instance, state.database));
  } catch (error) {
    toast(error.message, 'error');
  } finally {
    el.deepScanBtn.disabled = false;
    setLoading(null);
  }
}

// ---- Exploración on-demand ----------------------------------------------------------------

async function focusNode(nodeId) {
  if (!nodeId) return;
  if (graph.has(nodeId)) {
    graph.select(nodeId);
    selectNode(nodeId);
    return;
  }
  await expandNode(nodeId, { selectAfter: true });
}

async function expandNode(nodeId, { selectAfter = false, depth = 1 } = {}) {
  const known = graph.getNode(nodeId);
  const crossesBoundary = known && (known.isExternal || known.kind === 'LinkedServer' ||
    known.server !== state.instance || known.database !== state.database);

  if (crossesBoundary) {
    const confirmed = await confirmExternal(known);
    if (!confirmed) return;
  }

  setLoading(crossesBoundary ? 'Cargando solo la rama externa…' : 'Cargando vecinos…');
  try {
    const subgraph = await api.neighborhood(nodeId, depth, el.directionSelect.value);
    const { added } = graph.merge(subgraph, nodeId);
    showEmpty(false);
    showBanner(subgraph.message);
    updateCounts();
    if (selectAfter || crossesBoundary) {
      graph.select(nodeId);
      selectNode(nodeId);
    }
    if (added === 0 && !crossesBoundary) toast('No hay más vecinos para cargar en esa dirección.');
  } catch (error) {
    if (error.status === 422 || error.status === 404) {
      showExternalError(known, error.message);
    } else {
      toast(error.message, 'error');
    }
  } finally {
    setLoading(null);
  }
}

function confirmExternal(node) {
  return new Promise((resolve) => {
    const where = node.kind === 'LinkedServer'
      ? `el servidor vinculado ${node.name}`
      : `la base ${node.database} de la instancia ${node.server}`;
    el.dialogTitle.textContent = 'Cargar rama externa';
    el.dialogBody.textContent = `${node.schema}.${node.name} pertenece a ${where}. Se leerá el catálogo de esa base (si no está en memoria) y se cargará solo el vecindario de este objeto.`;
    el.dialogError.hidden = true;
    el.dialogConfirm.hidden = false;
    el.dialog.returnValue = '';
    el.dialog.addEventListener('close', () => resolve(el.dialog.returnValue === 'confirm'), { once: true });
    el.dialog.showModal();
  });
}

function showExternalError(node, message) {
  el.dialogTitle.textContent = node ? `No se pudo cargar ${node.schema}.${node.name}` : 'No se pudo cargar la rama';
  el.dialogBody.textContent = '';
  el.dialogError.textContent = message;
  el.dialogError.hidden = false;
  el.dialogConfirm.hidden = true;
  el.dialog.showModal();
}

// ---- Panel de detalle ---------------------------------------------------------------------

function selectNode(id, { fromGraph = false } = {}) {
  state.selectedId = id;
  renderDetail(graph.getNode(id));
  // Un clic en la red lleva al detalle, salvo que el usuario esté conversando con el copiloto.
  if (fromGraph && $('tab-copilot').hidden) switchTab('detail');
}

function renderDetail(node) {
  el.impactContent.innerHTML = '';
  if (!node) {
    el.detailEmpty.hidden = false;
    el.detailContent.hidden = true;
    return;
  }

  el.detailEmpty.hidden = true;
  el.detailContent.hidden = false;

  const badges = [];
  if (node.isCritical) badges.push('<span class="badge crit">Crítico</span>');
  if (node.isExternal) badges.push('<span class="badge warn">Externo · no expandido</span>');
  if (node.kind === 'Missing') badges.push('<span class="badge crit">No existe</span>');
  if (node.hasDynamicSql) badges.push('<span class="badge warn">SQL dinámico</span>');
  if (node.usesOpenQuery) badges.push('<span class="badge warn">OPENQUERY</span>');
  if (['StoredProcedure', 'Function', 'Trigger', 'View'].includes(node.kind)) {
    badges.push(node.isClassified ? '<span class="badge info">Clasificado</span>' : '<span class="badge">Sin clasificar</span>');
  }

  el.detailContent.innerHTML = `
    <div class="detail-head">
      <span class="dot" style="--c:${kindColor(node.kind)}"></span>
      <div>
        <div class="detail-name">${escapeHtml(node.schema)}.${escapeHtml(node.name)}</div>
        <div class="detail-sub">${escapeHtml(kindLabel(node.kind))} · ${escapeHtml(node.server)} / ${escapeHtml(node.database)}</div>
      </div>
    </div>
    <div class="badges">${badges.join('')}</div>
    <div class="metrics">
      <div class="metric"><b>${node.fanIn}</b><span>Lo usan (fan-in)</span></div>
      <div class="metric"><b>${node.fanOut}</b><span>Usa (fan-out)</span></div>
    </div>
    <div class="actions">
      <button type="button" class="btn ghost small" data-action="expand1">Expandir 1 nivel</button>
      <button type="button" class="btn ghost small" data-action="expand2">Expandir 2 niveles</button>
      <button type="button" class="btn primary small" data-action="impact">Analizar impacto</button>
      <button type="button" class="btn ghost small" data-action="focus">Usar como foco</button>
      <button type="button" class="btn ghost small" data-action="ask">Preguntar al copiloto</button>
      <button type="button" class="btn ghost small" data-action="explain">¿Qué hace?</button>
      <button type="button" class="btn ghost small" data-action="logic">Explicar lógica</button>
      <button type="button" class="btn primary small" data-action="migrate">Migrar a C#</button>
    </div>`;

  el.detailContent.querySelectorAll('[data-action]').forEach((button) =>
    button.addEventListener('click', () => onDetailAction(button.dataset.action, node)));
}

function onDetailAction(action, node) {
  switch (action) {
    case 'expand1': expandNode(node.id); break;
    case 'expand2': expandNode(node.id, { depth: 2 }); break;
    case 'impact': runImpact(node); break;
    case 'focus':
      graph.setFocus(node.id);
      toast(`Foco: ${node.schema}.${node.name}. Use "Saltos desde el foco" para filtrar.`);
      updateCounts();
      break;
    case 'ask':
      switchTab('copilot');
      el.chatInput.value = '¿Qué pasa si modifico este objeto?';
      el.chatInput.focus();
      break;
    case 'explain':
      switchTab('copilot');
      sendChat('¿Qué hace este objeto y de qué depende?');
      break;
    case 'logic':
      switchTab('copilot');
      sendChat('Explícame la lógica de este procedimiento paso a paso');
      break;
    case 'migrate':
      switchTab('copilot');
      sendChat('Migra este procedimiento a C# con Dapper manteniendo la lógica');
      break;
  }
}

async function runImpact(node) {
  el.impactContent.innerHTML = '<div class="muted-block">Calculando impacto…</div>';
  try {
    const report = await api.impact(node.id);
    renderImpact(report);
    graph.highlight([report.targetId, ...report.affected.map((a) => a.nodeId)]);
  } catch (error) {
    el.impactContent.innerHTML = `<div class="msg error">${escapeHtml(error.message)}</div>`;
  }
}

const RISK_LABELS = { Low: 'Bajo', Medium: 'Medio', High: 'Alto', Critical: 'Crítico' };

function renderImpact(report) {
  const direct = report.affected.filter((a) => a.depth === 1);
  const indirect = report.affected.filter((a) => a.depth > 1);

  const list = (items) => `<ul class="item-list">${items.slice(0, 60).map((a) => `
      <li><button type="button" data-node-id="${escapeHtml(a.nodeId)}">
        <span class="dot" style="--c:${kindColor(a.kind)}"></span>
        <span class="name">${escapeHtml(a.name)}</span>
        <span class="meta">${a.isCritical ? '⚠ ' : ''}${a.isOtherDatabase ? 'otra base · ' : ''}nivel ${a.depth}</span>
      </button></li>`).join('')}</ul>`;

  const data = report.dataImpact.map((d) =>
    `<li><button type="button" data-node-id="${escapeHtml(d.tableId)}"><span class="name">${escapeHtml(d.tableName)}</span><span class="meta">${d.readerNames.length} lector(es)</span></button></li>`).join('');

  el.impactContent.innerHTML = `
    <h3 class="section-title">Impacto si se modifica</h3>
    <div class="risk">
      <span class="risk-badge risk-${report.level}">${RISK_LABELS[report.level] ?? report.level}</span>
      <div class="bar" title="Puntaje ${report.score}/100"><i style="width:${Math.min(100, report.score)}%"></i></div>
      <b>${Math.round(report.score)}</b>
    </div>
    <p class="detail-sub">${report.totalAffected} objeto(s) afectados en cascada · ${report.criticalAffected} crítico(s)</p>
    ${direct.length ? `<h3 class="section-title">Directos (${direct.length})</h3>${list(direct)}` : ''}
    ${indirect.length ? `<h3 class="section-title">Indirectos (${indirect.length})</h3>${list(indirect)}` : ''}
    ${data ? `<h3 class="section-title">Impacto por datos (tablas que escribe)</h3><ul class="item-list">${data}</ul>` : ''}
    ${report.warnings.length ? `<h3 class="section-title">Límites del análisis</h3><ul class="warn-list">${report.warnings.map((w) => `<li>${escapeHtml(w)}</li>`).join('')}</ul>` : ''}`;
}

// ---- Panel de análisis --------------------------------------------------------------------

async function runAnalysis(kind, button) {
  if (!state.instance) { toast('Cargue primero una base.'); return; }
  document.querySelectorAll('[data-analysis]').forEach((b) => b.classList.toggle('active', b === button));
  el.analysisContent.innerHTML = '<div class="muted-block">Analizando…</div>';
  try {
    const result = await api.analysis(kind, state.instance, state.database);
    el.analysisContent.innerHTML = ANALYSIS_RENDERERS[kind](result);
  } catch (error) {
    el.analysisContent.innerHTML = `<div class="msg error">${escapeHtml(error.message)}</div>`;
  }
}

const nameCell = (id, name, kind) =>
  `<td class="mono"><span class="dot" style="--c:${kindColor(kind)};display:inline-block;margin-right:6px"></span>${escapeHtml(name)}</td>`;

const row = (id, cells) => `<tr class="clickable" data-node-id="${escapeHtml(id)}">${cells}</tr>`;

const table = (headers, rows, empty) => rows.length
  ? `<table class="data-table"><thead><tr>${headers.map((h) => `<th class="${h.num ? 'num' : ''}">${h.label}</th>`).join('')}</tr></thead><tbody>${rows.join('')}</tbody></table>`
  : `<div class="muted-block">${empty}</div>`;

const ANALYSIS_RENDERERS = {
  overview(report) {
    const s = report.summary;
    const counts = Object.entries(s.objectCounts).map(([k, v]) => `<span class="badge">${v} ${escapeHtml(kindLabel(k))}</span>`).join('');
    return `
      <div class="badges">${counts}</div>
      <div class="metrics">
        <div class="metric"><b>${s.edgeCount}</b><span>Relaciones</span></div>
        <div class="metric"><b>${Math.round(s.classificationCoverage * 100)}%</b><span>Clasificado</span></div>
        <div class="metric"><b>${s.externalReferenceCount}</b><span>Refs. externas</span></div>
        <div class="metric"><b>${s.brokenReferenceCount}</b><span>Refs. rotas</span></div>
      </div>
      <h3 class="section-title">Puntos únicos de fallo</h3>${ANALYSIS_RENDERERS.spof(report.singlePointsOfFailure)}
      <h3 class="section-title">Tablas con escritura concurrente</h3>${hotspotTable(report.bottlenecks.writeHotspots)}
      ${report.warnings.length ? `<h3 class="section-title">Avisos</h3><ul class="warn-list">${report.warnings.map((w) => `<li>${escapeHtml(w)}</li>`).join('')}</ul>` : ''}`;
  },

  spof(findings) {
    return table(
      [{ label: 'Objeto' }, { label: 'Dependientes', num: true }, { label: 'Motivos' }],
      findings.map((f) => row(f.nodeId, `${nameCell(f.nodeId, f.name, f.kind)}<td class="num">${f.totalDependents}</td><td>${f.reasons.map(escapeHtml).join('<br>')}</td>`)),
      'No se detectaron puntos únicos de fallo.');
  },

  coupling(findings) {
    return table(
      [{ label: 'Objeto' }, { label: 'Lo usan', num: true }, { label: 'Usa', num: true }, { label: 'Inest.', num: true }],
      findings.map((f) => row(f.nodeId, `${nameCell(f.nodeId, f.name, f.kind)}<td class="num">${f.fanIn}</td><td class="num">${f.fanOut}</td><td class="num">${f.instability.toFixed(2)}</td>`)),
      'Sin datos de acoplamiento.');
  },

  redundancy(report) {
    const duplicates = report.exactDuplicates.map((g) =>
      `<li>${g.names.map((n, i) => `<button type="button" class="row-btn" data-node-id="${escapeHtml(g.nodeIds[i] ?? '')}"><span class="name">${escapeHtml(n)}</span></button>`).join('')}</li>`).join('');
    const similar = report.similarProcedures.map((p) => row(p.firstId,
      `<td class="mono">${escapeHtml(p.firstName)}<br>↔ ${escapeHtml(p.secondName)}</td><td class="num">${Math.round(p.similarity * 100)}%</td><td class="num">${p.sharedDependencies}</td>`));
    return `
      <h3 class="section-title">Definiciones idénticas</h3>
      ${duplicates ? `<ul class="item-list">${duplicates}</ul>` : '<div class="muted-block">Ninguna.</div>'}
      <h3 class="section-title">SPs con dependencias casi iguales</h3>
      ${table([{ label: 'Par' }, { label: 'Similitud', num: true }, { label: 'Comunes', num: true }], similar, 'Ninguno sobre el 75% de similitud.')}`;
  },

  bottlenecks(report) {
    const chains = report.deepCallChains.map((c) => row(c.rootId, `<td class="mono">${c.path.map(escapeHtml).join(' → ')}</td><td class="num">${c.depth}</td>`));
    const cycles = report.cycles.map((c) => row(c.nodeIds[0], `<td class="mono">${c.names.map(escapeHtml).join(' ⇄ ')}</td>`));
    const gods = report.godProcedures.map((g) => row(g.nodeId, `${nameCell(g.nodeId, g.name, g.kind)}<td class="num">${g.value}</td>`));
    const dynamic = report.dynamicSqlObjects.map((d) => row(d.nodeId, `${nameCell(d.nodeId, d.name, d.kind)}<td class="num">${d.value}</td>`));
    return `
      <p class="detail-sub">Cobertura de clasificación: ${Math.round(report.classificationCoverage * 100)}%${report.classificationCoverage < 1 ? ' — ejecute el escaneo profundo para resultados completos.' : ''}</p>
      <h3 class="section-title">Tablas escritas por varios SPs</h3>${hotspotTable(report.writeHotspots)}
      <h3 class="section-title">Cadenas de EXEC profundas</h3>${table([{ label: 'Cadena' }, { label: 'Niveles', num: true }], chains, 'Ninguna de 4 niveles o más.')}
      <h3 class="section-title">Ciclos de ejecución</h3>${table([{ label: 'Ciclo' }], cycles, 'Sin ciclos.')}
      <h3 class="section-title">SPs que tocan demasiados objetos</h3>${table([{ label: 'SP' }, { label: 'Objetos', num: true }], gods, 'Ninguno con 15 o más.')}
      <h3 class="section-title">SQL dinámico</h3>${table([{ label: 'Objeto' }, { label: 'Lo usan', num: true }], dynamic, 'Ninguno.')}`;
  },

  'broken-references'(broken) {
    return table(
      [{ label: 'Objeto inexistente' }, { label: 'Usado por' }],
      broken.map((b) => row(b.referencedByIds[0] ?? b.missingId, `<td class="mono">${escapeHtml(b.missingName)}</td><td class="mono">${b.referencedByNames.map(escapeHtml).join('<br>')}</td>`)),
      'No hay referencias rotas.');
  }
};

function hotspotTable(hotspots) {
  return table(
    [{ label: 'Tabla' }, { label: 'Escriben', num: true }, { label: 'Leen', num: true }],
    hotspots.map((h) => row(h.tableId, `<td class="mono">${escapeHtml(h.tableName)}</td><td class="num">${h.writers}</td><td class="num">${h.readers}</td>`)),
    'Ninguna tabla con 2 o más escritores clasificados.');
}

// ---- Copiloto -----------------------------------------------------------------------------

async function sendChat(preset) {
  const message = (preset ?? el.chatInput.value).trim();
  if (!message || !state.instance || state.busy) return;
  if (!preset) el.chatInput.value = '';

  addChatMessage('user', message);
  const pending = addChatMessage('bot', '<span class="typing"><i></i><i></i><i></i></span>', true);
  state.busy = true;
  el.chatSend.disabled = true;

  try {
    const response = await api.chat({
      instance: state.instance,
      database: state.database,
      message,
      focusNodeId: state.selectedId
    });
    pending.innerHTML = renderMarkdown(response.reply);

    // Botón "Copiar código" debajo de cada bloque de código (SQL o C#).
    pending.querySelectorAll('pre').forEach((pre) => {
      const copy = document.createElement('button');
      copy.type = 'button';
      copy.className = 'btn ghost small';
      copy.textContent = 'Copiar código';
      copy.addEventListener('click', async () => {
        try {
          await navigator.clipboard.writeText(pre.innerText);
          toast('Código copiado.', 'ok');
        } catch {
          toast('No se pudo copiar.', 'error');
        }
      });
      pre.after(copy);
    });

    const foot = document.createElement('div');
    foot.className = 'msg-foot';
    const engine = document.createElement('span');
    engine.textContent = response.engine === 'deterministic' ? 'Motor: análisis del grafo' : `Motor: ${response.engine}`;
    foot.appendChild(engine);
    if (response.focusNodeIds?.length) {
      const show = document.createElement('button');
      show.type = 'button';
      show.className = 'btn ghost small';
      show.textContent = `Ver en la red (${response.focusNodeIds.length})`;
      show.addEventListener('click', () => showInGraph(response.focusNodeIds));
      foot.appendChild(show);
    }
    pending.appendChild(foot);
  } catch (error) {
    pending.classList.add('error');
    pending.textContent = error.message;
  } finally {
    state.busy = false;
    el.chatSend.disabled = false;
    el.chatLog.scrollTop = el.chatLog.scrollHeight;
  }
}

/** Carga (si faltan) y resalta los nodos que menciona la respuesta. */
async function showInGraph(ids) {
  const missing = ids.filter((id) => !graph.has(id)).slice(0, 4);
  for (const id of missing) {
    try {
      graph.merge(await api.neighborhood(id, 1, 'Both'), null);
    } catch { /* un objeto que no carga no impide resaltar el resto */ }
  }
  showEmpty(graph.size === 0);
  updateCounts();
  if (ids[0] && graph.has(ids[0])) graph.select(ids[0], { focus: true });
  graph.highlight(ids);
}

function addChatMessage(kind, content, isHtml = false) {
  const message = document.createElement('div');
  message.className = `msg ${kind}`;
  if (isHtml) message.innerHTML = content; else message.textContent = content;
  el.chatLog.appendChild(message);
  el.chatLog.scrollTop = el.chatLog.scrollHeight;
  return message;
}

function buildSuggestions() {
  for (const text of SUGGESTIONS) {
    const chip = document.createElement('button');
    chip.type = 'button';
    chip.className = 'chip';
    chip.textContent = text;
    chip.addEventListener('click', () => sendChat(text));
    el.chatSuggestions.appendChild(chip);
  }
}

// ---- Búsqueda -----------------------------------------------------------------------------

async function runSearch(reset) {
  if (!state.instance) return;
  if (reset) {
    state.search.page = 1;
    el.searchResults.innerHTML = '';
  } else {
    state.search.page++;
  }
  state.search.text = el.searchInput.value.trim();

  try {
    const result = await api.search(state.instance, state.database, {
      text: state.search.text,
      kinds: [...state.search.kinds],
      page: state.search.page
    });
    state.search.totalPages = Math.ceil(result.totalCount / result.pageSize);
    const fragment = document.createDocumentFragment();
    for (const node of result.items) {
      const li = document.createElement('li');
      li.innerHTML = `<button type="button" data-node-id="${escapeHtml(node.id)}">
          <span class="dot" style="--c:${kindColor(node.kind)}"></span>
          <span class="name">${escapeHtml(node.label)}</span>
          <span class="meta">${node.fanIn}↘ ${node.fanOut}↗</span>
        </button>`;
      fragment.appendChild(li);
    }
    el.searchResults.appendChild(fragment);
    if (reset && result.items.length === 0) {
      el.searchResults.innerHTML = '<li class="muted-block">Sin resultados.</li>';
    }
    el.searchMore.hidden = state.search.page >= state.search.totalPages;
  } catch (error) {
    toast(error.message, 'error');
  }
}

// ---- Controles de tipos y leyenda ----------------------------------------------------------

function buildKindControls() {
  for (const [kind, style] of Object.entries(KIND_STYLE)) {
    // Filtro de visibilidad en la red
    const label = document.createElement('label');
    const checkbox = document.createElement('input');
    checkbox.type = 'checkbox';
    checkbox.checked = true;
    checkbox.value = kind;
    checkbox.addEventListener('change', () => {
      const kinds = new Set([...el.kindFilters.querySelectorAll('input:checked')].map((c) => c.value));
      graph.setFilters({ kinds });
      updateCounts();
    });
    label.append(checkbox, document.createTextNode(style.label));
    el.kindFilters.appendChild(label);

    // Chip de filtro para la búsqueda (solo tipos locales)
    if (!['External', 'LinkedServer', 'Missing'].includes(kind)) {
      const chip = document.createElement('button');
      chip.type = 'button';
      chip.className = 'chip';
      chip.textContent = style.label;
      chip.setAttribute('aria-pressed', 'false');
      chip.addEventListener('click', () => {
        const active = chip.getAttribute('aria-pressed') !== 'true';
        chip.setAttribute('aria-pressed', String(active));
        if (active) state.search.kinds.add(kind); else state.search.kinds.delete(kind);
        runSearch(true);
      });
      el.searchKinds.appendChild(chip);
    }

    // Leyenda
    const legend = document.createElement('span');
    legend.innerHTML = `${shapeSvg(style.shape, style.color, ['External', 'LinkedServer', 'Missing'].includes(kind))}${escapeHtml(style.label)}`;
    el.legendNodes.appendChild(legend);
  }
}

function shapeSvg(shape, color, dashed) {
  const stroke = `stroke="${color}" stroke-width="1.6" ${dashed ? 'stroke-dasharray="2 2" fill-opacity="0.25"' : ''} fill="${color}"`;
  const shapes = {
    dot: `<circle cx="7" cy="7" r="5.5" ${stroke}/>`,
    square: `<rect x="2" y="2" width="10" height="10" rx="1" ${stroke}/>`,
    hexagon: `<polygon points="7,1.5 12,4.3 12,9.7 7,12.5 2,9.7 2,4.3" ${stroke}/>`,
    triangle: `<polygon points="7,1.5 12.5,12 1.5,12" ${stroke}/>`,
    triangleDown: `<polygon points="1.5,2 12.5,2 7,12.5" ${stroke}/>`,
    star: `<polygon points="7,1 8.8,5.2 13,5.4 9.7,8.1 10.8,12.6 7,10.1 3.2,12.6 4.3,8.1 1,5.4 5.2,5.2" ${stroke}/>`,
    diamond: `<polygon points="7,1 13,7 7,13 1,7" ${stroke}/>`
  };
  return `<svg viewBox="0 0 14 14" aria-hidden="true">${shapes[shape] ?? shapes.dot}</svg>`;
}

// ---- Utilidades de UI ---------------------------------------------------------------------

function renderStatus(summary) {
  const total = Object.values(summary.objectCounts).reduce((a, b) => a + b, 0);
  el.statusObjects.textContent = `${total.toLocaleString('es')} objetos · ${summary.edgeCount.toLocaleString('es')} relaciones`;
  const coverage = Math.round(summary.classificationCoverage * 100);
  el.statusCoverage.textContent = `Clasificado ${coverage}%`;
  el.statusCoverage.className = `pill ${coverage >= 99 ? 'ok' : 'warn'}`;
  el.indexStatus.hidden = false;
}

function updateCounts() {
  if (!graph) return;
  const c = graph.counts();
  el.graphCounts.textContent = c.nodes
    ? `${c.nodes} nodos · ${c.edges} relaciones${c.hidden ? ` · ${c.hidden} ocultos por filtros` : ''}${c.clusters ? ` · ${c.clusters} grupos` : ''}`
    : '';
}

function enableScopeControls(enabled) {
  for (const control of [el.searchInput, el.chatInput, el.chatSend, el.hubsBtn, el.fitBtn, el.clearBtn]) {
    control.disabled = !enabled;
  }
}

function switchTab(tab) {
  document.querySelectorAll('.tabs button').forEach((b) => b.setAttribute('aria-selected', String(b.dataset.tab === tab)));
  for (const name of ['detail', 'analysis', 'copilot']) $(`tab-${name}`).hidden = name !== tab;
}

function setLoading(text) {
  el.loading.hidden = !text;
  if (text) el.loadingText.textContent = text;
}

function showEmpty(show) { el.emptyState.hidden = !show; }

function showBanner(message) {
  el.banner.hidden = !message;
  if (message) el.banner.textContent = message;
}

let toastTimer;
function toast(message, kind = '') {
  el.toast.textContent = message;
  el.toast.className = `toast ${kind}`;
  el.toast.hidden = false;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => { el.toast.hidden = true; }, kind === 'error' ? 7000 : 4000);
}

function fillSelect(select, options, placeholder) {
  select.innerHTML = '';
  const first = document.createElement('option');
  first.value = '';
  first.textContent = placeholder;
  select.appendChild(first);
  for (const option of options) {
    const item = document.createElement('option');
    item.value = option.value;
    item.textContent = option.text;
    select.appendChild(item);
  }
}

function saveScope() {
  try { localStorage.setItem(STORAGE_KEY, JSON.stringify({ instance: state.instance, database: state.database })); } catch { /* almacenamiento no disponible */ }
}

function readSavedScope() {
  try { return JSON.parse(localStorage.getItem(STORAGE_KEY) || 'null'); } catch { return null; }
}
