// Mishi-SQL: mascota que reacciona al análisis (estados, burbuja, experiencia y nivel).
// Puede ser un gato o un husky siberiano; se elige desde el HUD y se recuerda en el navegador.
// Todo es local y determinista: no consume tokens del LLM.

const API_BASE = '/api';   // ajustar si la API cuelga de otra ruta
const REDUCED = typeof matchMedia === 'function' && matchMedia('(prefers-reduced-motion: reduce)').matches;
const BUBBLE_MS = 9000;
const PET_KEY = 'sqlgraphcopilot.mishi.pet';

const CAT_SVG = `
<svg viewBox="0 -16 120 128" aria-hidden="true" class="mishi-svg">
  <defs>
    <radialGradient id="mishi-aura" cx="50%" cy="50%" r="50%"><stop offset="0" stop-color="currentColor" stop-opacity=".55"/><stop offset="1" stop-color="currentColor" stop-opacity="0"/></radialGradient>
  </defs>
  <circle class="aura" cx="60" cy="58" r="58" fill="url(#mishi-aura)"/>
  <path class="tail" d="M88 92 C116 94 118 58 100 50" fill="none" stroke="#f59e0b" stroke-width="10" stroke-linecap="round"/>
  <ellipse cx="60" cy="94" rx="27" ry="17" fill="#fbbf24"/>
  <ellipse cx="60" cy="100" rx="15" ry="10" fill="#fde68a"/>
  <g class="ear ear-l"><polygon points="26,40 28,8 54,28" fill="#fbbf24"/><polygon points="32,33 33,17 46,27" fill="#fda4af"/></g>
  <g class="ear ear-r"><polygon points="94,40 92,8 66,28" fill="#fbbf24"/><polygon points="88,33 87,17 74,27" fill="#fda4af"/></g>
  <ellipse cx="60" cy="54" rx="35" ry="31" fill="#fcd34d"/>
  <path d="M52 27 L55 38 M60 25 L60 37 M68 27 L65 38" stroke="#f59e0b" stroke-width="3" stroke-linecap="round"/>
  <path class="fur" d="M25 44 l-9 -4 l8 -5 l-8 -6 l10 -1 M95 44 l9 -4 l-8 -5 l8 -6 l-10 -1" fill="none" stroke="#ef4444" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round"/>

  <g class="eyes e-happy"><path d="M38 52 q8 -9 16 0" fill="none" stroke="#1f2937" stroke-width="3.5" stroke-linecap="round"/><path d="M66 52 q8 -9 16 0" fill="none" stroke="#1f2937" stroke-width="3.5" stroke-linecap="round"/><ellipse cx="38" cy="62" rx="6" ry="3.5" fill="#fb7185" opacity=".55"/><ellipse cx="82" cy="62" rx="6" ry="3.5" fill="#fb7185" opacity=".55"/></g>
  <g class="eyes e-studying"><g class="blink"><circle cx="46" cy="54" r="5" fill="#1f2937"/><circle cx="74" cy="54" r="5" fill="#1f2937"/><circle cx="47.5" cy="52.5" r="1.6" fill="#fff"/><circle cx="75.5" cy="52.5" r="1.6" fill="#fff"/></g><circle cx="46" cy="52" r="11" fill="rgba(125,211,252,.15)" stroke="#38bdf8" stroke-width="2.5"/><circle cx="74" cy="52" r="11" fill="rgba(125,211,252,.15)" stroke="#38bdf8" stroke-width="2.5"/><path d="M57 52 h6" stroke="#38bdf8" stroke-width="2.5"/></g>
  <g class="eyes e-smart"><g class="blink"><circle cx="46" cy="52" r="7" fill="#1f2937"/><circle cx="74" cy="52" r="7" fill="#1f2937"/></g><path d="M44 49 l1.6 3 l3 .4 l-2.2 2.1 l.6 3 l-3 -1.6 l-3 1.6 l.6 -3 l-2.2 -2.1 l3 -.4z" fill="#fef08a" transform="translate(1,-1) scale(.8)"/><path d="M72 49 l1.6 3 l3 .4 l-2.2 2.1 l.6 3 l-3 -1.6 l-3 1.6 l.6 -3 l-2.2 -2.1 l3 -.4z" fill="#fef08a" transform="translate(1,-1) scale(.8)"/></g>
  <g class="eyes e-alert"><ellipse cx="45" cy="52" rx="9" ry="11" fill="#fff"/><ellipse cx="75" cy="52" rx="9" ry="11" fill="#fff"/><ellipse cx="45" cy="52" rx="2.6" ry="9" fill="#1f2937"/><ellipse cx="75" cy="52" rx="2.6" ry="9" fill="#1f2937"/><path d="M33 38 l16 6 M87 38 l-16 6" stroke="#b91c1c" stroke-width="3" stroke-linecap="round"/></g>

  <path d="M56 61 h8 l-4 5z" fill="#fb7185"/>
  <g class="mouth m-happy"><path d="M52 69 q8 8 16 0" fill="none" stroke="#1f2937" stroke-width="2.5" stroke-linecap="round"/></g>
  <g class="mouth m-studying"><path d="M54 70 q6 3 12 0" fill="none" stroke="#1f2937" stroke-width="2.5" stroke-linecap="round"/></g>
  <g class="mouth m-smart"><path d="M52 69 q5 6 9 2 q5 4 9 -2" fill="none" stroke="#1f2937" stroke-width="2.5" stroke-linecap="round"/></g>
  <g class="mouth m-alert"><path d="M52 68 q8 10 16 0z" fill="#7f1d1d"/><path d="M55 68 l2 4 l2 -4 M61 68 l2 4 l2 -4" fill="#fff" stroke="#fff" stroke-width="1"/></g>
  <path d="M14 58 l20 3 M14 68 l20 -2 M106 58 l-20 3 M106 68 l-20 -2" stroke="#92400e" stroke-width="1.6" stroke-linecap="round" opacity=".75"/>

  <g class="bulb"><circle cx="60" cy="-4" r="7" fill="#fef08a"/><path d="M57 3 h6 M58 6 h4" stroke="#f59e0b" stroke-width="2" stroke-linecap="round"/><path d="M60 -16 v4 M48 -10 l3 3 M72 -10 l-3 3" stroke="#fde047" stroke-width="2" stroke-linecap="round"/></g>
  <g class="dots"><circle cx="96" cy="14" r="2.6"/><circle cx="105" cy="8" r="2.6"/><circle cx="114" cy="2" r="2.6"/></g>
</svg>`;

