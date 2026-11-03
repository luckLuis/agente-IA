// Panel "Datos de entrada": parámetros de un SP y condiciones para llegar a cada EXEC / escritura.
// Los valores se evalúan en el navegador contra las condiciones que extrajo el backend.

const API_BASE = '/api';   // ajustar si la API cuelga de otra ruta

export async function fetchFlowInputs({ instance, database, schema, name }) {
  const query = new URLSearchParams({ instance, database, schema, name });
  const response = await fetch(`${API_BASE}/flow/inputs?${query}`);
  if (!response.ok) {
    let message = `Error ${response.status}`;
    try { message = (await response.json()).message ?? message; } catch { /* sin cuerpo JSON */ }
    throw new Error(message);
  }
  return response.json();
}

// ---- Evaluación ----------------------------------------------------------------------------

const isNumber = (text) => text !== '' && Number.isFinite(Number(text));

function parseValue(raw) {
  if (raw === undefined || raw === null || raw.trim() === '') return { provided: false };
  return raw.trim().toUpperCase() === 'NULL' ? { provided: true, value: null } : { provided: true, value: raw.trim() };
}

function compare(a, b) {
  if (isNumber(a) && isNumber(b)) return Math.sign(Number(a) - Number(b));
  return a.localeCompare(b, undefined, { sensitivity: 'accent' });   // SQL Server: colación insensible a mayúsculas
}

/** true / false / null (desconocido) para una restricción simple. */
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

/** 'yes' | 'no' | 'maybe' para una condición. values: { '@nombre': 'texto' } (claves en minúsculas). */
export function evaluateCondition(condition, values) {
  let unknown = false;
  for (const constraint of condition.constraints) {
    const result = checkConstraint(constraint, values[constraint.parameter.toLowerCase()]);
    if (result === false) return 'no';
    if (result === null) unknown = true;
  }
  if (unknown) return 'maybe';
  // Todo lo evaluable se cumple; si hay partes que dependen de datos (tablas, variables, funciones) no se puede asegurar.
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

// ---- Valores sugeridos y script ------------------------------------------------------------

const baseType = (type) => type.toLowerCase().replace(/[\[\]]/g, '').replace(/\(.*\)/, '').trim();
const NUMERIC_TYPES = new Set(['int', 'bigint', 'smallint', 'tinyint', 'decimal', 'numeric', 'float', 'real', 'money', 'smallmoney', 'bit']);
const UNICODE_TYPES = new Set(['nvarchar', 'nchar', 'ntext']);

/** Valor de ejemplo válido para el tipo (cuando ninguna condición exige uno concreto). */
function sampleFor(type) {
  const t = baseType(type);
  if (NUMERIC_TYPES.has(t)) return '1';
  if (t === 'date') return '2025-01-01';
  if (t.startsWith('datetime') || t === 'smalldatetime') return '2025-01-01T00:00:00';
  if (t === 'time') return '12:00:00';
  if (t === 'uniqueidentifier') return '00000000-0000-0000-0000-000000000001';
  return 'X';
}

/** Literal T-SQL para un valor escrito por el usuario (null = NULL). */
function sqlLiteral(type, raw) {
  const input = parseValue(raw);
  if (!input.provided || input.value === null) return 'NULL';
  const t = baseType(type);
  if (NUMERIC_TYPES.has(t) && isNumber(input.value)) return input.value;
  return `${UNICODE_TYPES.has(t) ? 'N' : ''}'${input.value.replace(/'/g, "''")}'`;
}

/**
 * Busca valores de entrada que cumplan las condiciones de una rama.
 * Devuelve { values, ok, dataNotes }: ok = false si las condiciones se contradicen;
 * dataNotes = condiciones que dependen de datos de la base (no se pueden fijar con parámetros).
 */
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

  // Los obligatorios sin condición necesitan algún valor para poder ejecutar el SP.
  for (const parameter of report.parameters) {
    const key = parameter.name.toLowerCase();
    if (!parameter.isOptional && !parameter.isOutput && !(key in values)) values[key] = sampleFor(parameter.dataType);
  }

  const dataNotes = branch.conditions.filter((c) => !c.exact || c.dependsOnData).map((c) => c.text);
  return { values, ok, dataNotes };
}

