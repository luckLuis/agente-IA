
const API_BASE = '/api';   // ajustar si la API cuelga de otra ruta

async function readJson(response) {
  if (!response.ok) {
    let message = `Error ${response.status}`;
    try { const body = await response.json(); message = body.message ?? body.detail ?? body.title ?? message; } catch { }
    throw new Error(message);
  }
  return response.json();
}

export async function fetchFlowInputs({ instance, database, schema, name }) {
  const query = new URLSearchParams({ instance, database, schema, name });
  return readJson(await fetch(`${API_BASE}/flow/inputs?${query}`));
}

const isNumber = (text) => text !== '' && Number.isFinite(Number(text));

function parseValue(raw) {
  if (raw === undefined || raw === null || raw.trim() === '') return { provided: false };
  return raw.trim().toUpperCase() === 'NULL' ? { provided: true, value: null } : { provided: true, value: raw.trim() };
}

function compare(a, b) {
  if (isNumber(a) && isNumber(b)) return Math.sign(Number(a) - Number(b));
  return a.localeCompare(b, undefined, { sensitivity: 'accent' });   // SQL Server: colación insensible a mayúsculas
}

function checkConstraint(constraint, raw) {
  const input = parseValue(raw);
  if (!input.provided) return null;
  const { value } = input;
  switch (constraint.operator) {
    case 'IS NULL': return value === null;
    case 'IS NOT NULL': return value !== null;
  }
  if (value === null) return false;   // comparar con NULL nunca es verdadero
  switch (constraint.operator) {
    case '=': return compare(value, constraint.values[0]) === 0;
    case '<>': return compare(value, constraint.values[0]) !== 0;
    case '>': return compare(value, constraint.values[0]) > 0;
    case '>=': return compare(value, constraint.values[0]) >= 0;
    case '<': return compare(value, constraint.values[0]) < 0;
    case '<=': return compare(value, constraint.values[0]) <= 0;
    case 'IN': return constraint.values.some((v) => compare(value, v) === 0);
    case 'NOT IN': return !constraint.values.some((v) => compare(value, v) === 0);
    default: return null;
  }
}

export function evaluateCondition(condition, values) {
  let unknown = false;
  for (const constraint of condition.constraints) {
    const result = checkConstraint(constraint, values[constraint.parameter.toLowerCase()]);
    if (result === false) return 'no';
    if (result === null) unknown = true;
  }
  if (unknown) return 'maybe';
  return condition.exact && condition.constraints.length > 0 ? 'yes' : 'maybe';
}

export function evaluateBranch(branch, values) {
  let status = 'yes';
  for (const condition of branch.conditions) {
    const result = evaluateCondition(condition, values);
    if (result === 'no') return 'no';
    if (result === 'maybe') status = 'maybe';
  }
  return status;
}

const baseType = (type) => type.toLowerCase().replace(/[\[\]]/g, '').replace(/\(.*\)/, '').trim();
const NUMERIC_TYPES = new Set(['int', 'bigint', 'smallint', 'tinyint', 'decimal', 'numeric', 'float', 'real', 'money', 'smallmoney', 'bit']);
const UNICODE_TYPES = new Set(['nvarchar', 'nchar', 'ntext']);

function sampleFor(type) {
  const t = baseType(type);
  if (NUMERIC_TYPES.has(t)) return '1';
  if (t === 'date') return '2025-01-01';
  if (t.startsWith('datetime') || t === 'smalldatetime') return '2025-01-01T00:00:00';
  if (t === 'time') return '12:00:00';
  if (t === 'uniqueidentifier') return '00000000-0000-0000-0000-000000000001';
  return 'X';
}

function sqlLiteral(type, raw) {
  const input = parseValue(raw);
  if (!input.provided || input.value === null) return 'NULL';
  const t = baseType(type);
  if (NUMERIC_TYPES.has(t) && isNumber(input.value)) return input.value;
  return `${UNICODE_TYPES.has(t) ? 'N' : ''}'${input.value.replace(/'/g, "''")}'`;
}