// Husky siberiano: mismo contrato de clases que el gato (.ear-l/.ear-r, .tail, .fur, .eyes e-*, .mouth m-*,
// .blink, .bulb, .dots) para que mishi.css anime las expresiones sin cambios.
const HUSKY_SVG = `
<svg viewBox="0 -16 120 128" aria-hidden="true" class="mishi-svg">
  <defs>
    <radialGradient id="mishi-aura" cx="50%" cy="50%" r="50%"><stop offset="0" stop-color="currentColor" stop-opacity=".55"/><stop offset="1" stop-color="currentColor" stop-opacity="0"/></radialGradient>
  </defs>
  <circle class="aura" cx="60" cy="58" r="58" fill="url(#mishi-aura)"/>
  <g class="tail"><path d="M88 92 C116 94 118 58 100 50" fill="none" stroke="#64748b" stroke-width="12" stroke-linecap="round"/><path d="M88 92 C116 94 118 58 100 50" fill="none" stroke="#e2e8f0" stroke-width="4" stroke-linecap="round" opacity=".7"/></g>
  <ellipse cx="60" cy="94" rx="27" ry="17" fill="#64748b"/>
  <ellipse cx="60" cy="100" rx="15" ry="10" fill="#f1f5f9"/>
  <g class="ear ear-l"><path d="M24 46 Q22 14 34 8 Q48 10 56 30Z" fill="#475569"/><path d="M31 38 Q31 21 36 16 Q44 18 48 29Z" fill="#e2e8f0"/></g>
  <g class="ear ear-r"><path d="M96 46 Q98 14 86 8 Q72 10 64 30Z" fill="#475569"/><path d="M89 38 Q89 21 84 16 Q76 18 72 29Z" fill="#e2e8f0"/></g>
  <ellipse cx="60" cy="54" rx="35" ry="31" fill="#64748b"/>
  <path d="M52 28 L55 38 M60 26 L60 37 M68 28 L65 38" stroke="#94a3b8" stroke-width="3" stroke-linecap="round"/>
  <path d="M28 60 C32 55 40 58 45 51 C50 45 55 43 60 43 C65 43 70 45 75 51 C80 58 88 55 92 60 C90 76 77 85 60 85 C43 85 30 76 28 60Z" fill="#f8fafc"/>
  <ellipse cx="46" cy="42" rx="4.5" ry="3" fill="#f8fafc"/><ellipse cx="74" cy="42" rx="4.5" ry="3" fill="#f8fafc"/>
  <ellipse cx="60" cy="71" rx="16" ry="12.5" fill="#fff" stroke="#cbd5e1" stroke-width="1"/>
  <path class="fur" d="M25 44 l-9 -4 l8 -5 l-8 -6 l10 -1 M95 44 l9 -4 l-8 -5 l8 -6 l-10 -1" fill="none" stroke="#ef4444" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round"/>

  <g class="eyes e-happy"><path d="M38 52 q8 -9 16 0" fill="none" stroke="#1f2937" stroke-width="3.5" stroke-linecap="round"/><path d="M66 52 q8 -9 16 0" fill="none" stroke="#1f2937" stroke-width="3.5" stroke-linecap="round"/><ellipse cx="38" cy="63" rx="6" ry="3.5" fill="#fb7185" opacity=".5"/><ellipse cx="82" cy="63" rx="6" ry="3.5" fill="#fb7185" opacity=".5"/></g>
  <g class="eyes e-studying"><g class="blink"><circle cx="46" cy="54" r="5.5" fill="#38bdf8" stroke="#1f2937" stroke-width="1.2"/><circle cx="74" cy="54" r="5.5" fill="#38bdf8" stroke="#1f2937" stroke-width="1.2"/><circle cx="46" cy="54" r="2.4" fill="#0f172a"/><circle cx="74" cy="54" r="2.4" fill="#0f172a"/><circle cx="47.6" cy="52.4" r="1.4" fill="#fff"/><circle cx="75.6" cy="52.4" r="1.4" fill="#fff"/></g><circle cx="46" cy="52" r="11" fill="rgba(125,211,252,.15)" stroke="#38bdf8" stroke-width="2.5"/><circle cx="74" cy="52" r="11" fill="rgba(125,211,252,.15)" stroke="#38bdf8" stroke-width="2.5"/><path d="M57 52 h6" stroke="#38bdf8" stroke-width="2.5"/></g>
  <g class="eyes e-smart"><g class="blink"><circle cx="46" cy="52" r="7" fill="#38bdf8" stroke="#1f2937" stroke-width="1.2"/><circle cx="74" cy="52" r="7" fill="#38bdf8" stroke="#1f2937" stroke-width="1.2"/><circle cx="46" cy="52" r="3.4" fill="#0f172a"/><circle cx="74" cy="52" r="3.4" fill="#0f172a"/></g><path d="M44 49 l1.6 3 l3 .4 l-2.2 2.1 l.6 3 l-3 -1.6 l-3 1.6 l.6 -3 l-2.2 -2.1 l3 -.4z" fill="#fef08a" transform="translate(1,-1) scale(.8)"/><path d="M72 49 l1.6 3 l3 .4 l-2.2 2.1 l.6 3 l-3 -1.6 l-3 1.6 l.6 -3 l-2.2 -2.1 l3 -.4z" fill="#fef08a" transform="translate(1,-1) scale(.8)"/></g>
  <g class="eyes e-alert"><ellipse cx="45" cy="52" rx="9" ry="11" fill="#fff"/><ellipse cx="75" cy="52" rx="9" ry="11" fill="#fff"/><circle cx="45" cy="53" r="6" fill="#38bdf8"/><circle cx="75" cy="53" r="6" fill="#38bdf8"/><circle cx="45" cy="53" r="3" fill="#0f172a"/><circle cx="75" cy="53" r="3" fill="#0f172a"/><path d="M33 38 l16 6 M87 38 l-16 6" stroke="#b91c1c" stroke-width="3" stroke-linecap="round"/></g>

  <ellipse cx="60" cy="63" rx="6" ry="4.5" fill="#0f172a"/>
  <ellipse cx="58.4" cy="61.6" rx="2" ry="1" fill="#fff" opacity=".5"/>
  <path d="M60 67 v3" stroke="#0f172a" stroke-width="2" stroke-linecap="round"/>
  <g class="mouth m-happy"><path d="M51 70 q9 9 18 0" fill="none" stroke="#1f2937" stroke-width="2.5" stroke-linecap="round"/><path d="M56 73.6 q4 9 8 0z" fill="#fb7185"/></g>
  <g class="mouth m-studying"><path d="M54 71 q6 3 12 0" fill="none" stroke="#1f2937" stroke-width="2.5" stroke-linecap="round"/></g>
  <g class="mouth m-smart"><path d="M52 70 q5 6 9 2 q5 4 9 -2" fill="none" stroke="#1f2937" stroke-width="2.5" stroke-linecap="round"/></g>
  <g class="mouth m-alert"><path d="M51 70 q9 11 18 0z" fill="#7f1d1d"/><path d="M54 70 l2 4 l2 -4 M62 70 l2 4 l2 -4" fill="#fff" stroke="#fff" stroke-width="1"/></g>

  <g class="bulb"><circle cx="60" cy="-4" r="7" fill="#fef08a"/><path d="M57 3 h6 M58 6 h4" stroke="#f59e0b" stroke-width="2" stroke-linecap="round"/><path d="M60 -16 v4 M48 -10 l3 3 M72 -10 l-3 3" stroke="#fde047" stroke-width="2" stroke-linecap="round"/></g>
  <g class="dots"><circle cx="96" cy="14" r="2.6"/><circle cx="105" cy="8" r="2.6"/><circle cx="114" cy="2" r="2.6"/></g>
</svg>`;