/** Script T-SQL listo para pegar en SSMS. rollback: envuelve en transacción y la deshace. */
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
  // TRY/CATCH: si el SP lanza THROW/RAISERROR (p. ej. una validación), el error se muestra y el script sigue limpio.
  lines.push('BEGIN TRY', `    ${exec}`);
  if (outputs.length) lines.push(`    SELECT ${outputs.map((p) => `${p.name}_out AS [${p.name.slice(1)}]`).join(', ')};`);
  lines.push('    SELECT 0 AS CodigoError, \'OK\' AS Resultado;', 'END TRY', 'BEGIN CATCH',
    '    SELECT ERROR_NUMBER() AS CodigoError, ERROR_MESSAGE() AS Resultado;', 'END CATCH;');
  if (rollback) lines.push('IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;');
  return lines.join('\n');
}

/**
 * Escenarios de prueba ya resueltos: valores de entrada que recorren cada rama y cada validación que corta.
 * Cada escenario trae lo que se ejecutaría realmente con esos valores (se reevalúan todas las ramas).
 */
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

/** Un solo script con todos los escenarios, cada uno con los valores ya cargados y lo que se espera. */
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

// ---- Panel ---------------------------------------------------------------------------------

const STATUS_LABEL = { yes: 'Se ejecuta', maybe: 'Depende', no: 'No se ejecuta', none: '' };
const KIND_LABEL = { exec: 'EXEC', write: 'Escribe', dynamic: 'SQL dinámico' };

/**
 * Dibuja el panel dentro de `container`. onSimulate(branches) recibe las acciones que se ejecutarían
 * (estado 'yes' o 'maybe') cuando el usuario pulsa «Marcar en la red».
 */