export function suggestValues(report, branch) {
  const byParam = new Map();
  for (const condition of branch.conditions) {
    for (const constraint of condition.constraints) {
      const key = constraint.parameter.toLowerCase();
      if (!byParam.has(key)) byParam.set(key, []);
      byParam.get(key).push(constraint);
    }
  }
  const types = new Map(report.parameters.map((p) => [p.name.toLowerCase(), p.dataType]));

  const values = {};
  let ok = true;
  for (const [key, list] of byParam) {
    const candidates = [];
    for (const constraint of list) {
      if (constraint.operator === 'IS NULL') candidates.push('NULL');
      for (const value of constraint.values) {
        candidates.push(value);
        if (isNumber(value)) candidates.push(String(Number(value) + 1), String(Number(value) - 1));
      }
    }
    candidates.push(sampleFor(types.get(key) ?? ''), '1', '0', 'X', 'Y', 'Z');
    const pick = candidates.find((candidate) => list.every((constraint) => checkConstraint(constraint, candidate) === true));
    if (pick === undefined) ok = false; else values[key] = pick;
  }

  for (const parameter of report.parameters) {
    const key = parameter.name.toLowerCase();
    if (!parameter.isOptional && !parameter.isOutput && !(key in values)) values[key] = sampleFor(parameter.dataType);
  }

  const dataNotes = branch.conditions.filter((c) => !c.exact || c.dependsOnData).map((c) => c.text);
  return { values, ok, dataNotes };
}

export function buildScript(report, values, { database = null, rollback = true } = {}) {
  const proc = `[${report.schema}].[${report.name}]`;
  const outputs = report.parameters.filter((p) => p.isOutput);
  const args = [];
  for (const p of report.parameters) {
    const key = p.name.toLowerCase();
    if (p.isOutput) {
      args.push(`${p.name} = ${p.name}_out OUTPUT`);
      continue;
    }
    const provided = parseValue(values[key]).provided;
    if (!provided) {
      if (!p.isOptional) args.push(`${p.name} = NULL /* ← valor requerido */`);
      continue;
    }
    args.push(`${p.name} = ${sqlLiteral(p.dataType, values[key])}`);
  }

  const exec = args.length ? `EXEC ${proc}\n        ${args.join(',\n        ')};` : `EXEC ${proc};`;
  const lines = [];
  if (database) lines.push(`USE [${database}];`, 'GO', '');
  for (const p of outputs) lines.push(`DECLARE ${p.name}_out ${p.dataType};`);
  if (rollback) lines.push('BEGIN TRANSACTION;   -- prueba: todo se deshace al final');
  lines.push('BEGIN TRY', `    ${exec}`);
  if (outputs.length) lines.push(`    SELECT ${outputs.map((p) => `${p.name}_out AS [${p.name.slice(1)}]`).join(', ')};`);
  lines.push('    SELECT 0 AS CodigoError, \'OK\' AS Resultado;', 'END TRY', 'BEGIN CATCH',
    '    SELECT ERROR_NUMBER() AS CodigoError, ERROR_MESSAGE() AS Resultado;', 'END CATCH;');
  if (rollback) lines.push('IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;');
  return lines.join('\n');
}

export function buildScenarios(report) {
  const found = new Map();
  const add = (title, suggestion) => {
    if (!suggestion.ok) return;
    const key = JSON.stringify(Object.entries(suggestion.values).sort());
    if (!found.has(key)) found.set(key, { title, values: suggestion.values, dataNotes: new Set() });
    suggestion.dataNotes.forEach((note) => found.get(key).dataNotes.add(note));
  };

  for (const branch of report.branches) {
    if (branch.kind === 'dynamic') continue;
    add(`llega a ${branch.target}`, suggestValues(report, branch));
  }
  for (const guard of report.guards) {
    add(`se corta con ${guard.outcome} cuando ${guard.condition.text}`, suggestValues(report, { conditions: [guard.condition] }));
  }

  return [...found.values()].map((scenario) => {
    const runs = report.branches
      .filter((b) => b.kind !== 'dynamic')
      .map((b) => ({ branch: b, status: evaluateBranch(b, scenario.values) }))
      .filter((item) => item.status !== 'no');
    const cuts = report.guards.filter((g) => evaluateCondition(g.condition, scenario.values) === 'yes');
    return { ...scenario, dataNotes: [...scenario.dataNotes], runs, cuts };
  });
}

