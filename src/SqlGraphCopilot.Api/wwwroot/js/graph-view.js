// Vista de la red con vis-network: estilos por tipo, carga incremental, filtros por tipo y saltos,
// resaltado de vecinos y agrupación (clústeres) para evitar el efecto "nido de abeja".
// Diseño visual: "red neuronal" (nodos con halo, aristas curvas tenues, neurona activa al seleccionar).
// Capa de similitud: líneas punteadas entre SPs con lógica duplicada o casi igual (no afectan saltos ni grados).

export const KIND_STYLE = {
  Table: { label: 'Tabla', color: '#38bdf8', shape: 'dot' },
  View: { label: 'Vista', color: '#2dd4bf', shape: 'square' },
  StoredProcedure: { label: 'Procedimiento', color: '#a78bfa', shape: 'hexagon' },
  Function: { label: 'Función', color: '#f472b6', shape: 'triangle' },
  Trigger: { label: 'Trigger', color: '#a3e635', shape: 'triangleDown' },
  Synonym: { label: 'Sinónimo', color: '#94a3b8', shape: 'star' },
  External: { label: 'Externo', color: '#fb923c', shape: 'dot' },
  LinkedServer: { label: 'Servidor vinculado', color: '#f97316', shape: 'diamond' },
  Missing: { label: 'Inexistente', color: '#64748b', shape: 'dot' }
};

const EDGE_COLORS = {
  writes: '#fb7185',
  reads: '#38bdf8',
  exec: '#a78bfa',
  trigger: '#a3e635',
  synonym: '#94a3b8',
  ref: '#5b7ba6',
  bridge: '#f59e0b'
};

const RELATION_LABELS = {
  Writes: 'escribe', Reads: 'lee', Executes: 'ejecuta', Calls: 'llama',
  TriggeredBy: 'trigger sobre', SynonymFor: 'sinónimo de', References: 'referencia'
};

const AUTO_CLUSTER_THRESHOLD = 120;
const PHYSICS_OFF_THRESHOLD = 450;
const MIN_CLUSTER_SIZE = 3;

// Parámetros visuales de la red neuronal
const EDGE_BASE_OPACITY = 0.38;     // sinapsis en reposo: tenues
const EDGE_ACTIVE_OPACITY = 1;      // sinapsis de la neurona activa
const EDGE_DIM_OPACITY = 0.04;      // sinapsis apagadas
const NODE_DIM_OPACITY = 0.12;      // neuronas apagadas
const EDGE_GLOW_MAX_EDGES = 400;    // por encima se desactiva el brillo de aristas (rendimiento)
const NODE_FONT_FACE = 'Inter, Segoe UI, system-ui, sans-serif';
const SIM_COLOR = '#e879f9';        // lógica similar / duplicada

// Traza animada del análisis (overlay dibujado sobre el canvas de la red)
const FX_SCAN_STEP_MS = 420;
const FX_LEVEL_COLORS = ['#ffffff', '#00e5ff', '#5fffc1', '#facc15', '#fb923c', '#ff536e'];
const REDUCED_MOTION = typeof matchMedia === 'function' && matchMedia('(prefers-reduced-motion: reduce)').matches;
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

