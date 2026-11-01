// Cliente HTTP de la API. Errores en formato ProblemDetails -> Error con .status y .detail.

const enc = encodeURIComponent;

async function request(path, { method = 'GET', body, signal } = {}) {
  const headers = { Accept: 'application/json' };
  if (body !== undefined) headers['Content-Type'] = 'application/json';

  const response = await fetch(path, {
    method,
    headers,
    body: body === undefined ? undefined : JSON.stringify(body),
    signal
  });

  if (response.status === 204) return null;

  const contentType = response.headers.get('content-type') || '';
  const payload = contentType.includes('json') ? await response.json() : await response.text();

  if (!response.ok) {
    const message = (payload && (payload.detail || payload.title)) || `Error HTTP ${response.status}`;
    const error = new Error(message);
    error.status = response.status;
    error.problem = payload;
    throw error;
  }
  return payload;
}

export const api = {
  instances: () => request('/api/instances'),

  databases: (instance) => request(`/api/instances/${enc(instance)}/databases`),

  summary: (instance, database) =>
    request(`/api/instances/${enc(instance)}/databases/${enc(database)}/summary`),

  search: (instance, database, { text = '', kinds = [], page = 1, pageSize = 30 } = {}) => {
    const query = new URLSearchParams({ page, pageSize });
    if (text) query.set('search', text);
    if (kinds.length) query.set('kinds', kinds.join(','));
    return request(`/api/instances/${enc(instance)}/databases/${enc(database)}/objects?${query}`);
  },

  refresh: (instance, database) =>
    request(`/api/instances/${enc(instance)}/databases/${enc(database)}/refresh`, { method: 'POST' }),

  hubs: (instance, database, top = 30) =>
    request(`/api/graph/hubs?instance=${enc(instance)}&database=${enc(database)}&top=${top}`),

  neighborhood: (nodeId, depth = 1, direction = 'Both') =>
    request(`/api/graph/neighborhood?nodeId=${enc(nodeId)}&depth=${depth}&direction=${enc(direction)}`),

  impact: (nodeId, maxDepth = 6) =>
    request(`/api/analysis/impact?nodeId=${enc(nodeId)}&maxDepth=${maxDepth}`),

  analysis: (kind, instance, database) =>
    request(`/api/analysis/${kind}?instance=${enc(instance)}&database=${enc(database)}`),

  deepScan: (instance, database) =>
    request('/api/analysis/deep-scan', { method: 'POST', body: { instance, database } }),

  chat: (payload) => request('/api/copilot/chat', { method: 'POST', body: payload })
};