export function buildAllScenarios(report, { database = null, rollback = true } = {}) {
  const scenarios = buildScenarios(report);
  if (scenarios.length === 0) return buildScript(report, {}, { database, rollback });

  const out = [`-- Validación de ${report.schema}.${report.name}: ${scenarios.length} escenario(s)`];
  if (rollback) out.push('-- Cada escenario va en su transacción y se deshace al final.');
  if (database) out.push('', `USE [${database}];`, 'GO');

  scenarios.forEach((scenario, index) => {
    out.push('', `-- ═══ Escenario ${index + 1}: ${scenario.title} ═══`);
    const ran = scenario.runs.filter((r) => r.status === 'yes').map((r) => r.branch.target);
    const maybe = scenario.runs.filter((r) => r.status === 'maybe').map((r) => r.branch.target);
    if (scenario.cuts.length) {
      out.push(`-- Esperado: el SP se corta con ${scenario.cuts[0].outcome} porque ${scenario.cuts[0].condition.text}.`);
    } else {
      out.push(`-- Esperado: ${ran.length ? `ejecuta ${[...new Set(ran)].join(', ')}` : 'no ejecuta ninguna acción cargada'}.`);
    }
    if (maybe.length) out.push(`-- Podría ejecutar (depende de datos): ${[...new Set(maybe)].join(', ')}.`);
    if (scenario.dataNotes.length) out.push(`-- Requiere en la base: ${scenario.dataNotes.join(' | ')}`);
    out.push(buildScript(report, scenario.values, { database: null, rollback }));
    out.push('GO');   // un lote por escenario: las variables OUTPUT no chocan entre sí
  });
  return out.join('\n');
}

const KIND_ICON = {
  decision: '?', then: '✓', else: '✗', loop: '↻', try: '⛨', catch: '!',
  exec: '➜', write: '✎', read: '⌕', transaction: '⇄', return: '■', throw: '!', dynamic: '⚡'
};
const KIND_HELP = {
  decision: 'Evalúa una condición y decide qué camino seguir.',
  then: 'Camino que se toma cuando la condición se cumple.',
  else: 'Camino que se toma cuando la condición no se cumple.',
  loop: 'Repite los pasos de adentro mientras se cumpla la condición.',
  try: 'Bloque protegido: si algo falla, salta al bloque de error.',
  catch: 'Se ejecuta solo si algo falló dentro del bloque protegido.',
  exec: 'Llama a otro procedimiento almacenado.',
  write: 'Cambia datos de una tabla.',
  read: 'Consulta datos de una tabla.',
  transaction: 'Inicia, confirma o deshace una transacción.',
  return: 'Termina el procedimiento en este punto.',
  throw: 'Lanza un error y detiene la ejecución.',
  dynamic: 'Arma y ejecuta SQL como texto: no se puede saber qué hace sin ejecutarlo.'
};
const KIND_BADGE = {
  decision: 'Decisión', then: 'Sí', else: 'No', loop: 'Ciclo', try: 'Intenta', catch: 'Error',
  exec: 'Llama', write: 'Escribe', read: 'Lee', transaction: 'Transacción', return: 'Fin', throw: 'Error', dynamic: 'Dinámico'
};
const BRANCH_KINDS = new Set(['then', 'else', 'loop', 'catch']);
const MARKABLE_KINDS = new Set(['exec', 'write', 'read']);
const ACTION_KINDS = new Set(['exec', 'write', 'read', 'transaction', 'return', 'throw', 'dynamic']);
const STATUS_LABEL = { yes: 'Se ejecutaría', maybe: 'Depende de datos', no: 'No se ejecuta', none: '' };
const CONTAINER_STATUS_LABEL = { yes: 'Se evalúa', maybe: 'Depende de datos', no: 'No se evalúa', none: '' };
const BRANCH_STATUS_LABEL = { yes: 'Se cumple', maybe: 'Depende de datos', no: 'No se cumple', none: '' };

const unique = (items) => [...new Set(items)];
const plural = (count, one, many) => `${count} ${count === 1 ? one : many}`;