export class GraphView {
  constructor(container, handlers = {}) {
    this.container = container;
    this.handlers = handlers;
    this.meta = new Map();       // id -> NodeDto
    this.edgeMeta = new Map();   // edgeId -> EdgeDto
    this.simEdges = new Map();   // simId -> { a, b, similarity, shared, exact } (capa aparte)
    this.simNodeIds = new Set();
    this.fx = null;              // animación activa: { mode: 'scan' | 'trace', ... }
    this.fxRaf = null;
    this.fxFrame = 0;
    this.fxToken = 0;
    this.signals = [];
    this.ambientRaf = null;
    this.clusterIds = new Set();
    this.highlighted = null;
    this.focusId = null;
    this.filters = { kinds: new Set(Object.keys(KIND_STYLE)), maxHops: Infinity, direction: 'Both', clusterMode: 'auto', physics: true };

    this.nodes = new vis.DataSet();
    this.edges = new vis.DataSet();
    this.network = new vis.Network(container, { nodes: this.nodes, edges: this.edges }, this.#options());
    this.#wireEvents();
  }

  get size() { return this.meta.size; }
  has(id) { return this.meta.has(id); }
  getNode(id) { return this.meta.get(id); }

  clear() {
    this.stopFx();
    this.#openAllClusters();
    this.nodes.clear();
    this.edges.clear();
    this.meta.clear();
    this.edgeMeta.clear();
    this.simEdges.clear();
    this.simNodeIds.clear();
    this.highlighted = null;
    this.focusId = null;
  }

  /** Agrega un subgrafo. Un nodo externo que llega como real se reemplaza (mismo Id). */
  merge(subgraph, anchorId = null) {
    this.#openAllClusters();
    const anchor = anchorId && this.meta.has(anchorId) ? this.network.getPositions([anchorId])[anchorId] : null;
    const touched = new Set();
    const newIds = new Set();

    for (const dto of subgraph.nodes) {
      const existing = this.meta.get(dto.id);
      // Un nodo real nunca se degrada a externo.
      if (existing && !existing.isExternal && dto.isExternal) continue;
      if (!existing) newIds.add(dto.id);
      this.meta.set(dto.id, { ...dto });
      touched.add(dto.id);
    }

    const edgeIds = new Set();
    for (const edge of subgraph.edges) {
      const id = `${edge.from}→${edge.to}`;
      this.edgeMeta.set(id, edge);
      edgeIds.add(id);
      touched.add(edge.from);
      touched.add(edge.to);
    }

    // Si existe la arista inversa (A→B y B→A) ambas se curvan para no solaparse.
    const edgeUpdates = [];
    for (const id of edgeIds) {
      const edge = this.edgeMeta.get(id);
      const reverseId = `${edge.to}→${edge.from}`;
      const hasReverse = edge.from !== edge.to && this.edgeMeta.has(reverseId);
      edgeUpdates.push({ id, from: edge.from, to: edge.to, ...edgeVisual(edge, this.meta.get(edge.from), this.meta.get(edge.to), hasReverse) });
      if (hasReverse && !edgeIds.has(reverseId)) {
        const other = this.edgeMeta.get(reverseId);
        edgeUpdates.push({ id: reverseId, from: other.from, to: other.to, ...edgeVisual(other, this.meta.get(other.from), this.meta.get(other.to), true) });
      }
    }

    // "＋" se calcula en el cliente: grado total conocido vs. aristas ya cargadas.
    const loadedDegree = new Map();
    for (const edge of this.edgeMeta.values()) {
      loadedDegree.set(edge.from, (loadedDegree.get(edge.from) || 0) + 1);
      loadedDegree.set(edge.to, (loadedDegree.get(edge.to) || 0) + 1);
    }

    const nodeUpdates = [];
    for (const id of touched) {
      const dto = this.meta.get(id);
      if (!dto) continue;
      dto.hasMore = dto.kind !== 'LinkedServer' &&
        (dto.isExternal || (dto.fanIn + dto.fanOut) > (loadedDegree.get(id) || 0));
      const visual = nodeVisual(dto);
      if (this.simNodeIds.has(id)) markSimilar(visual);
      if (newIds.has(id) && anchor) {
        visual.x = anchor.x + (Math.random() - 0.5) * 160;
        visual.y = anchor.y + (Math.random() - 0.5) * 160;
      }
      nodeUpdates.push(visual);
    }

    this.nodes.update(nodeUpdates);
    this.edges.update(edgeUpdates);
    this.#tuneEdgeGlow();
    this.applyView();
    return { added: newIds.size, total: this.meta.size };
  }

  setFilters(partial) {
    Object.assign(this.filters, partial);
    if ('physics' in partial) this.network.setOptions({ physics: { enabled: partial.physics } });
    this.applyView();
  }

  setFocus(id) {
    this.focusId = id;
    this.applyView();
  }

  /** Recalcula visibilidad (tipo + saltos) y vuelve a agrupar. */
  applyView() {
    this.#openAllClusters();
    const hops = this.#hopDistances();
    const updates = [];
    let hidden = 0;

    for (const [id, dto] of this.meta) {
      const kindVisible = this.filters.kinds.has(dto.kind);
      const hopVisible = !hops || hops.has(id);
      const isHidden = !(kindVisible && hopVisible) && id !== this.focusId;
      if (isHidden) hidden++;
      updates.push({ id, hidden: isHidden });
    }
    this.nodes.update(updates);
    this.hiddenCount = hidden;
    this.#cluster();
    if (this.highlighted) this.highlight(this.highlighted);
  }

  select(id, { focus = true } = {}) {
    if (!this.meta.has(id)) return;
    this.#openClusterContaining(id);
    this.network.selectNodes([id]);
    if (focus) this.network.focus(id, { scale: Math.max(this.network.getScale(), 0.9), animation: { duration: 450, easingFunction: 'easeInOutQuad' } });
    this.highlight([id, ...this.network.getConnectedNodes(id)]);
  }

  /** Atenúa todo lo que no está en ids (resaltado tipo "neurona activa"). */
  highlight(ids) {
    const set = new Set(ids.filter((id) => this.meta.has(id)));
    this.highlighted = [...set];
    const nodeUpdates = [];
    for (const [id, dto] of this.meta) {
      const active = set.size === 0 || set.has(id);
      const isCore = set.size > 0 && ids[0] === id;   // la neurona seleccionada brilla más
      nodeUpdates.push({
        id,
        opacity: active ? 1 : NODE_DIM_OPACITY,
        font: nodeFont(active),
        shadow: nodeShadow(dto, active, isCore, this.simNodeIds.has(id))
      });
    }
    this.nodes.update(nodeUpdates);

    const edgeUpdates = [];
    for (const [id, edge] of this.edgeMeta) {
      const base = edgeVisual(edge, this.meta.get(edge.from), this.meta.get(edge.to));
      let opacity = EDGE_BASE_OPACITY;
      if (set.size > 0) {
        const active = set.has(edge.from) && set.has(edge.to);
        opacity = active ? EDGE_ACTIVE_OPACITY : EDGE_DIM_OPACITY;
      }
      edgeUpdates.push({ id, color: { ...base.color, opacity } });
    }
    for (const [id, pair] of this.simEdges) {
      const active = set.size === 0 || (set.has(pair.a) && set.has(pair.b));
      edgeUpdates.push({ id, color: simEdgeColor(active ? 0.9 : 0.05) });
    }
    this.edges.update(edgeUpdates);
  }

  clearHighlight() {
    this.stopFx();
    this.highlighted = null;
    this.highlight([]);
    this.highlighted = null;
    this.network.unselectAll();
  }

  /**
   * Dibuja líneas punteadas entre SPs con lógica parecida o idéntica.
   * pairs: [{ a: nodeId, b: nodeId, similarity: 0..1, shared?: number, exact?: boolean }]
   * Solo se dibujan los pares cuyos dos nodos ya están en la red. Devuelve cuántas líneas se dibujaron.
   */
  setSimilarity(pairs) {
    this.clearSimilarity();
    const edgeUpdates = [];

    for (const pair of pairs) {
      if (!pair.a || !pair.b || pair.a === pair.b || !this.meta.has(pair.a) || !this.meta.has(pair.b)) continue;
      const id = pair.a < pair.b ? `sim:${pair.a}↔${pair.b}` : `sim:${pair.b}↔${pair.a}`;
      if (this.simEdges.has(id)) continue;

      const similarity = pair.similarity ?? 1;
      this.simEdges.set(id, pair);
      this.simNodeIds.add(pair.a);
      this.simNodeIds.add(pair.b);
      edgeUpdates.push({
        id,
        from: pair.a,
        to: pair.b,
        arrows: { to: { enabled: false }, from: { enabled: false } },
        dashes: [2, 5],
        width: 1.2 + similarity * 2.2,
        color: simEdgeColor(0.9),
        smooth: { enabled: true, type: 'curvedCW', roundness: 0.3 },
        physics: false,
        label: `${Math.round(similarity * 100)}%`,
        font: { color: SIM_COLOR, size: 10, face: NODE_FONT_FACE, strokeWidth: 3, strokeColor: '#050914', align: 'middle' },
        title: textTooltip([
          `${this.meta.get(pair.a).label}`,
          `${pair.exact ? 'definición idéntica' : `lógica similar (${Math.round(similarity * 100)}%)`}${pair.shared ? ` · ${pair.shared} dependencias en común` : ''}`,
          `${this.meta.get(pair.b).label}`
        ])
      });
    }

    const nodeUpdates = [];
    for (const id of this.simNodeIds) {
      const dto = this.meta.get(id);
      if (dto) nodeUpdates.push(markSimilar(nodeVisual(dto)));
    }
    this.edges.update(edgeUpdates);
    this.nodes.update(nodeUpdates);
    return edgeUpdates.length;
  }

  clearSimilarity() {
    if (this.simEdges.size === 0 && this.simNodeIds.size === 0) return;
    this.edges.remove([...this.simEdges.keys()]);
    const restored = [];
    for (const id of this.simNodeIds) {
      const dto = this.meta.get(id);
      if (dto) restored.push(nodeVisual(dto));
    }
    this.simEdges.clear();
    this.simNodeIds.clear();
    this.nodes.update(restored);
    if (this.highlighted) this.highlight(this.highlighted);
  }

  // ---- Traza animada del análisis ------------------------------------------------------------

  /**
   * Ondas continuas desde uno o varios objetos mientras se espera un resultado.
   * Solo dibuja encima de la red: no cambia nodos, aristas ni resaltados.
   */
  startScan(rootIds = null, { direction = 'out', maxDepth = 6 } = {}) {
    if (REDUCED_MOTION || this.meta.size === 0) return;
    let roots = (rootIds ?? []).filter((id) => this.meta.has(id));
    // Con objeto: solo se marca su flujo y el resto de la red se atenúa. Sin objeto (escaneo profundo): ondas globales.
    const focused = roots.length > 0;
    if (!focused) {
      roots = [...this.meta.values()].sort((a, b) => (b.fanIn || 0) - (a.fanIn || 0)).slice(0, 3).map((n) => n.id);
    }
    this.stopFx();
    const dir = focused ? direction : 'both';
    const { dist } = this.#bfsLevels(roots, dir, focused ? maxDepth : 8);
    this.fx = this.#buildFx('scan', dist, dir, { roots, focused });
    this.#startFxLoop();
  }

  stopScan() {
    if (this.fx?.mode === 'scan') this.stopFx();
  }

  /** Detiene cualquier animación o marca de traza. */
  stopFx() {
    this.fxToken++;
    if (this.fxRaf) cancelAnimationFrame(this.fxRaf);
    this.fxRaf = null;
    const hadFx = !!this.fx;
    this.fx = null;
    if (hadFx) this.network.redraw();
  }

  /**
   * Revela la red por niveles: primero el objeto de partida y luego cada nivel, con un pulso de luz
   * que viaja por las aristas. levels: string[][] (índice 0 = objeto analizado).
   * direction: 'out' (sigue las llamadas), 'in' (sigue a quien lo usa) o 'both'.
   * Devuelve true si terminó; false si no había nada que mostrar o se interrumpió.
   */
  async playTrace(levels, { direction = 'both', stepMs = null, onStep = null } = {}) {
    const lists = levels
      .map((ids) => (ids || []).filter((id) => this.meta.has(id)))
      .filter((ids) => ids.length > 0);
    if (lists.length === 0) return false;

    this.stopFx();
    const token = this.fxToken;
    const dist = new Map();
    lists.forEach((ids, level) => ids.forEach((id) => dist.set(id, level)));

    const fx = this.#buildFx('trace', dist, direction, {});
    fx.stepMs = REDUCED_MOTION ? 0 : (stepMs ?? Math.max(180, Math.min(500, 3000 / lists.length)));
    this.fx = fx;
    this.#startFxLoop();

    const revealed = [];
    for (let level = 0; level < lists.length; level++) {
      fx.level = level;
      fx.stepStart = performance.now();
      revealed.push(...lists[level]);
      this.highlight([...revealed]);
      onStep?.({ level, count: lists[level].length, total: lists.length - 1, revealed: revealed.length });
      if (fx.stepMs > 0) await sleep(fx.stepMs);
      if (token !== this.fxToken) return false;
    }
    fx.done = true;      // las marcas por nivel quedan hasta que se limpie el resaltado
    this.network.redraw();
    return true;
  }

  /** Sigue el flujo hacia lo que el objeto usa (EXEC, lecturas y escrituras ya cargadas en la red). */
  traceFlow(rootId, { maxDepth = 8, ...options } = {}) {
    if (!this.meta.has(rootId)) return Promise.resolve(false);
    const { dist } = this.#bfsLevels([rootId], 'out', maxDepth);
    const levels = [];
    for (const [id, depth] of dist) (levels[depth] ??= []).push(id);
    if (levels.length < 2) return Promise.resolve(false);
    return this.playTrace(levels, { ...options, direction: 'out' });
  }

  /** Revela un conjunto de objetos ordenados por distancia al primero (p. ej. la respuesta del copiloto). */
  traceIds(ids, options = {}) {
    const present = [...new Set(ids)].filter((id) => this.meta.has(id));
    if (present.length === 0) return Promise.resolve(false);
    const { dist } = this.#bfsLevels([present[0]], 'both', 12);
    const wanted = new Set(present);
    let deepest = 0;
    for (const [id, depth] of dist) if (wanted.has(id)) deepest = Math.max(deepest, depth);
    const levels = [];
    for (const id of present) (levels[dist.has(id) ? dist.get(id) : deepest + 1] ??= []).push(id);
    return this.playTrace(levels, { direction: 'both', ...options });
  }

  /** Ids de los nodos cargados con ese nombre calificado («dbo.sp_X»), opcionalmente de una base concreta. */
  findByName(qualified, database = null) {
    const wanted = qualified.toLowerCase();
    return [...this.meta.values()]
      .filter((n) => `${n.schema}.${n.name}`.toLowerCase() === wanted && (!database || n.database === database))
      .map((n) => n.id);
  }

  fit() {
    this.network.fit({ animation: { duration: 500, easingFunction: 'easeInOutQuad' } });
  }

  counts() {
    return { nodes: this.meta.size, edges: this.edgeMeta.size, similar: this.simEdges.size, hidden: this.hiddenCount || 0, clusters: this.clusterIds.size };
  }

  // ---------------------------------------------------------------------------------------

  #options() {
    return {
      autoResize: true,
      layout: { improvedLayout: false },
      physics: {
        enabled: true,
        solver: 'forceAtlas2Based',
        forceAtlas2Based: { gravitationalConstant: -70, centralGravity: 0.01, springLength: 130, springConstant: 0.05, damping: 0.4, avoidOverlap: 0.6 },
        maxVelocity: 40,
        minVelocity: 0.6,
        stabilization: { enabled: true, iterations: 220, updateInterval: 30 }
      },
      interaction: { hover: true, tooltipDelay: 120, hideEdgesOnDrag: true, multiselect: false, navigationButtons: false, keyboard: false },
      edges: {
        arrows: { to: { enabled: true, scaleFactor: 0.45, type: 'arrow' } },
        // Sinapsis curvas, como en un tejido neuronal.
        smooth: { enabled: true, type: 'dynamic', roundness: 0.35 },
        selectionWidth: 1.5,
        hoverWidth: 1
      },
      nodes: { borderWidthSelected: 4, chosen: true }
    };
  }