// Identidad de cada mascota: nombre, emoji, frases propias y "voz".
// Los textos de la app y del backend están escritos con voz de gato (Mishi); cada mascota los
// adapta al mostrarlos con `voice` (lista de [patrón, reemplazo]). El gato no cambia nada.
const PETS = {
  cat: {
    svg: CAT_SVG, name: 'Mishi', kind: 'gato', emoji: '🐱',
    hello: '¡Miau! 🐱 Soy Mishi, el gato. ¿Qué olfateo ahora?',
    levelUp: (level) => `¡Subí al nivel ${level}! 🎉 Mis garras están más afiladas.`,
    voice: []
  },
  husky: {
    svg: HUSKY_SVG, name: 'Bruno', kind: 'husky siberiano', emoji: '🐶',
    hello: '¡Guau! 🐶 Soy Bruno, el husky siberiano. ¡Listo para olfatear!',
    levelUp: (level) => `¡Subí al nivel ${level}! 🎉 Mi olfato está más fino.`,
    voice: [
      [/Mishi/g, 'Bruno'],
      [/Miau/g, 'Guau'],
      [/Mrrr/g, 'Sniff sniff'],
      [/Purrr/g, 'Guau guau'],
      [/🐱|😸|🐾/g, '🐶'],
      [/🙀/g, '😱'],
      [/Mis garras están más afiladas/g, 'Mi olfato está más fino'],
      [/Hay que afilar las garras/g, 'Hay que afinar el olfato'],
      [/gatito/g, 'perrito']
    ]
  }
};