export function summarize(report) {
  const steps = report.steps ?? [];
  const targetsOf = (kind) => unique(steps.filter((s) => s.kind === kind).flatMap((s) => s.targets));
  const required = report.parameters.filter((p) => !p.isOptional && !p.isOutput);
  return {
    required,
    reads: targetsOf('read'),
    writes: targetsOf('write'),
    calls: targetsOf('exec'),
    cuts: report.guards.length,
    dynamic: steps.some((s) => s.kind === 'dynamic'),
    transactions: steps.some((s) => s.kind === 'transaction')
  };
}

export function describeProcedure(report) {
  const info = summarize(report);
  const parts = [];
  const total = report.parameters.length;
  parts.push(total === 0
    ? 'No recibe parámetros.'
    : `Recibe ${plural(total, 'parámetro', 'parámetros')}${info.required.length ? ` (${info.required.length} obligatorio${info.required.length === 1 ? '' : 's'})` : ', todos opcionales'}.`);
  if (info.cuts) parts.push(`Tiene ${plural(info.cuts, 'validación', 'validaciones')} que ${info.cuts === 1 ? 'puede' : 'pueden'} cortar la ejecución antes de terminar.`);
  if (info.reads.length) parts.push(`Lee: ${info.reads.join(', ')}.`);
  if (info.writes.length) parts.push(`Modifica: ${info.writes.join(', ')}.`);
  if (info.calls.length) parts.push(`Llama a: ${info.calls.join(', ')}.`);
  if (info.transactions) parts.push('Maneja transacciones propias.');
  if (info.dynamic) parts.push('Usa SQL dinámico: parte de lo que hace no se puede deducir leyendo el código.');
  if (!info.reads.length && !info.writes.length && !info.calls.length) parts.push('No lee ni modifica tablas ni llama a otros procedimientos.');
  return parts;
}

export function simulate(report, values) {
  const steps = report.steps ?? [];
  const result = new Map();
  let order = 0;
  for (const step of steps) {
    const status = evaluateBranch(step, values);
    const numbered = status === 'yes' && ACTION_KINDS.has(step.kind);
    result.set(step.id, { status, order: numbered ? ++order : null });
  }
  return result;
}