  #wireEvents() {
    this.network.on('selectNode', (params) => {
      const id = params.nodes[0];
      if (this.network.isCluster(id)) return;
      this.stopFx();
      this.highlight([id, ...this.network.getConnectedNodes(id)]);
      this.handlers.onSelect?.(id);
    });

    this.network.on('deselectNode', () => {
      this.stopFx();
      this.highlight([]);
      this.highlighted = null;
      this.handlers.onDeselect?.();
    });

    this.network.on('doubleClick', (params) => {
      const id = params.nodes[0];
      if (!id) return;
      if (this.network.isCluster(id)) {
        this.network.openCluster(id);
        this.clusterIds.delete(id);
        this.handlers.onCountsChanged?.();
        return;
      }
      this.handlers.onExpand?.(id);
    });

    this.network.on('beforeDrawing', (ctx) => this.#drawDendrites(ctx));
    this.network.on('afterDrawing', (ctx) => { this.#drawAmbient(ctx); this.#drawFx(ctx); });
    this.#startAmbient();

    this.network.on('stabilizationIterationsDone', () => {
      if (!this.filters.physics || this.meta.size > PHYSICS_OFF_THRESHOLD) {
        this.network.setOptions({ physics: { enabled: false } });
      }
    });
  }

  // ---- Animación: cálculo y dibujo ------------------------------------------------------------

  /** Distancias desde las raíces sobre las aristas ya cargadas. direction: 'out' | 'in' | 'both'. */
  #bfsLevels(roots, direction, maxDepth) {
    const outgoing = new Map();
    const incoming = new Map();
    for (const edge of this.edgeMeta.values()) {
      if (!outgoing.has(edge.from)) outgoing.set(edge.from, []);
      if (!incoming.has(edge.to)) incoming.set(edge.to, []);
      outgoing.get(edge.from).push(edge.to);
      incoming.get(edge.to).push(edge.from);
    }

    const dist = new Map(roots.map((id) => [id, 0]));
    const queue = [...roots];
    while (queue.length) {
      const current = queue.shift();
      const depth = dist.get(current);
      if (depth >= maxDepth) continue;
      const next = [
        ...(direction !== 'in' ? outgoing.get(current) || [] : []),
        ...(direction !== 'out' ? incoming.get(current) || [] : [])
      ];
      for (const id of next) {
        if (!dist.has(id)) {
          dist.set(id, depth + 1);
          queue.push(id);
        }
      }
    }
    return { dist };
  }

  #buildFx(mode, dist, direction, extra) {
    // pairs: nivel destino -> [[origen, destino]]; el pulso viaja del nivel menor al mayor.
    const pairs = new Map();
    const add = (level, from, to) => {
      if (!pairs.has(level)) pairs.set(level, []);
      pairs.get(level).push([from, to]);
    };
    for (const edge of this.edgeMeta.values()) {
      const fromLevel = dist.get(edge.from);
      const toLevel = dist.get(edge.to);
      if (fromLevel === undefined || toLevel === undefined) continue;
      if (direction !== 'in' && toLevel === fromLevel + 1) add(toLevel, edge.from, edge.to);
      else if (direction !== 'out' && fromLevel === toLevel + 1) add(fromLevel, edge.to, edge.from);
    }

    let maxLevel = 0;
    for (const level of dist.values()) maxLevel = Math.max(maxLevel, level);
    const now = performance.now();
    return { mode, dist, pairs, maxLevel, t0: now, stepStart: now, stepMs: 400, level: -1, done: false, roots: [], ...extra };
  }