function readSavedPet() {
  try {
    const saved = localStorage.getItem(PET_KEY);
    return PETS[saved] ? saved : 'cat';
  } catch { return 'cat'; }   // almacenamiento no disponible
}

export class Mishi {
  constructor(host, { escapeHtml, renderMarkdown, onAnalyzeSql, pet } = {}) {
    this.esc = escapeHtml ?? String;
    this.md = renderMarkdown ?? ((text) => this.esc(text));
    this.onAnalyzeSql = onAnalyzeSql;
    this.timer = null;
    this.level = 1;
    this.pet = PETS[pet] ? pet : readSavedPet();

    this.root = document.createElement('div');
    this.root.className = 'mishi';
    this.root.dataset.state = 'happy';
    this.root.innerHTML = `
      <div class="mishi-drawer" hidden>
        <div class="mishi-drawer-head"><b class="mishi-name">Mishi-SQL</b><button type="button" class="mishi-close" aria-label="Cerrar">×</button></div>
        <textarea class="mishi-sql" rows="5" placeholder="Pega un SP o una consulta para que Mishi la olfatee…" spellcheck="false"></textarea>
        <button type="button" class="btn primary small mishi-go">Olfatear</button>
        <div class="mishi-out"></div>
      </div>
      <div class="mishi-bubble" hidden></div>
      <div class="mishi-row">
        <button type="button" class="mishi-cat" aria-label="Mishi-SQL: abrir panel" title="Mishi-SQL"></button>
        <div class="mishi-hud">
          <span class="mishi-level">Nv 1</span><span class="mishi-bar"><i></i></span><span class="mishi-xp">0 XP</span>
          <span class="mishi-pets" role="group" aria-label="Elegir mascota">
            <button type="button" data-pet="cat" title="Gato" aria-label="Gato">🐱</button>
            <button type="button" data-pet="husky" title="Husky siberiano" aria-label="Husky siberiano">🐶</button>
          </span>
        </div>
      </div>`;
    host.appendChild(this.root);

    this.bubble = this.root.querySelector('.mishi-bubble');
    this.drawer = this.root.querySelector('.mishi-drawer');
    this.out = this.root.querySelector('.mishi-out');
    this.root.querySelector('.mishi-cat').addEventListener('click', () => { this.drawer.hidden = !this.drawer.hidden; });
    this.root.querySelector('.mishi-close').addEventListener('click', () => { this.drawer.hidden = true; });
    this.root.querySelector('.mishi-go').addEventListener('click', () => {
      const sql = this.root.querySelector('.mishi-sql').value.trim();
      if (sql) this.onAnalyzeSql?.(sql);
    });
    this.root.querySelectorAll('.mishi-pets button').forEach((button) =>
      button.addEventListener('click', () => this.setPet(button.dataset.pet, { announce: true })));
    this.bubble.addEventListener('click', () => this.hideBubble());

    this.setPet(this.pet, { save: false });
  }

