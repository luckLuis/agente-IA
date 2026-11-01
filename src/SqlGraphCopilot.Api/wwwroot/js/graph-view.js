// Vista de la red con vis-network: estilos por tipo, carga incremental, filtros por tipo y saltos,
// resaltado de vecinos y agrupación (clústeres) para evitar el efecto "nido de abeja".

export const KIND_STYLE = {
  Table:           { label: 'Tabla',              color: '#38bdf8', shape: 'dot' },
  View:            { label: 'Vista',              color: '#2dd4bf', shape: 'square' },
  StoredProcedure: { label: 'Procedimiento',      color: '#a78bfa', shape: 'hexagon' },
  Function:        { label: 'Función',            color: '#f472b6', shape: 'triangle' },
  Trigger:         { label: 'Trigger',            color: '#a3e635', shape: 'triangleDown' },
  Synonym:         { label: 'Sinónimo',           color: '#94a3b8', shape: 'star' },
  External:        { label: 'Externo',            color: '#fb923c', shape: 'dot' },
  LinkedServer:    { label: 'Servidor vinculado', color: '#f97316', shape: 'diamond' },
  Missing:         { label: 'Inexistente',        color: '#64748b', shape: 'dot' }
};

const EDGE_COLORS = {
  writes: '#fb7185',
  reads: '#7dd3fc',
  exec: '#a78bfa',
  trigger: '#a3e635',
  synonym: '#94a3b8',
  ref: '#475569',
  bridge: '#f59e0b'
};

const RELATION_LABELS = {
  Writes: 'escribe', Reads: 'lee', Executes: 'ejecuta', Calls: 'llama',
  TriggeredBy: 'trigger sobre', SynonymFor: 'sinónimo de', References: 'referencia'
};

const AUTO_CLUSTER_THRESHOLD = 120;
const PHYSICS_OFF_THRESHOLD = 450;
const MIN_CLUSTER_SIZE = 3;

export class GraphView {
  constructor(container, handlers = {}) {
    this.container = container;
    this.handlers = handlers;
    this.meta = new Map();       // id -> NodeDto
    this.edgeMeta = new Map();   // edgeId -> EdgeDto
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
    this.#openAllClusters();
    this.nodes.clear();
    this.edges.clear();
    this.meta.clear();
    this.edgeMeta.clear();
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

    const edgeUpdates = [];
    for (const edge of subgraph.edges) {
      const id = `${edge.from}→${edge.to}`;
      this.edgeMeta.set(id, edge);
      touched.add(edge.from);
      touched.add(edge.to);
      edgeUpdates.push({ id, from: edge.from, to: edge.to, ...edgeVisual(edge, this.meta.get(edge.from), this.meta.get(edge.to)) });
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
      if (newIds.has(id) && anchor) {
        visual.x = anchor.x + (Math.random() - 0.5) * 160;
        visual.y = anchor.y + (Math.random() - 0.5) * 160;
      }
      nodeUpdates.push(visual);
    }

    this.nodes.update(nodeUpdates);
    this.edges.update(edgeUpdates);
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
    for (const id of this.meta.keys()) {
      const active = set.size === 0 || set.has(id);
      nodeUpdates.push({ id, opacity: active ? 1 : 0.14, font: nodeFont(active) });
    }
    this.nodes.update(nodeUpdates);

    const edgeUpdates = [];
    for (const [id, edge] of this.edgeMeta) {
      const active = set.size === 0 || (set.has(edge.from) && set.has(edge.to));
      const base = edgeVisual(edge, this.meta.get(edge.from), this.meta.get(edge.to));
      edgeUpdates.push({ id, color: { ...base.color, opacity: active ? 0.95 : 0.06 } });
    }
    this.edges.update(edgeUpdates);
  }

  clearHighlight() {
    this.highlighted = null;
    this.highlight([]);
    this.highlighted = null;
    this.network.unselectAll();
  }

  fit() {
    this.network.fit({ animation: { duration: 500, easingFunction: 'easeInOutQuad' } });
  }

  counts() {
    return { nodes: this.meta.size, edges: this.edgeMeta.size, hidden: this.hiddenCount || 0, clusters: this.clusterIds.size };
  }

  // ---------------------------------------------------------------------------------------