  #startFxLoop() {
    if (this.fxRaf) return;
    const tick = () => {
      if (!this.fx || this.fx.done) { this.fxRaf = null; return; }
      this.fxFrame = (this.fxFrame + 1) % 1000;
      // En redes grandes se redibuja a la mitad de cuadros para no bloquear la interfaz.
      if (this.meta.size <= 300 || this.fxFrame % 2 === 0) this.network.redraw();
      this.fxRaf = requestAnimationFrame(tick);
    };
    this.fxRaf = requestAnimationFrame(tick);
  }

  /** Posición de cada nodo; si está dentro de un clúster, la del clúster que lo contiene. */
  #positions(ids) {
    if (this.clusterIds.size === 0) return this.network.getPositions(ids);
    const result = {};
    for (const id of ids) {
      const chain = this.network.findNode(id);
      const holder = chain[chain.length - 1];
      result[id] = this.network.getPositions([holder])[holder];
    }
    return result;
  }

  /** Punto de la arista a..b en la fracción t (0 = a, 1 = b), siguiendo la curva real si está disponible. */
  #edgePoint(a, b, t, positions) {
    const forwardId = `${a}→${b}`;
    const forward = this.edgeMeta.has(forwardId);
    const body = this.network.body?.edges?.[forward ? forwardId : `${b}→${a}`];
    if (body && typeof body.getPoint === 'function') {
      try {
        const point = body.getPoint(forward ? t : 1 - t);
        if (point && Number.isFinite(point.x) && Number.isFinite(point.y)) return point;
      } catch { /* sin curva disponible: se usa la recta */ }
    }
    const pa = positions[a];
    const pb = positions[b];
    if (!pa || !pb) return null;
    return { x: pa.x + (pb.x - pa.x) * t, y: pa.y + (pb.y - pa.y) * t };
  }

  /** Dibuja el tramo 0..upto de la arista a→b siguiendo su curva, con halo. */
  #strokeEdge(ctx, a, b, positions, upto, color, alpha) {
    const steps = 14;
    const points = [];
    for (let i = 0; i <= steps; i++) {
      const t = (i / steps) * upto;
      const point = this.#edgePoint(a, b, t, positions);
      if (point) points.push(point);
    }
    if (points.length < 2) return;
    for (const [width, a2] of [[6, 0.12], [2.2, 1]]) {
      ctx.lineWidth = width;
      ctx.lineCap = 'round';
      ctx.strokeStyle = rgba(color, alpha * a2);
      ctx.beginPath();
      ctx.moveTo(points[0].x, points[0].y);
      for (const point of points.slice(1)) ctx.lineTo(point.x, point.y);
      ctx.stroke();
    }
  }

  /** Dendritas: filamentos finos alrededor de cada neurona, dibujados detrás de los nodos. */
  #drawDendrites(ctx) {
    if (this.meta.size > 400 || this.clusterIds.size > 0) return;
    const ids = [...this.meta.keys()].filter((id) => {
      const node = this.nodes.get(id);
      return node && !node.hidden && (node.opacity ?? 1) > 0.3;
    });
    const positions = this.network.getPositions(ids);
    ctx.save();
    ctx.lineCap = 'round';
    for (const id of ids) {
      const pos = positions[id];
      const node = this.nodes.get(id);
      if (!pos) continue;
      const size = node.size ?? 14;
      const color = KIND_STYLE[this.meta.get(id).kind]?.color ?? '#94a3b8';
      let seed = 0;
      for (let i = 0; i < id.length; i++) seed = (seed * 31 + id.charCodeAt(i)) >>> 0;
      const count = 5 + (seed % 4);
      for (let k = 0; k < count; k++) {
        seed = (seed * 1664525 + 1013904223) >>> 0;
        const angle = (k / count) * Math.PI * 2 + ((seed % 100) / 100) * 0.9;
        const length = size * (1.5 + ((seed >>> 8) % 100) / 100);
        const bend = (((seed >>> 16) % 100) / 100 - 0.5) * 0.9;
        const x1 = pos.x + Math.cos(angle) * size * 0.9;
        const y1 = pos.y + Math.sin(angle) * size * 0.9;
        const x2 = pos.x + Math.cos(angle + bend) * (size * 0.9 + length);
        const y2 = pos.y + Math.sin(angle + bend) * (size * 0.9 + length);
        ctx.strokeStyle = rgba(color, 0.32 * (node.opacity ?? 1));
        ctx.lineWidth = 1;
        ctx.beginPath();
        ctx.moveTo(x1, y1);
        ctx.quadraticCurveTo((x1 + x2) / 2 + Math.sin(angle) * 4, (y1 + y2) / 2 - Math.cos(angle) * 4, x2, y2);
        ctx.stroke();
        ctx.fillStyle = rgba(color, 0.5 * (node.opacity ?? 1));   // botón sináptico en la punta
        ctx.beginPath();
        ctx.arc(x2, y2, 1.6, 0, Math.PI * 2);
        ctx.fill();
      }
    }
    ctx.restore();
  }

  /** Señales tenues que recorren sinapsis al azar cuando no hay un análisis en curso. */
  #startAmbient() {
    if (this.ambientRaf || REDUCED_MOTION) return;
    let frame = 0;
    const tick = () => {
      this.ambientRaf = requestAnimationFrame(tick);
      frame++;
      const eligible = !this.fx && !document.hidden && this.filters.physics && this.meta.size > 1 && this.meta.size <= 150;
      if (!eligible) { this.signals = []; return; }
      const now = performance.now();
      this.signals = this.signals.filter((signal) => now - signal.t0 < signal.dur);
      if (this.signals.length < 5 && Math.random() < 0.06) {
        const edges = [...this.edgeMeta.values()].filter((edge) => !edge.id?.startsWith?.('sim:') && !this.nodes.get(edge.from)?.hidden && !this.nodes.get(edge.to)?.hidden && (this.nodes.get(edge.from)?.opacity ?? 1) > 0.5 && (this.nodes.get(edge.to)?.opacity ?? 1) > 0.5);
        const edge = edges[Math.floor(Math.random() * edges.length)];
        if (edge) this.signals.push({ from: edge.from, to: edge.to, t0: now, dur: 1300 + Math.random() * 1200 });
      }
      if (this.signals.length && frame % 2 === 0) this.network.redraw();
    };
    this.ambientRaf = requestAnimationFrame(tick);
  }

  #drawAmbient(ctx) {
    if (this.fx || this.signals.length === 0) return;
    const now = performance.now();
    const ids = [...new Set(this.signals.flatMap((signal) => [signal.from, signal.to]))];
    const positions = this.#positions(ids);
    ctx.save();
    ctx.globalCompositeOperation = 'lighter';
    for (const signal of this.signals) {
      const t = (now - signal.t0) / signal.dur;
      for (let k = 0; k < 3; k++) {
        const point = this.#edgePoint(signal.from, signal.to, t - k * 0.05, positions);
        if (point && t - k * 0.05 >= 0) this.#pulse(ctx, point, FX_LEVEL_COLORS[1], 0.55 - k * 0.15);
      }
    }
    ctx.restore();
  }

  #glow(ctx, x, y, radius, color, alpha) {
    const gradient = ctx.createRadialGradient(x, y, radius * 0.15, x, y, radius);
    gradient.addColorStop(0, rgba(color, alpha));
    gradient.addColorStop(1, rgba(color, 0));
    ctx.fillStyle = gradient;
    ctx.beginPath();
    ctx.arc(x, y, radius, 0, Math.PI * 2);
    ctx.fill();
  }

  #pulse(ctx, point, color, scale = 1) {
    const radius = 8 * scale;
    const gradient = ctx.createRadialGradient(point.x, point.y, 0, point.x, point.y, radius);
    gradient.addColorStop(0, 'rgba(255, 255, 255, 1)');
    gradient.addColorStop(0.3, rgba(color, 0.9));
    gradient.addColorStop(1, rgba(color, 0));
    ctx.fillStyle = gradient;
    ctx.beginPath();
    ctx.arc(point.x, point.y, radius, 0, Math.PI * 2);
    ctx.fill();
  }

  #drawFx(ctx) {
    const fx = this.fx;
    if (!fx) return;
    const ids = [...fx.dist.keys()].filter((id) => !this.nodes.get(id)?.hidden);
    if (ids.length === 0) return;

    const positions = this.#positions(ids);
    const radiusOf = (id) => (this.nodes.get(id)?.size ?? 14) * 2.6;
    const now = performance.now();

    ctx.save();
    ctx.globalCompositeOperation = 'lighter';

    if (fx.mode === 'scan') {
      const cycle = fx.maxLevel + 3;
      const front = ((now - fx.t0) / FX_SCAN_STEP_MS) % cycle;

      // Fuera del flujo, las neuronas se apagan para que solo se vea el camino que se analiza.
      if (fx.focused) {
        const all = [...this.meta.keys()].filter((id) => !fx.dist.has(id) && !this.nodes.get(id)?.hidden);
        const others = this.clusterIds.size === 0 ? this.network.getPositions(all) : {};
        ctx.globalCompositeOperation = 'source-over';
        ctx.fillStyle = 'rgba(4, 8, 22, 0.7)';
        for (const id of all) {
          const pos = others[id];
          if (!pos) continue;
          ctx.beginPath();
          ctx.arc(pos.x, pos.y, (this.nodes.get(id)?.size ?? 14) * 1.9, 0, Math.PI * 2);
          ctx.fill();
        }
        ctx.globalCompositeOperation = 'lighter';
      }

      // Axones del flujo: se encienden desde el origen a medida que avanza la onda.
      for (const [level, list] of fx.pairs) {
        const lit = Math.max(0, Math.min(1, front - (level - 1)));
        if (lit <= 0) continue;
        for (const [a, b] of list) this.#strokeEdge(ctx, a, b, positions, lit, FX_LEVEL_COLORS[1], front >= level ? 0.5 : 0.9);
      }

      for (const id of ids) {
        const pos = positions[id];
        const delta = front - fx.dist.get(id);
        if (!pos || delta < 0) continue;
        if (delta < 2.5) this.#glow(ctx, pos.x, pos.y, radiusOf(id), FX_LEVEL_COLORS[1], 0.6 * (1 - delta / 2.5));
        if (delta < 1) {   // la neurona "dispara": anillo que se expande
          ctx.lineWidth = 2;
          ctx.strokeStyle = rgba(FX_LEVEL_COLORS[2], 0.9 * (1 - delta));
          ctx.beginPath();
          ctx.arc(pos.x, pos.y, radiusOf(id) * (0.35 + delta * 0.9), 0, Math.PI * 2);
          ctx.stroke();
        }
      }

      // Impulsos con cola de cometa viajando por cada axón.
      for (const [level, list] of fx.pairs) {
        if (front < level - 1 || front >= level) continue;
        for (const [a, b] of list) {
          for (let k = 0; k < 5; k++) {
            const t = front - (level - 1) - k * 0.045;
            if (t < 0) continue;
            const point = this.#edgePoint(a, b, t, positions);
            if (point) this.#pulse(ctx, point, FX_LEVEL_COLORS[1], k === 0 ? 1 : 0.7 - k * 0.12);
          }
        }
      }

      const phase = ((now - fx.t0) % 1200) / 1200;
      ctx.lineWidth = 2;
      for (const id of fx.roots) {
        const pos = positions[id];
        if (!pos) continue;
        ctx.strokeStyle = rgba(FX_LEVEL_COLORS[1], 0.8 * (1 - phase));
        ctx.beginPath();
        ctx.arc(pos.x, pos.y, radiusOf(id) * (0.5 + phase * 0.9), 0, Math.PI * 2);
        ctx.stroke();
      }
    } else {
      const current = fx.done ? fx.maxLevel : fx.level;
      const progress = fx.done || fx.stepMs <= 0 ? 1 : Math.min(1, (now - fx.stepStart) / fx.stepMs);
      for (const id of ids) {
        const level = fx.dist.get(id);
        const pos = positions[id];
        if (!pos || level > current) continue;
        const flash = !fx.done && level === current ? 1 - progress : 0;
        const color = FX_LEVEL_COLORS[Math.min(level, FX_LEVEL_COLORS.length - 1)];
        this.#glow(ctx, pos.x, pos.y, radiusOf(id) * (1 + flash * 0.6), color, 0.28 + 0.6 * flash);
      }
      for (const [level, list] of fx.pairs) {
        if (level > current) continue;
        const lit = level === current && !fx.done ? progress : 1;
        const color = FX_LEVEL_COLORS[Math.min(level, FX_LEVEL_COLORS.length - 1)];
        for (const [a, b] of list) this.#strokeEdge(ctx, a, b, positions, lit, color, 0.7);
      }
      if (!fx.done && current > 0) {
        const color = FX_LEVEL_COLORS[Math.min(current, FX_LEVEL_COLORS.length - 1)];
        for (const [a, b] of fx.pairs.get(current) || []) {
          for (const lag of [0, 0.16]) {
            const t = progress - lag;
            if (t < 0) continue;
            const point = this.#edgePoint(a, b, t, positions);
            if (point) this.#pulse(ctx, point, color, lag ? 0.6 : 1);
          }
        }
      }
    }

    ctx.restore();
  }

  /** El brillo de las aristas es costoso: se apaga en redes muy grandes. */
  #tuneEdgeGlow() {
    const enabled = this.edgeMeta.size <= EDGE_GLOW_MAX_EDGES;
    this.network.setOptions({
      edges: { shadow: enabled ? { enabled: true, color: 'rgba(0, 170, 255, 0.35)', size: 5, x: 0, y: 0 } : { enabled: false } }
    });
  }

  #hopDistances() {
    if (!this.focusId || !this.meta.has(this.focusId) || !Number.isFinite(this.filters.maxHops)) return null;

    const outgoing = new Map();
    const incoming = new Map();
    for (const edge of this.edgeMeta.values()) {
      if (!outgoing.has(edge.from)) outgoing.set(edge.from, []);
      if (!incoming.has(edge.to)) incoming.set(edge.to, []);
      outgoing.get(edge.from).push(edge.to);
      incoming.get(edge.to).push(edge.from);
    }

    const direction = this.filters.direction;
    const distances = new Map([[this.focusId, 0]]);
    const queue = [this.focusId];
    while (queue.length) {
      const current = queue.shift();
      const depth = distances.get(current);
      if (depth >= this.filters.maxHops) continue;
      const next = [
        ...(direction !== 'Incoming' ? outgoing.get(current) || [] : []),
        ...(direction !== 'Outgoing' ? incoming.get(current) || [] : [])
      ];
      for (const id of next) {
        if (!distances.has(id)) {
          distances.set(id, depth + 1);
          queue.push(id);
        }
      }
    }
    return distances;
  }

  #clusterKey(dto, mode) {
    if (mode === 'instance') return dto.server;
    if (mode === 'database') return `${dto.server}.${dto.database}`;
    return `${dto.database}.${dto.schema}`;
  }

  #cluster() {
    let mode = this.filters.clusterMode;
    const visible = [...this.meta.entries()].filter(([id]) => !this.nodes.get(id)?.hidden);
    if (mode === 'auto') mode = visible.length > AUTO_CLUSTER_THRESHOLD ? 'schema' : 'none';
    if (mode === 'none') return;

    const protectedIds = new Set([this.focusId, ...(this.network.getSelectedNodes?.() || []), ...this.simNodeIds]);
    const groups = new Map();
    for (const [id, dto] of visible) {
      if (protectedIds.has(id) || dto.kind === 'LinkedServer') continue;
      const key = this.#clusterKey(dto, mode);
      if (!groups.has(key)) groups.set(key, []);
      groups.get(key).push(id);
    }

    for (const [key, ids] of groups) {
      if (ids.length < MIN_CLUSTER_SIZE) continue;
      const members = new Set(ids);
      const clusterId = `cluster:${mode}:${key}`;
      const label = mode === 'schema' ? key.split('.').slice(-1)[0] : key;
      this.network.cluster({
        joinCondition: (options) => members.has(options.id),
        clusterNodeProperties: {
          id: clusterId,
          label: `${label}\n${ids.length} objetos`,
          shape: mode === 'database' ? 'database' : mode === 'instance' ? 'box' : 'hexagon',
          size: 18 + Math.min(30, Math.sqrt(ids.length) * 4),
          borderWidth: 2,
          color: { background: 'rgba(0, 229, 255, 0.12)', border: '#00e5ff', highlight: { background: 'rgba(0, 229, 255, 0.28)', border: '#ffffff' } },
          font: { color: '#d9f3ff', size: 13, face: NODE_FONT_FACE, multi: false, strokeWidth: 4, strokeColor: '#050914' },
          shadow: { enabled: true, color: 'rgba(0, 229, 255, 0.55)', size: 26, x: 0, y: 0 },
          title: textTooltip([`Grupo ${mode}: ${key}`, `${ids.length} objetos`, 'Doble clic para abrir'])
        }
      });
      this.clusterIds.add(clusterId);
    }
    this.handlers.onCountsChanged?.();
  }

  #openAllClusters() {
    for (const id of this.clusterIds) {
      if (this.network.isCluster(id)) this.network.openCluster(id);
    }
    this.clusterIds.clear();
  }

  #openClusterContaining(nodeId) {
    for (const id of [...this.clusterIds]) {
      if (this.network.isCluster(id) && this.network.getNodesInCluster(id).includes(nodeId)) {
        this.network.openCluster(id);
        this.clusterIds.delete(id);
      }
    }
  }
}

