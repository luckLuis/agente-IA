const SEVERITY_ORDER = { high: 0, medium: 1, low: 2 };
const AMBIENT = new Set(['hello', 'loaded', 'selected', 'focusSet', 'focusCleared', 'flowSimulated', 'chatWaiting', 'chatDone', 'idle', 'petSwitch']);

const list = (items, max = 3) => {
  const shown = items.slice(0, max);
  const extra = items.length - shown.length;
  return shown.join(', ') + (extra > 0 ? ` y ${extra} más` : '');
};
const plural = (n, one, many) => `${n} ${n === 1 ? one : many}`;
const nightly = () => { const h = new Date().getHours(); return h >= 23 || h < 5; };
const lunch = () => { const h = new Date().getHours(); return h >= 12 && h < 14; };

const VOICES = {
  cat: {
    hello: [
      () => '*levanta la cabeza* Ah, llegaste. Soy Mishi. Elige un SP y lo reviso.',
      () => 'Mishi presente. Dime qué procedimiento te preocupa.'
    ],
    petSwitch: [() => '*se estira* Volví. Seguimos donde lo dejamos.'],
    loaded: [
      (c) => `${c.db} cargada: ${c.objects.toLocaleString('es')} objetos. Empieza por los más referenciados; ahí suele estar el riesgo.`,
      (c) => `Listo, ${c.db}. Si algo se mueve en esa red, lo noto.`
    ],
    selected: [
      (c) => c.fanIn >= 15 ? `${c.name} lo usan ${c.fanIn} objetos. Si lo tocas, se nota en todos lados.` : null,
      (c) => c.fanIn === 0 && c.kind === 'StoredProcedure' ? `Nadie llama a ${c.name} dentro de la base. Quizá lo invoca la aplicación, o está en desuso.` : null,
      (c) => c.fanOut >= 10 ? `${c.name} depende de ${c.fanOut} objetos. Es un procedimiento con muchas manos metidas.` : null,
      (c) => `*mira ${c.name} sin pestañear*`
    ],
    sniffStart: [
      (c) => `Revisando ${c.name}…`,
      (c) => `Veamos qué hay en ${c.name}.`
    ],
    sniffSql: [() => 'Leyendo tu consulta…'],
    clean: [
      (c) => `${c.label} está limpio. No encontré nada que me incomode.`,
      (c) => `Sin riesgos en ${c.label}. Puedes seguir tranquilo.`
    ],
    optimizations: [
      (c) => `No hay riesgos graves, pero ${plural(c.count, 'cosa mejorable', 'cosas mejorables')}. Mira el detalle.`
    ],
    known: [
      (c) => c.tables.length ? `Esto ya lo vi: ${list(c.tables)} estaban en mi libreta.` : 'Este patrón ya lo tenía anotado.'
    ],
    learned: [
      (c) => `Primera vez que veo ${list(c.tables)}. Lo anoto.`
    ],
    'update-sin-where': [
      (c) => `Ese UPDATE sobre ${c.table} no tiene WHERE: modificaría todas las filas. Revísalo antes de ejecutar.`,
      (c) => `UPDATE sin WHERE en ${c.table}. Si es intencional, al menos corre antes dentro de una transacción.`
    ],
    'delete-sin-where': [
      (c) => `Ese DELETE sobre ${c.table} no tiene WHERE. Borraría todo. Yo no lo ejecutaría así.`
    ],
    truncate: [
      (c) => `TRUNCATE en ${c.table}. No se puede deshacer fila por fila y suele chocar con claves foráneas.`
    ],
    drop: [
      (c) => `Hay un DROP${c.table ? ` sobre ${c.table}` : ''}. Asegúrate de tener respaldo.`
    ],
    'alter-critico': [
      (c) => `Vas a alterar ${c.table ?? 'un objeto crítico'}. Hay quien depende de eso: respalda y avisa antes.`
    ],
    'tran-sin-trycatch': [
      (c) => 'Abre una transacción sin TRY/CATCH. Si algo falla a medias, puede quedarse abierta y bloquear a los demás.'
    ],
    'sin-nocount': [() => 'No usa SET NOCOUNT ON. Es poca cosa, pero genera tráfico de más.'],
    nolock: [(c) => `Usa NOLOCK${c.table ? ` en ${c.table}` : ''}. Puede leer datos sucios; confirma que es aceptable.`],
    'lock-pesado': [(c) => `Toma un bloqueo pesado${c.table ? ` en ${c.table}` : ''}. En horas de carga puede frenar a otros.`],
    cursor: [() => 'Hay un cursor. Casi siempre se puede resolver por conjuntos y más rápido.'],
    'select-star': [(c) => `SELECT *${c.table ? ` sobre ${c.table}` : ''}: trae columnas que quizá no usas y se rompe si cambia la tabla.`],
    'sql-dinamico': [() => 'Arma SQL dinámico. Ahí no puedo ver qué se ejecuta; revisa que esté parametrizado.'],
    manyRisks: [
      (c) => `${plural(c.count, 'hallazgo', 'hallazgos')} en ${c.label}, ${plural(c.high, 'grave', 'graves')}. Empieza por los de la lista en rojo.`
    ],
    error: [
      (c) => `No pude revisarlo: ${c.message}`,
      (c) => `Algo salió mal: ${c.message}`
    ],
    verifyGreen: [
      (c) => `${plural(c.passed, 'prueba pasó', 'pruebas pasaron')}, sin tocar la base. Buen trabajo.`,
      (c) => `Todo en verde (${c.passed}). La lógica migrada se comporta igual en los escenarios probados.`
    ],
    verifyRed: [
      (c) => `${plural(c.failed, 'prueba falló', 'pruebas fallaron')}. Mira cuál y por qué; el copiloto puede corregirlo.`
    ],
    verifyNoCompile: [() => 'El código no compila. Lee el primer error: suele arrastrar a los demás.'],
    flowSimulated: [
      (c) => c.count ? `Con esos valores se llega a ${plural(c.count, 'objeto', 'objetos')}. Es una predicción, no se ejecutó nada.` : 'Con esa entrada no llega a ningún objeto cargado.'
    ],
    focusSet: [
      (c) => `Centrado en ${c.name}. Muestro solo lo que está a ${plural(c.hops, 'salto', 'saltos')}.`
    ],
    focusCleared: [() => 'Foco quitado. Vuelves a ver toda la red.'],
    chatWaiting: [
      (c) => `El copiloto lleva ${c.seconds} s. Las respuestas largas tardan; puedes detenerlo si quieres.`
    ],
    chatDone: [() => 'Ya respondió. Si menciona objetos, los marco en la red.'],
    levelUp: [(c) => `Subí al nivel ${c.level}.`],
    idle: [
      () => nightly() ? 'Ya es tarde. Los procedimientos no se van a ir a ningún lado; mañana se ven mejor.' : null,
      () => lunch() ? '*mira el reloj* Es hora de comer. Revisar con hambre da malos resultados.' : null,
      (c) => c.stats.sniffed ? `Llevo ${plural(c.stats.sniffed, 'objeto revisado', 'objetos revisados')} hoy${c.stats.high ? `, ${plural(c.stats.high, 'con riesgo alto', 'con riesgo alto')}` : ''}.` : null,
      () => '*se lame una pata* Cuando quieras que revise algo, avisa.',
      () => 'Consejo: antes de tocar un SP, mira quién lo usa con «Analizar impacto».'
    ]
  },
  husky: {
    hello: [
      () => '¡Guau! Soy Bruno. Elige un SP y lo reviso ahora mismo.',
      () => '*mueve la cola* ¡Hola! ¿Qué procedimiento revisamos?'
    ],
    petSwitch: [() => '¡Aquí estoy! *da una vuelta sobre sí mismo* ¿Seguimos?'],
    loaded: [
      (c) => `¡${c.db} cargada! ${c.objects.toLocaleString('es')} objetos. Empiezo por los más referenciados.`,
      (c) => `¡Listo con ${c.db}! Hay mucho por oler aquí.`
    ],
    selected: [
      (c) => c.fanIn >= 15 ? `¡${c.name} lo usan ${c.fanIn} objetos! Si lo cambias, mucha gente se entera.` : null,
      (c) => c.fanIn === 0 && c.kind === 'StoredProcedure' ? `A ${c.name} no lo llama nadie en la base. ¿Lo usa la aplicación o está abandonado?` : null,
      (c) => c.fanOut >= 10 ? `${c.name} depende de ${c.fanOut} objetos. ¡Qué ocupado está!` : null,
      (c) => `*olfatea ${c.name}*`
    ],
    sniffStart: [
      (c) => `¡A oler ${c.name}!`,
      (c) => `Sniff sniff… revisando ${c.name}.`
    ],
    sniffSql: [() => 'Sniff sniff… leyendo tu consulta.'],
    clean: [
      (c) => `¡${c.label} está limpio! No huelo ningún problema.`,
      (c) => `Nada raro en ${c.label}. *mueve la cola*`
    ],
    optimizations: [
      (c) => `No hay riesgos graves, pero ${plural(c.count, 'cosa se puede mejorar', 'cosas se pueden mejorar')}. Mira el detalle.`
    ],
    known: [
      (c) => c.tables.length ? `¡Esto me suena! ${list(c.tables)} ya los había olfateado.` : '¡Este patrón ya lo conocía!'
    ],
    learned: [
      (c) => `¡${list(c.tables)} es nuevo para mí! Lo guardo en mi memoria.`
    ],
    'update-sin-where': [
      (c) => `¡Ojo! Ese UPDATE en ${c.table} no tiene WHERE y cambiaría todas las filas. Revísalo antes de correrlo.`,
      (c) => `¡UPDATE sin WHERE en ${c.table}! Si es a propósito, prueba primero con una transacción.`
    ],
    'delete-sin-where': [
      (c) => `¡Cuidado! Ese DELETE en ${c.table} no tiene WHERE y borraría todo.`
    ],
    truncate: [
      (c) => `TRUNCATE en ${c.table}: no hay vuelta atrás y puede chocar con claves foráneas.`
    ],
    drop: [
      (c) => `¡Hay un DROP${c.table ? ` sobre ${c.table}` : ''}! Ten un respaldo a mano.`
    ],
    'alter-critico': [
      (c) => `Vas a alterar ${c.table ?? 'un objeto crítico'}, y muchos dependen de él. Respalda y avisa al equipo.`
    ],
    'tran-sin-trycatch': [
      () => 'Abre una transacción sin TRY/CATCH. Si falla a la mitad, puede quedar abierta y bloquear a otros.'
    ],
    'sin-nocount': [() => 'Falta SET NOCOUNT ON. Es un detalle, pero ahorra tráfico.'],
    nolock: [(c) => `Usa NOLOCK${c.table ? ` en ${c.table}` : ''}: puede leer datos sucios. Confirma que está bien.`],
    'lock-pesado': [(c) => `Toma un bloqueo pesado${c.table ? ` en ${c.table}` : ''}. Con carga alta puede frenar a los demás.`],
    cursor: [() => 'Hay un cursor. Casi siempre se resuelve por conjuntos, más rápido.'],
    'select-star': [(c) => `SELECT *${c.table ? ` sobre ${c.table}` : ''}: trae columnas que quizá no necesitas.`],
    'sql-dinamico': [() => 'SQL dinámico: ahí no veo qué se ejecuta. Revisa que esté parametrizado.'],
    manyRisks: [
      (c) => `¡${plural(c.count, 'hallazgo', 'hallazgos')} en ${c.label}, ${plural(c.high, 'grave', 'graves')}! Empieza por los rojos.`
    ],
    error: [
      (c) => `¡Uy! No pude revisarlo: ${c.message}`,
      (c) => `Algo falló: ${c.message}`
    ],
    verifyGreen: [
      (c) => `¡${plural(c.passed, 'prueba pasó', 'pruebas pasaron')}, sin tocar la base! *mueve la cola*`,
      (c) => `¡Todo en verde (${c.passed})! La migración se comporta igual en esos escenarios.`
    ],
    verifyRed: [
      (c) => `${plural(c.failed, 'prueba falló', 'pruebas fallaron')}. Mira cuál; el copiloto puede corregirlo.`
    ],
    verifyNoCompile: [() => 'No compila. Empieza por el primer error, suele causar los demás.'],
    flowSimulated: [
      (c) => c.count ? `¡Con esos valores llego a ${plural(c.count, 'objeto', 'objetos')}! Es una predicción, no se ejecutó nada.` : 'Con esa entrada no llego a ningún objeto cargado.'
    ],
    focusSet: [
      (c) => `¡Centrado en ${c.name}! Solo muestro lo que está a ${plural(c.hops, 'salto', 'saltos')}.`
    ],
    focusCleared: [() => 'Foco quitado. ¡Otra vez toda la red!'],
    chatWaiting: [
      (c) => `El copiloto lleva ${c.seconds} s. Si es larga tarda; puedes detenerlo.`
    ],
    chatDone: [() => '¡Respondió! Si menciona objetos, los marco en la red.'],
    levelUp: [(c) => `¡Subí al nivel ${c.level}!`],
    idle: [
      () => nightly() ? 'Es tarde… *bosteza* Mañana se ve mejor.' : null,
      () => lunch() ? '*mira su plato vacío* ¿Ya comiste?' : null,
      (c) => c.stats.sniffed ? `Hoy llevo ${plural(c.stats.sniffed, 'objeto revisado', 'objetos revisados')}${c.stats.high ? `, ${plural(c.stats.high, 'con riesgo alto', 'con riesgo alto')}` : ''}.` : null,
      () => '*apoya la cabeza en tus pies* Aquí estoy por si necesitas algo.',
      () => 'Idea: antes de tocar un SP, mira quién lo usa con «Analizar impacto».'
    ]
  }
};