export function mountInputsPanel(container, report, { escapeHtml, onSimulate, onPickTarget, database = null }) {
  const esc = escapeHtml;
  const steps = report.steps ?? [];
  const scenarios = buildScenarios(report);

  const info = summarize(report);
  const chipList = (items, kind) => items.length
    ? items.map((t) => `<button type="button" class="flow-chip ${kind}" data-target="${esc(t)}" title="Ver en la red">${esc(t)}</button>`).join('')
    : '<span class="flow-none">Ninguno</span>';

  const stats = [
    ['Parámetros', report.parameters.length, ''],
    ['Validaciones', info.cuts, info.cuts ? 'warn' : ''],
    ['Lee', info.reads.length, 'read'],
    ['Modifica', info.writes.length, 'write'],
    ['Llama a', info.calls.length, 'exec']
  ].map(([label, n, tone]) => `<div class="flow-stat ${tone}"><b>${n}</b><span>${label}</span></div>`).join('');

  const notes = [];
  if (info.transactions) notes.push('Maneja transacciones propias.');
  if (info.dynamic) notes.push('Usa SQL dinámico: parte de lo que hace no se puede deducir leyendo el código.');
  report.guards.forEach((g) => notes.push(`Termina antes (${g.outcome}) cuando: ${g.condition.text}`));

  const paramCards = report.parameters.map((p) => {
    const role = p.isOutput ? '<span class="badge info">Salida</span>'
      : p.isOptional ? '<span class="badge">Opcional</span>' : '<span class="badge crit">Obligatorio</span>';
    const hint = p.isOutput ? 'Parámetro de salida: lo devuelve el procedimiento, no se escribe.'
      : p.defaultValue ? `Si lo dejas vacío se usa <code>${esc(p.defaultValue)}</code>.`
        : p.isOptional ? 'Si lo dejas vacío no se envía.' : 'Necesario para que el procedimiento funcione.';
    return `
    <div class="flow-param">
      <div class="flow-param-head"><code class="flow-param-name">${esc(p.name)}</code><span class="flow-param-type">${esc(p.dataType)}</span>${role}</div>
      ${p.isOutput ? '' : `<input class="flow-input" type="text" data-param="${esc(p.name.toLowerCase())}" placeholder="${esc(p.dataType)}" aria-label="${esc(p.name)}" autocomplete="off" spellcheck="false">`}
      <div class="flow-param-hint">${hint}</div>
    </div>`;
  }).join('');

  const scenarioChips = scenarios.map((sc, i) => `<button type="button" class="flow-scenario" data-scenario-pick="${i}" title="${esc(sc.title)}">${esc(sc.title)}</button>`).join('');

  const stepRows = steps.map((step) => {
    const targets = MARKABLE_KINDS.has(step.kind)
      ? step.targets.map((t) => `<button type="button" class="flow-chip ${esc(step.kind)}" data-target="${esc(t)}" title="Ver en la red">${esc(t)}</button>`).join('') : '';
    const args = step.arguments.length
      ? `<div class="flow-args"><span>Con:</span>${step.arguments.map((a) => `<code>${esc(a.name)} = ${esc(a.value)}</code>`).join('')}</div>` : '';
    const conds = step.conditions.length
      ? `<div class="flow-conds"><span>Solo si:</span>${step.conditions.map((c) => `<code class="chip-cond ${c.dependsOnData ? 'data' : ''}" title="${c.dependsOnData ? 'Depende de datos que solo se conocen al ejecutar' : 'Se puede evaluar con los valores de entrada'}">${esc(c.text)}</code>`).join('')}</div>`
      : (step.depth === 0 && ACTION_KINDS.has(step.kind) ? '<div class="flow-conds"><span>Siempre se ejecuta si el flujo llega hasta aquí.</span></div>' : '');
    const op = step.operation && step.operation !== 'validación' ? `<span class="flow-op">${esc(step.operation)}</span>` : '';
    const validation = step.operation === 'validación' ? '<span class="badge info">validación</span>' : '';
    return `
    <li class="flow-step" data-step="${step.id}" data-kind="${esc(step.kind)}" style="--d:${step.depth}">
      <span class="flow-step-icon" aria-hidden="true">${KIND_ICON[step.kind] ?? '•'}</span>
      <div class="flow-step-body">
        <div class="flow-step-head">
          <span class="flow-step-order" data-order></span>
          <span class="kind kind-${esc(step.kind)}" title="${esc(KIND_HELP[step.kind] ?? '')}">${esc(KIND_BADGE[step.kind] ?? step.kind)}</span>
          ${op}${validation}
          <span class="flow-step-line" title="Línea del código del procedimiento">línea ${step.line}</span>
          <span class="status" data-status></span>
        </div>
        <div class="flow-step-title">${esc(step.title)}</div>
        ${targets ? `<div class="flow-step-targets">${targets}</div>` : ''}${args}${conds}
      </div>
    </li>`;
  }).join('');

  container.innerHTML = `
    <div class="flow-panel">
      <div class="flow-title">
        <span class="flow-eyebrow">Datos de entrada</span>
        <h3>${esc(report.schema)}.${esc(report.name)}</h3>
      </div>
      ${report.warnings.map((w) => `<div class="msg warn">${esc(w)}</div>`).join('')}
      <div class="flow-stats">${stats}</div>

      <section class="flow-card">
        <h4>Qué hace este procedimiento</h4>
        <p class="flow-lead">${esc(describeProcedure(report)[0])}</p>
        <dl class="flow-facts">
          <div><dt>Lee</dt><dd>${chipList(info.reads, 'read')}</dd></div>
          <div><dt>Modifica</dt><dd>${chipList(info.writes, 'write')}</dd></div>
          <div><dt>Llama a</dt><dd>${chipList(info.calls, 'exec')}</dd></div>
        </dl>
        ${notes.length ? `<ul class="flow-alerts">${notes.map((n) => `<li>${esc(n)}</li>`).join('')}</ul>` : ''}
        <details class="flow-how">
          <summary>Cómo funciona este panel</summary>
          <ol>
            <li>Lee el código del procedimiento; no ejecuta nada en la base de datos.</li>
            <li>Tú escribes valores de entrada y el panel evalúa las condiciones del código para decir qué pasos se ejecutarían y en qué orden.</li>
            <li>Lo que depende de datos de tablas, variables o funciones se marca como «Depende de datos»: no se puede saber sin ejecutar.</li>
            <li>«Simular y marcar en la red» anima ese recorrido sobre el grafo.</li>
          </ol>
        </details>
      </section>

      <section class="flow-card">
        <h4><span class="flow-num">1</span> Entradas</h4>
        ${report.parameters.length === 0
      ? '<div class="muted-block">Este procedimiento no recibe parámetros.</div>'
      : `<p class="muted">Escribe <code>NULL</code> para nulo. Vacío significa «sin dato».</p>
         <div class="flow-params">${paramCards}</div>`}
        ${scenarios.length ? `<div class="flow-scenarios"><span>Probar con un ejemplo:</span>${scenarioChips}</div>` : ''}
      </section>

      <section class="flow-card">
        <h4><span class="flow-num">2</span> Flujo paso a paso</h4>
        <div class="flow-outcome" data-outcome hidden></div>
        <div class="flow-toolbar">
          <label class="flow-check"><input type="checkbox" data-only-active> Mostrar solo lo que se ejecutaría</label>
          <span class="flow-legend-inline"><i class="dot yes"></i>se ejecuta <i class="dot maybe"></i>depende de datos <i class="dot no"></i>no</span>
        </div>
        ${steps.length ? `<ol class="flow-steps">${stepRows}</ol>` : '<div class="muted-block">No se encontraron pasos que mostrar.</div>'}
        <div class="actions"><button type="button" class="btn primary small" data-simulate ${steps.some((s) => MARKABLE_KINDS.has(s.kind)) ? '' : 'disabled'}>Simular y marcar en la red</button></div>
      </section>

      <details class="flow-details">
        <summary>Script opcional para validar en SSMS</summary>
        <div class="flow-notes" data-notes hidden></div>
        <label class="flow-check">Mostrar
          <select data-script-mode>
            <option value="all">todos los escenarios (valores ya cargados)</option>
            <option value="current">solo los valores de los campos</option>
          </select>
        </label>
        <label class="flow-check"><input type="checkbox" data-rollback checked> Envolver en transacción y deshacer (ROLLBACK)</label>
        <pre class="flow-script" data-script></pre>
        <div class="actions">
          <button type="button" class="btn ghost small" data-copy>Copiar script</button>
          <button type="button" class="btn ghost small" data-download>Descargar .sql</button>
        </div>
      </details>
    </div>`;

  const readValues = () => {
    const values = {};
    container.querySelectorAll('[data-param]').forEach((input) => { values[input.dataset.param] = input.value; });
    return values;
  };
  const anyValue = () => Object.values(readValues()).some((v) => v.trim() !== '');

  const outcomeBox = container.querySelector('[data-outcome]');
  const onlyActive = container.querySelector('[data-only-active]');

  const describeOutcome = (outcome) => {
    const list = steps.map((step) => ({ step, ...outcome.get(step.id) }));
    const actions = list.filter((i) => ACTION_KINDS.has(i.step.kind));
    const yes = actions.filter((i) => i.status === 'yes').length;
    const maybe = actions.filter((i) => i.status === 'maybe').length;
    const stop = list.find((i) => i.status === 'yes' && (i.step.kind === 'return' || i.step.kind === 'throw'));
    const parts = [`<b>${yes}</b> paso${yes === 1 ? '' : 's'} se ejecutaría${yes === 1 ? '' : 'n'}`];
    if (maybe) parts.push(`<b>${maybe}</b> depende${maybe === 1 ? '' : 'n'} de datos`);
    if (stop) parts.push(`termina en la <b>línea ${stop.step.line}</b>: ${esc(stop.step.title)}`);
    return parts.join(' · ');
  };

  const refresh = () => {
    const values = readValues();
    const active = anyValue();
    const outcome = active ? simulate(report, values) : null;
    container.querySelectorAll('.flow-step').forEach((row) => {
      const step = steps.find((s) => s.id === Number(row.dataset.step));
      const info = outcome?.get(step.id);
      const status = info?.status ?? 'none';
      row.dataset.state = status;
      row.hidden = Boolean(onlyActive.checked && outcome && status === 'no');
      const labels = BRANCH_KINDS.has(step.kind) ? BRANCH_STATUS_LABEL : (step.kind === 'decision' || step.kind === 'try') ? CONTAINER_STATUS_LABEL : STATUS_LABEL;
      row.querySelector('[data-status]').textContent = labels[status];
      row.querySelector('[data-order]').textContent = info?.order ? `#${info.order}` : '';
    });
    outcomeBox.hidden = !outcome;
    if (outcome) outcomeBox.innerHTML = describeOutcome(outcome);
  };

  const script = container.querySelector('[data-script]');
  const mode = container.querySelector('[data-script-mode]');
  const updateScript = () => {
    const rollback = container.querySelector('[data-rollback]').checked;
    script.textContent = mode.value === 'all'
      ? buildAllScenarios(report, { database, rollback })
      : buildScript(report, readValues(), { database, rollback });
  };
  const refreshAll = () => { refresh(); updateScript(); };
  const fill = (values) => {
    container.querySelectorAll('[data-param]').forEach((input) => { input.value = values[input.dataset.param] ?? ''; });
  };

  if (scenarios[0]) {
    fill(scenarios[0].values);
    container.querySelector('[data-scenario-pick="0"]')?.setAttribute('aria-pressed', 'true');
  }

  container.addEventListener('input', (event) => {
    if (event.target.matches('[data-param]')) {
      mode.value = 'current';
      container.querySelectorAll('[data-scenario-pick]').forEach((b) => b.setAttribute('aria-pressed', 'false'));
    }
    refreshAll();
  });

  container.addEventListener('change', (event) => {
    if (event.target.matches('[data-only-active]')) refresh();
  });

  container.addEventListener('click', (event) => {
    const target = event.target.closest('[data-target]');
    if (target) onPickTarget?.(target.dataset.target);

    const pick = event.target.closest('[data-scenario-pick]');
    if (pick) {
      fill(scenarios[Number(pick.dataset.scenarioPick)].values);
      container.querySelectorAll('[data-scenario-pick]').forEach((b) => b.setAttribute('aria-pressed', String(b === pick)));
      mode.value = 'current';
      refreshAll();
    }

    if (event.target.closest('[data-download]')) {
      const blob = new Blob([script.textContent], { type: 'text/plain;charset=utf-8' });
      const link = document.createElement('a');
      link.href = URL.createObjectURL(blob);
      link.download = `${report.schema}.${report.name}_validacion.sql`;
      link.click();
      setTimeout(() => URL.revokeObjectURL(link.href), 1000);
    }

    if (event.target.closest('[data-copy]')) {
      const button = event.target.closest('[data-copy]');
      const done = () => { button.textContent = '¡Copiado!'; setTimeout(() => { button.textContent = 'Copiar script'; }, 1500); };
      if (navigator.clipboard?.writeText) navigator.clipboard.writeText(script.textContent).then(done, () => { });
      else { const range = document.createRange(); range.selectNodeContents(script); getSelection().removeAllRanges(); getSelection().addRange(range); document.execCommand('copy'); done(); }
    }

    if (event.target.closest('[data-simulate]')) {
      const values = readValues();
      const active = anyValue();
      const outcome = active ? simulate(report, values) : null;
      const chosen = steps
        .filter((step) => MARKABLE_KINDS.has(step.kind))
        .map((step) => ({ step, status: outcome ? outcome.get(step.id).status : 'maybe' }))
        .filter((item) => item.status !== 'no')
        .flatMap(({ step, status }) => step.targets
          .filter((t) => !t.startsWith('#') && !t.startsWith('@'))
          .map((t) => ({ branch: { kind: step.kind, target: t }, status })));
      onSimulate?.(chosen, active);
    }
  });

  refreshAll();
}

export function buildMigrationPrompt(node) {
  return `Migra ${node.schema}.${node.name} a C#: solo la lógica (sin SQL en el código; los datos se quedan en los SP) y con pruebas unitarias.`;
}