// ---- Estilos --------------------------------------------------------------------------------

/** Halo de la neurona. Los críticos brillan en rojo, los de lógica similar en fucsia; la seleccionada, más fuerte. */
function nodeShadow(n, active = true, core = false, similar = false) {
  const style = KIND_STYLE[n.kind] || KIND_STYLE.External;
  const glow = similar ? SIM_COLOR : (n.isCritical ? '#ff536e' : style.color);
  if (!active) return { enabled: false };
  const pending = n.isExternal || n.kind === 'Missing' || n.kind === 'LinkedServer';
  const baseAlpha = similar ? 0.85 : n.isCritical ? 0.8 : pending ? 0.25 : 0.55;
  const baseSize = similar ? 26 : n.isCritical ? 28 : 18;
  return {
    enabled: true,
    color: rgba(glow, core ? 0.95 : baseAlpha),
    size: core ? baseSize + 12 : baseSize,
    x: 0,
    y: 0
  };
}

export function nodeVisual(n) {
  const style = KIND_STYLE[n.kind] || KIND_STYLE.External;
  const color = style.color;
  const pending = n.isExternal || n.kind === 'Missing' || n.kind === 'LinkedServer';
  const size = 11 + Math.min(20, Math.sqrt(n.fanIn || 0) * 3.2) + (n.isCritical ? 4 : 0);

  return {
    id: n.id,
    label: shorten(n.label, 34) + (n.hasMore ? ' ＋' : ''),
    title: nodeTooltip(n),
    shape: style.shape,
    size,
    opacity: 1,
    color: {
      // Cuerpo sólido del color de la neurona; borde del mismo tono, más claro, para dar volumen.
      background: pending ? rgba(color, 0.2) : (n.isCritical ? '#ff536e' : color),
      border: n.isCritical ? '#ff8095' : lighten(color, 0.35),
      highlight: { background: '#5fffc1', border: '#ffffff' },
      hover: { background: lighten(color, 0.25), border: '#ffffff' }
    },
    borderWidth: n.isCritical ? 3 : 1.5,
    borderWidthSelected: 4,
    shapeProperties: { borderDashes: pending ? [4, 3] : false },
    shadow: nodeShadow(n, true, false),
    font: nodeFont(true),
    // Al elegirse, el nodo crece como una neurona que se activa.
    chosen: {
      node: (values) => { values.size = size * 1.3; values.borderWidth = 4; }
    }
  };
}