  /**
   * Cambia entre gato y husky conservando el estado de ánimo, el nivel y la experiencia.
   * Actualiza nombre, textos y etiquetas del widget, marca <html data-pet> (para estilos) y emite
   * el evento `mishi:pet` ({ pet, name, emoji }) para que el resto de la app se adapte.
   */
  setPet(pet, { announce = false, save = true } = {}) {
    const info = PETS[pet];
    if (!info) return;
    const changed = pet !== this.pet;
    this.pet = pet;
    this.root.dataset.pet = pet;
    document.documentElement.dataset.pet = pet;

    const button = this.root.querySelector('.mishi-cat');
    button.innerHTML = info.svg;
    button.setAttribute('aria-label', `${info.name}-SQL: abrir panel`);
    button.title = `${info.name}-SQL`;
    this.root.querySelector('.mishi-name').textContent = `${info.name}-SQL`;
    this.root.querySelector('.mishi-sql').placeholder = `Pega un SP o una consulta para que ${info.name} la olfatee…`;
    this.root.querySelectorAll('.mishi-pets button').forEach((choice) => {
      const option = PETS[choice.dataset.pet];
      choice.title = `${option.name} (${option.kind})`;
      choice.setAttribute('aria-label', `${option.name} (${option.kind})`);
      choice.setAttribute('aria-pressed', String(choice.dataset.pet === pet));
    });

    if (save) {
      try { localStorage.setItem(PET_KEY, pet); } catch { /* almacenamiento no disponible */ }
    }
    document.dispatchEvent(new CustomEvent('mishi:pet', { detail: { pet, name: info.name, emoji: info.emoji } }));
    if (announce && changed) this.say(info.hello);
  }