  #options() {
    return {
      autoResize: true,
      layout: { improvedLayout: false },
      physics: {
        enabled: true,
        solver: 'forceAtlas2Based',
        forceAtlas2Based: { gravitationalConstant: -70, centralGravity: 0.01, springLength: 120, springConstant: 0.06, avoidOverlap: 0.5 },
        maxVelocity: 45,
        minVelocity: 0.8,
        stabilization: { enabled: true, iterations: 220, updateInterval: 30 }
      },
      interaction: { hover: true, tooltipDelay: 120, hideEdgesOnDrag: true, multiselect: false, navigationButtons: false, keyboard: false },
      edges: {
        arrows: { to: { enabled: true, scaleFactor: 0.5 } },
        smooth: { enabled: true, type: 'continuous', roundness: 0.35 },
        selectionWidth: 1.5,
        hoverWidth: 1
      },
      nodes: { borderWidthSelected: 4 }
    };
  }

  #wireEvents() {
    this.network.on('selectNode', (params) => {
      const id = params.nodes[0];
      if (this.network.isCluster(id)) return;
      this.highlight([id, ...this.network.getConnectedNodes(id)]);
      this.handlers.onSelect?.(id);
    });

    this.network.on('deselectNode', () => {
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

    this.network.on('stabilizationIterationsDone', () => {
      if (!this.filters.physics || this.meta.size > PHYSICS_OFF_THRESHOLD) {
        this.network.setOptions({ physics: { enabled: false } });
      }
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

    const protectedIds = new Set([this.focusId, ...(this.network.getSelectedNodes?.() || [])]);
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
          color: { background: 'rgba(34,211,238,0.14)', border: '#22d3ee', highlight: { background: 'rgba(34,211,238,0.28)', border: '#e2e8f0' } },
          font: { color: '#e2e8f0', size: 13, face: 'Inter, Segoe UI, system-ui, sans-serif', multi: false, strokeWidth: 4, strokeColor: '#050914' },
          shadow: { enabled: true, color: 'rgba(34,211,238,0.45)', size: 22, x: 0, y: 0 },
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
      background: pending ? rgba(color, 0.22) : color,
      border: n.isCritical ? '#f43f5e' : color,
      highlight: { background: color, border: '#ffffff' },
      hover: { background: color, border: '#ffffff' }
    },
    borderWidth: n.isCritical ? 4 : 2,
    shapeProperties: { borderDashes: pending ? [4, 3] : false },
    shadow: { enabled: true, color: rgba(n.isCritical ? '#f43f5e' : color, n.isCritical ? 0.7 : 0.42), size: n.isCritical ? 26 : 14, x: 0, y: 0 },
    font: nodeFont(true)
  };
}

function nodeFont(active) {
  return {
    color: active ? '#e2e8f0' : 'rgba(226, 232, 240, 0.2)',
    size: 12,
    face: 'Inter, Segoe UI, system-ui, sans-serif',
    strokeWidth: active ? 4 : 0,
    strokeColor: '#050914'
  };
}

export function edgeVisual(edge, from, to) {
  const kinds = edge.kinds || [];
  let color = EDGE_COLORS.ref;
  let dashes = [4, 4];
  let width = 1.2;

  if (kinds.includes('Writes')) { color = EDGE_COLORS.writes; dashes = false; width = 2.2; }
  else if (kinds.includes('Executes') || kinds.includes('Calls')) { color = EDGE_COLORS.exec; dashes = false; width = 1.7; }
  else if (kinds.includes('Reads')) { color = EDGE_COLORS.reads; dashes = false; width = 1.4; }
  else if (kinds.includes('TriggeredBy')) { color = EDGE_COLORS.trigger; dashes = false; width = 1.6; }
  else if (kinds.includes('SynonymFor')) { color = EDGE_COLORS.synonym; dashes = [2, 4]; }

  const bridge = from && to && (from.server !== to.server || from.database !== to.database);
  if (bridge) { color = EDGE_COLORS.bridge; dashes = [7, 5]; width = 3; }

  const relation = kinds.map((k) => RELATION_LABELS[k] || k).join(' + ');
  return {
    color: { color, highlight: '#e2e8f0', hover: '#e2e8f0', opacity: 0.95 },
    dashes,
    width,
    title: textTooltip([`${from?.label ?? edge.from}`, `${relation}${bridge ? ' (puente entre bases/instancias)' : ''}`, `${to?.label ?? edge.to}`])
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