/** Marca un nodo como parte de un par con lógica similar: borde fucsia, halo fucsia y "≈" en la etiqueta. */
function markSimilar(visual) {
  visual.label = `${visual.label} ≈`;
  visual.borderWidth = Math.max(visual.borderWidth || 2, 3);
  visual.color = { ...visual.color, border: SIM_COLOR };
  return visual;
}

function simEdgeColor(opacity) {
  return { color: SIM_COLOR, highlight: '#ffffff', hover: '#ffffff', opacity };
}

function nodeFont(active) {
  return {
    color: active ? '#d9f3ff' : 'rgba(217, 243, 255, 0.18)',
    size: 11,
    face: NODE_FONT_FACE,
    strokeWidth: active ? 3 : 0,
    strokeColor: '#050914'
  };
}

export function edgeVisual(edge, from, to, hasReverse = false) {
  const kinds = edge.kinds || [];
  let color = EDGE_COLORS.ref;
  let dashes = false;
  let width = 1.1;

  if (kinds.includes('Writes')) { color = EDGE_COLORS.writes; width = 1.9; }
  else if (kinds.includes('Executes') || kinds.includes('Calls')) { color = EDGE_COLORS.exec; width = 1.5; }
  else if (kinds.includes('Reads')) { color = EDGE_COLORS.reads; width = 1.2; }
  else if (kinds.includes('TriggeredBy')) { color = EDGE_COLORS.trigger; width = 1.4; }
  else if (kinds.includes('SynonymFor')) { color = EDGE_COLORS.synonym; dashes = [2, 4]; }
  else { dashes = [4, 4]; }   // referencia sin clasificar

  const bridge = from && to && (from.server !== to.server || from.database !== to.database);
  if (bridge) { color = EDGE_COLORS.bridge; dashes = [7, 5]; width = 2.4; }

  const relation = kinds.map((k) => RELATION_LABELS[k] || k).join(' + ');
  const notes = `${relation}${bridge ? ' (puente entre bases/instancias)' : ''}${hasReverse ? ' (ciclo directo: se llaman entre sí)' : ''}`;
  return {
    color: { color, highlight: '#00e5ff', hover: '#00e5ff', opacity: EDGE_BASE_OPACITY },
    dashes,
    width,
    selectionWidth: 1.5,
    // A→B y B→A: curvas hacia lados opuestos (CW depende del sentido), así no se superponen.
    smooth: hasReverse
      ? { enabled: true, type: 'curvedCW', roundness: 0.22 }
      : { enabled: true, type: 'dynamic', roundness: 0.35 },
    title: textTooltip([`${from?.label ?? edge.from}`, notes, `${to?.label ?? edge.to}`])
  };
}

