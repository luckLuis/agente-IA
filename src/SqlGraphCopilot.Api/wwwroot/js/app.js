import { api } from './api.js';
import { renderMarkdown, escapeHtml } from './markdown.js';
import { GraphView, KIND_STYLE, kindLabel, kindColor } from './graph-view.js';
import { fetchFlowInputs, mountInputsPanel, buildMigrationPrompt } from './flow-inputs.js';
import { loadStatsPanel } from './stats.js';
import { Mishi, analyzeObject, analyzeSql, fetchProgress } from './mishi.js';
import { fetchDefinition, mountDefinition } from './definition-viewer.js';
import { extractCSharpFiles, hasTests, verifyMigration, isGreen, renderVerifyReport, buildFixPrompt, MAX_AUTO_FIXES } from './migration-verify.js';

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
let mishi = null;   // mascota (Mishi el gato o Bruno el husky)
let petInfo = { name: 'Mishi', emoji: '🐱' };   // se actualiza con el evento mishi:pet
let lastRedundancy = null;   // último informe de redundancias, para dibujar la similitud en la red
const results = {};   // secciones plegables del detalle: { impact, inputs, definition } -> <details>

const MODULE_KINDS = ['StoredProcedure', 'Function', 'Trigger', 'View'];   // objetos con código que se puede leer
const MIGRATABLE_KINDS = ['StoredProcedure', 'Function', 'Trigger'];       // objetos que tiene sentido migrar a C#
const prefersReducedMotion = () => typeof matchMedia === 'function' && matchMedia('(prefers-reduced-motion: reduce)').matches;

// ---- Arranque ----------------------------------------------------------------------------

document.addEventListener('DOMContentLoaded', init);