const STATE_BY_EVENT = {
  sniffStart: 'studying', sniffSql: 'studying', error: 'alert', verifyGreen: 'happy', verifyRed: 'alert', verifyNoCompile: 'alert',
  clean: 'happy', known: 'smart', learned: 'studying', hello: 'happy', petSwitch: 'happy', loaded: 'smart', levelUp: 'happy',
  focusSet: 'studying', chatWaiting: 'studying', chatDone: 'smart', flowSimulated: 'smart'
};

export function createVoice(pet, stats = { sniffed: 0, high: 0 }) {
  const table = VOICES[pet] ?? VOICES.cat;
  const last = new Map();
  const cooldowns = { selected: 25000, idle: 0 };
  const lastSpoken = new Map();

  function pickFrom(event, ctx) {
    const pool = table[event];
    if (!pool?.length) return null;
    const candidates = pool.map((fn, index) => ({ index, text: fn(ctx) })).filter((item) => item.text);
    if (!candidates.length) return null;
    const previous = last.get(event);
    const fresh = candidates.filter((item) => item.index !== previous);
    const choice = (fresh.length ? fresh : candidates)[Math.floor(Math.random() * (fresh.length || candidates.length))];
    last.set(event, choice.index);
    return choice.text;
  }

  return {
    stats,
    isAmbient: (event) => AMBIENT.has(event),
    line(event, ctx = {}) {
      const cooldown = cooldowns[event];
      const now = Date.now();
      if (cooldown && now - (lastSpoken.get(event) ?? 0) < cooldown) return null;
      const text = pickFrom(event, { ...ctx, stats });
      if (text) lastSpoken.set(event, now);
      return text ? { text, state: STATE_BY_EVENT[event] ?? null } : null;
    },
    reply(reply, label) {
      const findings = [...(reply.findings ?? [])].sort((a, b) => (SEVERITY_ORDER[a.severity] ?? 3) - (SEVERITY_ORDER[b.severity] ?? 3));
      const risks = findings.filter((f) => !f.isOptimization);
      const high = findings.filter((f) => f.severity === 'high');
      const tables = reply.learned_pattern ? [] : [];
      stats.sniffed += 1;
      stats.high += high.length ? 1 : 0;

      let event;
      let ctx = { label, count: findings.length, high: high.length, tables };
      if (high.length > 1 || (high.length && risks.length > 3)) {
        event = 'manyRisks';
      } else if (risks.length) {
        event = risks[0].id;
        ctx = { ...ctx, table: risks[0].table };
      } else if (findings.length) {
        event = 'optimizations';
      } else {
        event = reply.cat_state === 'smart' ? 'known' : reply.cat_state === 'studying' ? 'learned' : 'clean';
      }
      if (!table[event]) event = findings.length ? 'manyRisks' : 'clean';
      const line = pickFrom(event, { ...ctx, stats });
      return line ? { text: line, state: high.length ? 'alert' : reply.cat_state } : null;
    }
  };
}