export function kindLabel(kind) {
  return KIND_STYLE[kind]?.label ?? kind;
}

export function kindColor(kind) {
  return KIND_STYLE[kind]?.color ?? '#94a3b8';
}

function nodeTooltip(n) {
  const lines = [
    `${kindLabel(n.kind)} · ${n.server} / ${n.database}`,
    `Lo usan ${n.fanIn} · usa ${n.fanOut}`
  ];
  const flags = [];
  if (n.isCritical) flags.push('crítico');
  if (n.hasDynamicSql) flags.push('SQL dinámico');
  if (n.usesOpenQuery) flags.push('OPENQUERY');
  if (n.isExternal) flags.push('externo: doble clic para cargar su rama');
  if (n.kind === 'Missing') flags.push('no existe (referencia rota)');
  if (n.hasMore && !n.isExternal) flags.push('doble clic para cargar vecinos');
  if (flags.length) lines.push(flags.join(' · '));

  const element = document.createElement('div');
  const title = document.createElement('div');
  title.className = 'tip-title';
  title.textContent = `${n.schema}.${n.name}`;
  element.appendChild(title);
  for (const line of lines) {
    const row = document.createElement('div');
    row.className = 'tip-row';
    row.textContent = line;
    element.appendChild(row);
  }
  return element;
}

function textTooltip(lines) {
  const element = document.createElement('div');
  lines.forEach((line, index) => {
    const row = document.createElement('div');
    row.className = index === 0 ? 'tip-title' : 'tip-row';
    row.textContent = line;
    element.appendChild(row);
  });
  return element;
}

function shorten(text, max) {
  return text.length > max ? `${text.slice(0, max - 1)}…` : text;
}

function rgba(hex, alpha) {
  const value = parseInt(hex.slice(1), 16);
  return `rgba(${(value >> 16) & 255}, ${(value >> 8) & 255}, ${value & 255}, ${alpha})`;
}

/** Mezcla el color con blanco (amount 0..1). */
function lighten(hex, amount) {
  const value = parseInt(hex.slice(1), 16);
  const mix = (channel) => Math.round(channel + (255 - channel) * amount);
  return `rgb(${mix((value >> 16) & 255)}, ${mix((value >> 8) & 255)}, ${mix(value & 255)})`;
}