async function init() {
  graph = new GraphView(el.network, {
    onSelect: (id) => selectNode(id, { fromGraph: true }),
    onDeselect: () => { state.selectedId = null; },
    onExpand: (id) => expandNode(id),
    onCountsChanged: updateCounts
  });

  // Contenedores de los informes del panel de detalle (impacto, datos de entrada, código).
  // Cada uno vive dentro de una sección plegable; solo la última consultada queda abierta.
  el.inputsContent = document.createElement('div');
  el.inputsContent.id = 'inputs-content';
  el.definitionContent = document.createElement('div');
  el.definitionContent.id = 'definition-content';
  el.impactContent.after(el.inputsContent, el.definitionContent);
  results.impact = makeResultShell(el.impactContent, 'Impacto si se modifica');
  results.inputs = makeResultShell(el.inputsContent, 'Datos de entrada');
  results.definition = makeResultShell(el.definitionContent, 'Código T-SQL');

  buildKindControls();
  buildSuggestions();
  wireEvents();
  initMishi();

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

/** Texto del botón que pide a la mascota olfatear el objeto seleccionado. */
const sniffLabel = () => `Que ${petInfo.name} lo huela ${petInfo.emoji}`;

function wireEvents() {
  // La mascota avisa cuando cambia (o al arrancar): se actualizan las etiquetas que llevan su nombre.
  document.addEventListener('mishi:pet', (event) => {
    petInfo = event.detail;
    document.querySelectorAll('[data-sniff-label]').forEach((button) => { button.textContent = sniffLabel(); });
  });

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

  document.body.addEventListener('click', (event) => {
    // Botones de la capa de similitud (panel de redundancias).
    const sim = event.target.closest('[data-sim]');
    if (sim) {
      if (sim.dataset.sim === 'show') showSimilarity(); else hideSimilarity();
      return;
    }

    // Delegación: cualquier elemento con data-node-id enfoca ese nodo (resultados, tablas, listas).
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
  lastRedundancy = null;
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

  // Sin pantalla de carga: la red sigue visible y muestra ondas mientras el servidor clasifica.
  showBanner('Escaneo profundo en curso: clasificando lee / escribe / ejecuta…', 'busy');
  graph.startScan(null);
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
    graph.stopScan();
    showBanner(null);
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

// ---- Secciones plegables de resultados ---------------------------------------------------

/** Envuelve un contenedor de resultados en un <details>; el contenedor sigue siendo donde se dibuja el informe. */
function makeResultShell(container, title) {
  const details = document.createElement('details');
  details.className = 'result';
  details.hidden = true;
  const summary = document.createElement('summary');
  summary.textContent = title;
  container.before(details);
  details.append(summary, container);
  return details;
}

/** Muestra la sección pedida, cierra las demás (queda abierta solo la última consulta) y lleva la vista hasta ella. */
function showResult(name) {
  const shell = results[name];
  for (const other of Object.values(results)) if (other !== shell) other.open = false;
  shell.hidden = false;
  shell.open = true;
  scrollToResult(name);
}

/** Alinea la sección con el borde superior del panel. Se repite al terminar de cargar, porque al principio
 *  solo hay un «Cargando…» y el panel aún no tiene alto suficiente para dejarla arriba. */
function scrollToResult(name) {
  results[name].scrollIntoView({ behavior: prefersReducedMotion() ? 'auto' : 'smooth', block: 'start' });
}

/** Deshabilita el botón y muestra un spinner mientras dura la tarea (evita pulsaciones repetidas). */
async function withBusy(button, task) {
  if (!button || button.classList.contains('is-busy')) return;
  button.classList.add('is-busy');
  button.disabled = true;
  button.setAttribute('aria-busy', 'true');
  try {
    await task();
  } finally {
    button.classList.remove('is-busy');
    button.disabled = false;
    button.removeAttribute('aria-busy');
  }
}

// ---- Panel de detalle ---------------------------------------------------------------------

function selectNode(id, { fromGraph = false } = {}) {
  state.selectedId = id;
  renderDetail(graph.getNode(id));
  // Un clic en la red lleva al detalle, salvo que el usuario esté conversando con el copiloto.
  if (fromGraph && $('tab-copilot').hidden) switchTab('detail');
}

const actionButton = (action, label, style = 'ghost', extra = '') =>
  `<button type="button" class="btn ${style} small" data-action="${action}" ${extra}>${label}</button>`;

function actionGroup(title, buttons) {
  const items = buttons.filter(Boolean);
  return items.length
    ? `<section class="action-group"><h4>${title}</h4><div class="action-grid">${items.join('')}</div></section>`
    : '';
}

function renderDetail(node) {
  for (const shell of Object.values(results)) {
    shell.hidden = true;
    shell.open = true;
  }
  el.impactContent.innerHTML = '';
  el.inputsContent.innerHTML = '';
  el.definitionContent.innerHTML = '';
  if (!node) {
    el.detailEmpty.hidden = false;
    el.detailContent.hidden = true;
    return;
  }

  el.detailEmpty.hidden = true;
  el.detailContent.hidden = false;

  const isModule = MODULE_KINDS.includes(node.kind);
  const fanIn = node.fanIn ?? 0;
  const fanOut = node.fanOut ?? 0;

  const badges = [];
  if (node.isCritical) badges.push('<span class="badge crit">Crítico</span>');
  if (node.isExternal) badges.push('<span class="badge warn">Externo · no expandido</span>');
  if (node.kind === 'Missing') badges.push('<span class="badge crit">No existe</span>');
  if (node.hasDynamicSql) badges.push('<span class="badge warn" title="Construye SQL en tiempo de ejecución: el grafo puede estar incompleto.">SQL dinámico</span>');
  if (node.usesOpenQuery) badges.push('<span class="badge warn">OPENQUERY</span>');
  if (isModule) {
    badges.push(node.isClassified
      ? '<span class="badge info" title="Ya se sabe qué objetos lee, escribe o ejecuta.">Clasificado</span>'
      : '<span class="badge" tabindex="0" title="Aún no se sabe si este objeto lee, escribe o ejecuta a otros, por eso sus relaciones aparecen como «referencia». Use «Escaneo profundo» (arriba) para clasificar toda la base.">Sin clasificar</span>');
  }

  const style = KIND_STYLE[node.kind];
  const icon = style
    ? shapeSvg(style.shape, style.color, ['External', 'LinkedServer', 'Missing'].includes(node.kind))
    : `<span class="dot" style="--c:${kindColor(node.kind)}"></span>`;

  const analyze = actionGroup('Analizar', [
    actionButton('impact', 'Analizar impacto', 'primary'),
    node.kind === 'StoredProcedure' ? actionButton('inputs', 'Datos de entrada', 'primary') : '',
    isModule ? actionButton('mishi', sniffLabel(), 'ghost', 'data-sniff-label') : '',
    isModule ? actionButton('definition', 'Ver código', 'ghost') : ''
  ]);

  const explore = actionGroup('Explorar en la red', [
    actionButton('expand1', 'Expandir 1 nivel'),
    actionButton('expand2', 'Expandir 2 niveles'),
    actionButton('focus', 'Usar como foco'),
    isModule ? actionButton('flow', 'Trazar flujo') : ''
  ]);

  const ask = `
    <details class="action-more">
      <summary>Preguntar al copiloto</summary>
      <div class="action-grid">
        ${actionButton('explain', '¿Qué hace?')}
        ${isModule ? actionButton('logic', 'Explicar lógica') : ''}
        ${actionButton('ask', 'Escribir una pregunta')}
      </div>
    </details>`;

  const migrate = MIGRATABLE_KINDS.includes(node.kind)
    ? actionGroup('Migrar', [actionButton('migrate', 'Migrar a C#', 'primary')])
    : '';

  el.detailContent.innerHTML = `
    <div class="detail-head">
      <span class="detail-icon">${icon}</span>
      <div class="detail-title">
        <div class="detail-name">${escapeHtml(node.schema)}.${escapeHtml(node.name)}</div>
        <div class="detail-sub">${escapeHtml(kindLabel(node.kind))} · ${escapeHtml(node.server)} / ${escapeHtml(node.database)}</div>
      </div>
      <button type="button" class="btn ghost small" data-copy-name title="Copiar ${escapeHtml(node.schema)}.${escapeHtml(node.name)}">Copiar</button>
    </div>
    <div class="badges">${badges.join('')}</div>
    <div class="metrics">
      <button type="button" class="metric metric-btn" data-neighbors="in" aria-expanded="false" ${fanIn ? '' : 'disabled'}>
        <b>${fanIn}</b><span>Lo usan (fan-in)</span>
      </button>
      <button type="button" class="metric metric-btn" data-neighbors="out" aria-expanded="false" ${fanOut ? '' : 'disabled'}>
        <b>${fanOut}</b><span>Usa (fan-out)</span>
      </button>
    </div>
    <div class="neighbor-box" hidden></div>
    ${analyze}${explore}${ask}${migrate}`;

  el.detailContent.querySelectorAll('[data-action]').forEach((button) =>
    button.addEventListener('click', () => onDetailAction(button.dataset.action, node, button)));
  el.detailContent.querySelectorAll('[data-neighbors]').forEach((button) =>
    button.addEventListener('click', () => toggleNeighbors(node, button.dataset.neighbors, button)));
  el.detailContent.querySelector('[data-copy-name]').addEventListener('click', async () => {
    try {
      await navigator.clipboard.writeText(`${node.schema}.${node.name}`);
      toast('Nombre copiado.', 'ok');
    } catch {
      toast('No se pudo copiar.', 'error');
    }
  });
}

/** Lista quién usa al objeto (fan-in) o a quién usa (fan-out), pidiendo solo su vecindario directo. */
async function toggleNeighbors(node, side, button) {
  const box = el.detailContent.querySelector('.neighbor-box');
  const buttons = el.detailContent.querySelectorAll('[data-neighbors]');
  const wasOpen = button.getAttribute('aria-expanded') === 'true';
  buttons.forEach((b) => b.setAttribute('aria-expanded', 'false'));
  if (wasOpen) { box.hidden = true; return; }

  button.setAttribute('aria-expanded', 'true');
  box.hidden = false;
  box.innerHTML = '<div class="muted-block">Cargando…</div>';
  try {
    const subgraph = await api.neighborhood(node.id, 1, side === 'in' ? 'Incoming' : 'Outgoing');
    if (!box.isConnected || button.getAttribute('aria-expanded') !== 'true') return;   // el usuario cambió de objeto o cerró la lista

    const neighbors = (subgraph.nodes ?? []).filter((n) => n.id !== node.id);
    if (neighbors.length === 0) {
      box.innerHTML = '<div class="muted-block">No hay objetos en esa dirección.</div>';
      return;
    }
    const rows = neighbors.slice(0, 60).map((n) => `
      <li><button type="button" data-node-id="${escapeHtml(n.id)}">
        <span class="dot" style="--c:${kindColor(n.kind)}"></span>
        <span class="name">${escapeHtml(n.schema)}.${escapeHtml(n.name)}</span>
        <span class="meta">${escapeHtml(kindLabel(n.kind))}</span>
      </button></li>`).join('');
    const more = neighbors.length > 60 ? `<p class="hint">Se muestran 60 de ${neighbors.length}.</p>` : '';
    box.innerHTML = `
      <div class="neighbor-head">
        <span class="section-title">${side === 'in' ? 'Lo usan' : 'Usa'} (${neighbors.length})</span>
        <button type="button" class="btn ghost small" data-neighbors-merge>Mostrar en la red</button>
      </div>
      <ul class="item-list">${rows}</ul>${more}`;
    box.querySelector('[data-neighbors-merge]').addEventListener('click', () => {
      graph.merge(subgraph, node.id);
      showEmpty(false);
      updateCounts();
      toast(`${neighbors.length} objeto(s) añadidos a la red.`, 'ok');
    });
  } catch (error) {
    box.innerHTML = `<div class="msg error">${escapeHtml(error.message)}</div>`;
  }
}

function onDetailAction(action, node, button) {
  switch (action) {
    case 'expand1': return withBusy(button, () => expandNode(node.id));
    case 'expand2': return withBusy(button, () => expandNode(node.id, { depth: 2 }));
    case 'impact': return withBusy(button, () => runImpact(node));
    case 'flow': return withBusy(button, () => traceFlow(node));
    case 'inputs': return withBusy(button, () => showInputs(node));
    case 'mishi': return withBusy(button, () => smellNode(node));
    case 'definition': return withBusy(button, () => showDefinition(node));
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
      migrateToCSharp(node);
      break;
  }
}

/** Lee el código del objeto y lo muestra resaltado, con números de línea y botón de copiar. */
async function showDefinition(node) {
  showResult('definition');
  el.definitionContent.innerHTML = '<div class="muted-block">Leyendo el código…</div>';
  try {
    const report = await fetchDefinition({ instance: node.server, database: node.database, schema: node.schema, name: node.name });
    if (state.selectedId !== node.id) return;   // el usuario ya eligió otro objeto
    mountDefinition(el.definitionContent, report, {
      onCopy: (ok) => toast(ok ? 'Código copiado.' : 'No se pudo copiar.', ok ? 'ok' : 'error')
    });
    scrollToResult('definition');
  } catch (error) {
    el.definitionContent.innerHTML = `<div class="msg error">${escapeHtml(error.message)}</div>`;
  }
}

async function runImpact(node) {
  showResult('impact');
  el.impactContent.innerHTML = '<div class="muted-block">Calculando impacto…</div>';
  // Mientras el servidor calcula, la red muestra ondas desde el objeto; al llegar el informe se marca por niveles.
  graph.startScan([node.id], { direction: 'in' });
  showBanner(`Analizando el impacto de ${node.schema}.${node.name}…`, 'busy');
  try {
    const report = await api.impact(node.id);
    renderImpact(report);
    scrollToResult('impact');
    graph.stopScan();
    await graph.playTrace(impactLevels(report), { direction: 'both', onStep: traceBanner('Impacto') });
  } catch (error) {
    el.impactContent.innerHTML = `<div class="msg error">${escapeHtml(error.message)}</div>`;
  } finally {
    graph.stopScan();
    showBanner(null);
  }
}

/** Niveles del informe de impacto: [objeto analizado], [directos], [indirectos nivel 2], ... */
function impactLevels(report) {
  const levels = [[report.targetId]];
  for (const affected of report.affected) {
    (levels[affected.depth] ??= []).push(affected.nodeId);
  }
  return Array.from(levels, (ids) => ids ?? []);
}

function traceBanner(title) {
  return ({ level, count }) => showBanner(
    level === 0 ? `${title}: objeto de partida` : `${title} · nivel ${level}: ${count} objeto(s)`,
    'busy');
}

/**
 * Pide al copiloto la migración a C#: solo la lógica (sin SQL en el código; los datos siguen en el SP)
 * y pruebas xUnit. Si el analizador responde, se adjuntan parámetros, ramas y validaciones reales.
 */
async function migrateToCSharp(node) {
  switchTab('copilot');
  sendChat(buildMigrationPrompt(node), { autoVerify: { attempt: 0 }, display: `Migra ${node.schema}.${node.name} a C#: solo la lógica (sin SQL en el código) y pruebas unitarias` });
}

// ---- Mishi-SQL ---------------------------------------------------------------------------

async function initMishi() {
  mishi = new Mishi(document.querySelector('.stage'), {
    escapeHtml,
    renderMarkdown,
    onAnalyzeSql: (sql) => smellSql(sql)
  });
  try {
    mishi.setProgress(await fetchProgress());
    mishi.say('¡Miau! 🐱 Soy Mishi. Elige un SP y pídeme que lo huela.', 7000);
  } catch { /* sin backend de Mishi: la mascota queda en reposo */ }
}

/** Mishi analiza el código de un objeto de la red (sin gastar tokens: usa el árbol sintáctico). */
async function smellNode(node) {
  if (!mishi) return;
  mishi.react('studying', `Mrrr… 📚 Olfateando ${node.schema}.${node.name}…`);
  try {
    mishi.apply(await analyzeObject({ instance: node.server, database: node.database, schema: node.schema, name: node.name }));
    mishi.openDrawer();
  } catch (error) {
    mishi.react('alert', `¡Miau! 🙀 ${error.message}`);
  }
}

/** Mishi analiza SQL pegado por el usuario. */
async function smellSql(sql) {
  mishi.react('studying', 'Mrrr… 📚 Olfateando tu consulta…');
  try {
    mishi.apply(await analyzeSql(sql));
  } catch (error) {
    mishi.react('alert', `¡Miau! 🙀 ${error.message}`);
  }
}

/** Muestra los parámetros del SP y qué rama se ejecuta según los valores de entrada; «Marcar en la red» anima esa rama. */
async function showInputs(node) {
  showResult('inputs');
  el.inputsContent.innerHTML = '<div class="muted-block">Leyendo el código del procedimiento…</div>';
  graph.startScan([node.id]);
  try {
    const report = await fetchFlowInputs({ instance: node.server, database: node.database, schema: node.schema, name: node.name });
    graph.stopScan();
    mountInputsPanel(el.inputsContent, report, {
      escapeHtml,
      database: node.database,
      onPickTarget: (target) => {
        const [id] = graph.findByName(target, node.database);
        if (id) graph.select(id, { focus: true });
        else toast(`${target} no está cargado en la red. Expanda el nodo para verlo.`);
      },
      onSimulate: (chosen, hasValues) => {
        const ids = [];
        let missing = 0;
        for (const { branch } of chosen) {
          if (branch.kind === 'dynamic') continue;
          const [id] = graph.findByName(branch.target, node.database);
          if (!id) missing++;
          else if (id !== node.id && !ids.includes(id)) ids.push(id);
        }
        if (missing) toast(`${missing} destino(s) no están cargados en la red. Expanda el nodo para verlos.`);
        if (ids.length === 0) { toast(hasValues ? 'Con esa entrada no se llega a ningún objeto cargado en la red.' : 'No hay destinos cargados en la red.'); return; }
        graph.playTrace([[node.id], ids], { direction: 'out', onStep: traceBanner('Entrada') }).then(() => showBanner(null));
      }
    });
    scrollToResult('inputs');
  } catch (error) {
    el.inputsContent.innerHTML = `<div class="msg error">${escapeHtml(error.message)}</div>`;
  } finally {
    graph.stopScan();
  }
}

/** Anima el flujo de un SP hacia lo que usa (EXEC, lecturas y escrituras), cargando 3 niveles si hace falta. */
async function traceFlow(node) {
  graph.startScan([node.id]);
  showBanner(`Siguiendo el flujo de ${node.schema}.${node.name}…`, 'busy');
  try {
    const subgraph = await api.neighborhood(node.id, 3, 'Outgoing');
    graph.merge(subgraph, node.id);
    showEmpty(false);
    updateCounts();
    graph.stopScan();
    const played = await graph.traceFlow(node.id, { onStep: traceBanner('Flujo') });
    if (!played) toast('Este objeto no llama ni usa otros objetos en la red cargada.');
  } catch (error) {
    toast(error.message, 'error');
  } finally {
    graph.stopScan();
    showBanner(null);
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
    lastRedundancy = report;
    const duplicates = report.exactDuplicates.map((g) =>
      `<li>${g.names.map((n, i) => `<button type="button" class="row-btn" data-node-id="${escapeHtml(g.nodeIds[i] ?? '')}"><span class="name">${escapeHtml(n)}</span></button>`).join('')}</li>`).join('');
    const similar = report.similarProcedures.map((p) => row(p.firstId,
      `<td class="mono">${escapeHtml(p.firstName)}<br>↔ ${escapeHtml(p.secondName)}</td><td class="num">${Math.round(p.similarity * 100)}%</td><td class="num">${p.sharedDependencies}</td>`));
    return `
      <div class="actions" style="margin-bottom:8px">
        <button type="button" class="btn primary small" data-sim="show">Ver en la red</button>
        <button type="button" class="btn ghost small" data-sim="clear">Quitar de la red</button>
      </div>
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

// ---- Similitud de lógica (SPs duplicados o casi iguales) ------------------------------------

/** Convierte el informe de redundancias en pares {a, b, similarity} para dibujarlos en la red. */
function similarityPairs(report) {
  const pairs = [];
  for (const group of report.exactDuplicates) {
    const ids = group.nodeIds.filter(Boolean);
    for (let i = 0; i < ids.length; i++) {
      for (let j = i + 1; j < ids.length; j++) {
        pairs.push({ a: ids[i], b: ids[j], similarity: 1, exact: true });
      }
    }
  }
  for (const p of report.similarProcedures) {
    pairs.push({ a: p.firstId, b: p.secondId, similarity: p.similarity, shared: p.sharedDependencies });
  }
  return pairs.slice(0, 60);
}

async function showSimilarity() {
  if (!lastRedundancy) return;
  const pairs = similarityPairs(lastRedundancy);
  if (!pairs.length) { toast('No hay pares similares para mostrar.'); return; }

  setLoading('Cargando los SPs similares…');
  try {
    const ids = [...new Set(pairs.flatMap((p) => [p.a, p.b]))];
    const missing = ids.filter((id) => !graph.has(id)).slice(0, 12);
    for (const id of missing) {
      try {
        graph.merge(await api.neighborhood(id, 1, 'Both'), null);
      } catch { /* un objeto que no carga no impide mostrar el resto */ }
    }
    showEmpty(graph.size === 0);
    updateCounts();
    const drawn = graph.setSimilarity(pairs);
    graph.highlight(ids);
    graph.fit();
    updateCounts();
    toast(drawn ? `${drawn} par(es) con lógica similar dibujados en la red.` : 'Los SPs similares no se pudieron cargar en la red.', drawn ? 'ok' : 'error');
  } finally {
    setLoading(null);
  }
}

function hideSimilarity() {
  graph.clearSimilarity();
  graph.clearHighlight();
  updateCounts();
}

// ---- Copiloto -----------------------------------------------------------------------------

async function sendChat(preset, { display = null, autoVerify = null } = {}) {
  const message = (preset ?? el.chatInput.value).trim();
  if (!message || !state.instance || state.busy) return;
  if (!preset) el.chatInput.value = '';

  addChatMessage('user', display ?? message);
  const pending = addChatMessage('bot', '<span class="typing"><i></i><i></i><i></i></span>', true);
  let followUp = null;
  state.busy = true;
  el.chatSend.disabled = true;

  // La red muestra que se está analizando: ondas desde el objeto seleccionado (o desde los hubs).
  // Solo se marca el flujo del objeto; sin objeto seleccionado no hay nada que marcar.
  if (state.selectedId && graph.has(state.selectedId)) graph.startScan([state.selectedId]);

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

    // Si la respuesta trae C# con pruebas xUnit, se ofrece validarlo (o se valida solo durante una migración).
    const files = extractCSharpFiles([...pending.querySelectorAll('pre')].map((pre) => pre.innerText));
    if (files.length && hasTests(files)) {
      const verify = document.createElement('button');
      verify.type = 'button';
      verify.className = 'btn primary small';
      verify.textContent = 'Validar código y pruebas';
      verify.addEventListener('click', () => runVerification(pending, files, { attempt: 0, auto: false, button: verify }));
      foot.appendChild(verify);
      if (autoVerify) followUp = () => runVerification(pending, files, { attempt: autoVerify.attempt, auto: true, button: verify });
    }

    // Al llegar la respuesta, la red marca paso a paso los objetos que menciona.
    graph.stopScan();
    const present = (response.focusNodeIds ?? []).filter((id) => graph.has(id));
    if (present.length) {
      graph.traceIds(present, { onStep: traceBanner('Copiloto') }).then(() => showBanner(null));
    }
  } catch (error) {
    pending.classList.add('error');
    pending.textContent = error.message;
  } finally {
    graph.stopScan();
    state.busy = false;
    el.chatSend.disabled = false;
    el.chatLog.scrollTop = el.chatLog.scrollHeight;
  }
  if (followUp) followUp();
}

/**
 * Compila y ejecuta las pruebas del código generado. Con auto = true, si fallan, pide al copiloto que lo corrija
 * y vuelve a validar (hasta MAX_AUTO_FIXES veces); es el ciclo generar → probar → corregir.
 */
async function runVerification(message, files, { attempt, auto, button }) {
  let box = message.querySelector('.verify-box');
  if (!box) {
    box = document.createElement('div');
    box.className = 'verify-box';
    message.appendChild(box);
  }
  button.disabled = true;
  box.innerHTML = `<div class="verify run">Compilando y ejecutando las pruebas… ${attempt ? `(corrección ${attempt})` : ''}</div>`;
  el.chatLog.scrollTop = el.chatLog.scrollHeight;

  try {
    const report = await verifyMigration(files, attempt);
    box.innerHTML = renderVerifyReport(report, escapeHtml);
    if (isGreen(report)) mishi?.react('happy', `¡Purrr! 😸 ${report.passed} prueba(s) en verde. ¡Migración a salvo!`);
    else mishi?.react('alert', report.compiled ? `¡Miau! 🙀 Fallaron ${report.failed} prueba(s). Voy a revisar con el copiloto.` : '¡Miau! 🙀 El código no compila. Hay que afilar las garras.');
    button.disabled = false;
    button.textContent = 'Validar de nuevo';

    if (!isGreen(report)) {
      const fix = document.createElement('button');
      fix.type = 'button';
      fix.className = 'btn ghost small';
      fix.textContent = 'Pedir corrección al copiloto';
      fix.addEventListener('click', () => sendChat(buildFixPrompt(report, attempt + 1), { display: 'Corrige el código según los errores de las pruebas', autoVerify: { attempt: attempt + 1 } }));
      box.appendChild(fix);
      if (auto && attempt < MAX_AUTO_FIXES) {
        sendChat(buildFixPrompt(report, attempt + 1), { display: `Corrigiendo el código (intento ${attempt + 1} de ${MAX_AUTO_FIXES})…`, autoVerify: { attempt: attempt + 1 } });
      } else if (auto) {
        box.insertAdjacentHTML('beforeend', '<div class="verify-note">Se alcanzó el máximo de correcciones automáticas; revise los errores.</div>');
      }
    } else if (auto) {
      toast('Código migrado y pruebas en verde.', 'ok');
    }
  } catch (error) {
    box.innerHTML = `<div class="verify bad">${escapeHtml(error.message)}</div>`;
    button.disabled = false;
  }
  el.chatLog.scrollTop = el.chatLog.scrollHeight;
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
  graph.traceIds(ids, { onStep: traceBanner('Copiloto') }).then(() => showBanner(null));
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
    ? `${c.nodes} nodos · ${c.edges} relaciones${c.similar ? ` · ${c.similar} similares` : ''}${c.hidden ? ` · ${c.hidden} ocultos por filtros` : ''}${c.clusters ? ` · ${c.clusters} grupos` : ''}`
    : '';
}

function enableScopeControls(enabled) {
  for (const control of [el.searchInput, el.chatInput, el.chatSend, el.hubsBtn, el.fitBtn, el.clearBtn]) {
    control.disabled = !enabled;
  }
}

function switchTab(tab) {
  document.querySelectorAll('.tabs button').forEach((b) => b.setAttribute('aria-selected', String(b.dataset.tab === tab)));
  for (const name of ['detail', 'analysis', 'copilot', 'stats']) $(`tab-${name}`).hidden = name !== tab;
  if (tab === 'stats') loadStatsPanel($('stats-content'), escapeHtml);
}

function setLoading(text) {
  el.loading.hidden = !text;
  if (text) el.loadingText.textContent = text;
}

function showEmpty(show) { el.emptyState.hidden = !show; }

function showBanner(message, kind = '') {
  el.banner.hidden = !message;
  el.banner.className = kind ? `banner ${kind}` : 'banner';
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