  get name() { return PETS[this.pet].name; }

  get emoji() { return PETS[this.pet].emoji; }

  /** Adapta el texto a la voz de la mascota activa (el gato no cambia nada). */
  voice(text) {
    return PETS[this.pet].voice.reduce((result, [pattern, replacement]) => result.replace(pattern, replacement), String(text));
  }

  setState(state) { this.root.dataset.state = state; }

  say(text, ms = BUBBLE_MS) {
    this.bubble.textContent = this.voice(text);
    this.bubble.hidden = false;
    clearTimeout(this.timer);
    if (ms) this.timer = setTimeout(() => this.hideBubble(), ms);
  }

  hideBubble() { this.bubble.hidden = true; }

  /** Reacción rápida sin pasar por el servidor (p. ej. pruebas en verde). */
  react(state, text) {
    this.setState(state);
    this.say(text);
  }

  setProgress({ xp, level, levelStartXp, nextLevelXp }) {
    this.level = level;
    const span = Math.max(1, nextLevelXp - levelStartXp);
    this.root.querySelector('.mishi-level').textContent = `Nv ${level}`;
    this.root.querySelector('.mishi-xp').textContent = `${xp} XP`;
    this.root.querySelector('.mishi-bar i').style.width = `${Math.min(100, Math.round(100 * (xp - levelStartXp) / span))}%`;
  }

  /** Aplica la respuesta del backend (contrato: cat_state, bubble_text, gained_xp, learned_pattern, technical_response). */
  apply(reply, { showDetail = true } = {}) {
    const previousLevel = this.level;
    this.setState(reply.cat_state);
    this.say(reply.bubble_text);
    this.setProgress({ xp: reply.total_xp, level: reply.level, levelStartXp: reply.xp_level_start, nextLevelXp: reply.xp_next_level });
    if (reply.gained_xp > 0) this.floatXp(reply.gained_xp);
    if (reply.level > previousLevel && previousLevel > 0 && reply.total_xp > reply.gained_xp) {
      setTimeout(() => this.say(PETS[this.pet].levelUp(reply.level), 6000), 2500);
    }
    if (showDetail) {
      const learned = reply.learned_pattern ? `<div class="mishi-learned">🧠 ${this.esc(this.voice(reply.learned_pattern))}</div>` : '';
      this.out.innerHTML = `${learned}<div class="mishi-tech">${this.md(this.voice(reply.technical_response))}</div>`;
    }
  }

  floatXp(amount) {
    if (REDUCED) return;
    const tag = document.createElement('span');
    tag.className = 'mishi-float';
    tag.textContent = `+${amount} XP`;
    this.root.querySelector('.mishi-row').appendChild(tag);
    setTimeout(() => tag.remove(), 1600);
  }

  openDrawer() { this.drawer.hidden = false; }

  showDetailOnly(html) { this.out.innerHTML = html; }
}

async function post(path, body) {
  const response = await fetch(`${API_BASE}${path}`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
  if (!response.ok) {
    let message = `Error ${response.status}`;
    try { message = (await response.json()).message ?? message; } catch { /* sin cuerpo JSON */ }
    throw new Error(message);
  }
  return response.json();
}

export const analyzeObject = ({ instance, database, schema, name }) => post('/mishi/analyze', { instance, database, schema, name });
export const analyzeSql = (sql) => post('/mishi/analyze', { sql });

export async function fetchProgress() {
  const response = await fetch(`${API_BASE}/mishi/state`);
  if (!response.ok) throw new Error(`Error ${response.status}`);
  const p = await response.json();
  return { xp: p.xp, level: p.level, levelStartXp: p.levelStartXp, nextLevelXp: p.nextLevelXp };
}