export function mountInputsPanel(container, report, { escapeHtml, onSimulate, onPickTarget, database = null }) {
  const esc = escapeHtml;
  const required = report.parameters.filter((p) => !p.isOptional && !p.isOutput);
  const inputParams = report.parameters;

  const paramRows = inputParams.map((p) => `
    <tr>
      <td><code>${esc(p.name)}</code></td>
      <td>${esc(p.dataType)}</td>
      <td>${p.isOutput ? '<span class="badge info">OUT</span>' : p.isOptional ? `<span class="badge">Opcional${p.defaultValue ? ` · ${esc(p.defaultValue)}` : ''}</span>` : '<span class="badge crit">Obligatorio</span>'}</td>
      <td>${p.isOutput ? '' : `<input class="flow-input" type="text" data-param="${esc(p.name.toLowerCase())}" placeholder="valor" aria-label="${esc(p.name)}" autocomplete="off">`}</td>
    </tr>`).join('');

  const conditionChips = (branch) => branch.conditions.length === 0
    ? '<span class="chip-cond always">siempre</span>'
    : branch.conditions.map((c) => `<span class="chip-cond${c.dependsOnData ? ' data' : ''}" title="${c.dependsOnData ? 'Depende de datos de tablas, variables o funciones' : ''}">${esc(c.text)}</span>`).join('');

  const branchRows = report.branches.map((b, index) => `
    <li class="flow-branch" data-branch="${index}">
      <div class="flow-branch-head">
        <span class="kind kind-${esc(b.kind)}">${esc(KIND_LABEL[b.kind] ?? b.kind)}</span>
        ${b.kind === 'dynamic' ? `<span class="name">${esc(b.target)}</span>` : `<button type="button" class="link" data-target="${esc(b.target)}">${esc(b.target)}</button>`}
        <span class="status" data-status></span>
        ${b.kind === 'dynamic' ? '' : `<button type="button" class="btn ghost small" data-reach="${index}" title="Rellena los valores necesarios para llegar a esta acción">Probar este flujo</button>`}
      </div>
      <div class="flow-conds">${conditionChips(b)}</div>
      ${b.arguments.length ? `<div class="flow-args">con ${b.arguments.map((a) => `<code>${esc(a.name)} = ${esc(a.value)}</code>`).join(' ')}</div>` : ''}
    </li>`).join('');

  const guardRows = report.guards.map((g, index) => `
    <li class="flow-guard" data-guard="${index}"><span class="chip-cond">${esc(g.condition.text)}</span> <span class="muted">→ ${esc(g.outcome)}</span><span class="status" data-status></span></li>`).join('');

  const warnings = report.warnings.map((w) => `<div class="msg warn">${esc(w)}</div>`).join('');

  container.innerHTML = `
    <div class="flow-panel">
      <h3>Datos de entrada · ${esc(report.schema)}.${esc(report.name)}</h3>
      ${warnings}
      ${report.parameters.length === 0
      ? '<div class="muted-block">Este procedimiento no recibe parámetros.</div>'
      : `<p class="muted">${required.length
        ? `Obligatorios: ${required.map((p) => `<code>${esc(p.name)}</code>`).join(', ')}.`
        : 'Todos los parámetros son opcionales.'} Escriba valores para ver qué rama se ejecutaría (<code>NULL</code> para nulo; vacío = sin dato).</p>
           <table class="flow-params"><thead><tr><th>Parámetro</th><th>Tipo</th><th></th><th>Valor</th></tr></thead><tbody>${paramRows}</tbody></table>`}
      ${report.guards.length ? `<h4>Validaciones que cortan el flujo</h4><ul class="flow-list">${guardRows}</ul>` : ''}
      <h4>Qué ocurre según la entrada</h4>
      ${report.branches.length ? `<ul class="flow-list">${branchRows}</ul>` : '<div class="muted-block">No se encontraron EXEC ni escrituras.</div>'}
      <h4>Script para validar en la base</h4>
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
      <div class="actions"><button type="button" class="btn primary small" data-simulate ${report.branches.length ? '' : 'disabled'}>Marcar en la red</button></div>
    </div>`;

  const readValues = () => {
    const values = {};
    container.querySelectorAll('[data-param]').forEach((input) => { values[input.dataset.param] = input.value; });
    return values;
  };
  const anyValue = () => Object.values(readValues()).some((v) => v.trim() !== '');

  const refresh = () => {
    const values = readValues();
    const active = anyValue();
    container.querySelectorAll('.flow-branch').forEach((row) => {
      const status = active ? evaluateBranch(report.branches[Number(row.dataset.branch)], values) : 'none';
      row.dataset.state = status;
      row.querySelector('[data-status]').textContent = STATUS_LABEL[status];
    });
    container.querySelectorAll('.flow-guard').forEach((row) => {
      const status = active ? evaluateCondition(report.guards[Number(row.dataset.guard)].condition, values) : 'none';
      row.dataset.state = status;
      row.querySelector('[data-status]').textContent = status === 'yes' ? 'Con esta entrada se corta aquí' : status === 'maybe' ? 'Podría cortarse' : '';
    });
  };

  const notes = container.querySelector('[data-notes]');
  const script = container.querySelector('[data-script]');
  const mode = container.querySelector('[data-script-mode]');
  const updateScript = () => {
    const rollback = container.querySelector('[data-rollback]').checked;
    script.textContent = mode.value === 'all'
      ? buildAllScenarios(report, { database, rollback })
      : buildScript(report, readValues(), { database, rollback });
  };
  const refreshAll = () => { refresh(); updateScript(); };

  // Con los datos ya cargados del primer escenario, el panel sirve sin tocar nada.
  const first = buildScenarios(report)[0];
  if (first) container.querySelectorAll('[data-param]').forEach((input) => { input.value = first.values[input.dataset.param] ?? ''; });

  container.addEventListener('input', (event) => {
    if (event.target.matches('[data-param]')) mode.value = 'current';   // el usuario editó: el script sigue sus valores
    refreshAll();
  });
  container.addEventListener('click', (event) => {
    const target = event.target.closest('[data-target]');
    if (target) onPickTarget?.(target.dataset.target);
    const reach = event.target.closest('[data-reach]');
    if (reach) {
      const branch = report.branches[Number(reach.dataset.reach)];
      const suggestion = suggestValues(report, branch);
      container.querySelectorAll('[data-param]').forEach((input) => { input.value = suggestion.values[input.dataset.param] ?? ''; });
      mode.value = 'current';
      const lines = [];
      if (!suggestion.ok) lines.push('Las condiciones de esta rama se contradicen o no se pueden cumplir solo con parámetros; revise el código.');
      if (suggestion.dataNotes.length) {
        lines.push('Además debe cumplirse en la base (depende de datos, no de parámetros):');
        lines.push(...suggestion.dataNotes.map((n) => `• ${n}`));
      }
      notes.hidden = lines.length === 0;
      notes.textContent = lines.join('\n');
      refreshAll();
      script.scrollIntoView?.({ block: 'nearest', behavior: 'smooth' });
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
      const chosen = report.branches
        .map((branch) => ({ branch, status: active ? evaluateBranch(branch, values) : 'maybe' }))
        .filter((item) => item.status !== 'no');
      onSimulate?.(chosen, active);
    }
  });
  refreshAll();
}

// ---- Petición de migración a C# ------------------------------------------------------------

/**
 * Texto corto para pedir la migración. Las reglas (solo lógica, sin SQL en C#, pruebas xUnit y de paridad) viven en el
 * prompt del sistema del backend, así no se pagan tokens de más en cada petición.
 */
export function buildMigrationPrompt(node) {
  return `Migra ${node.schema}.${node.name} a C#: solo la lógica (sin SQL en el código; los datos se quedan en los SP) y con pruebas unitarias.`;
}
