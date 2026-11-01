using System.Collections.Concurrent;
using Microsoft.Data.SqlClient;
using SqlGraphCopilot.Core.Metadata;

namespace SqlGraphCopilot.Api.Endpoints;

/// <summary>
/// Login de SQL Authentication SOLO LECTURA, con la misma línea gráfica de la aplicación.
/// Todo vive en esta clase (HTML + CSS + JS + validación). Uso en Program.cs:  app.MapLoginPage();
///
/// Rutas:
///   GET  /login                  página
///   GET  /login/login.css        estilos   (la CSP prohíbe scripts inline, por eso van aparte)
///   GET  /login/login.js         lógica del formulario
///   GET  /api/auth/instances     instancias registradas (solo nombres)
///   POST /api/auth/login-check   autentica y verifica que el login NO pueda escribir ni administrar
///
/// Regla: si el login tiene cualquier permiso de escritura o administración, se rechaza (fail-closed).
/// No guarda ni registra la contraseña.
/// </summary>
public static class LoginPage
{
    private const int MaxFailures = 5;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(5);
    private static readonly ConcurrentDictionary<string, FailureCounter> Failures = new();

    public sealed record LoginRequest(string? Instance, string? User, string? Password);

    public sealed record PermissionResult(string Code, string Label, bool Granted);

    public sealed record LoginResponse(bool Authenticated, bool ReadOnly, string Message, IReadOnlyList<PermissionResult> Permissions);

    private sealed class FailureCounter
    {
        public int Count;
        public DateTime Since = DateTime.UtcNow;
    }

    public static IEndpointRouteBuilder MapLoginPage(this IEndpointRouteBuilder app)
    {
        app.MapGet("/login", () => Static(Html, "text/html; charset=utf-8")).ExcludeFromDescription();
        app.MapGet("/login/login.css", () => Static(Css, "text/css; charset=utf-8")).ExcludeFromDescription();
        app.MapGet("/login/login.js", () => Static(Js, "text/javascript; charset=utf-8")).ExcludeFromDescription();

        app.MapGet("/api/auth/instances", (IInstanceRegistry registry) =>
        {
            List<object> items = new();
            foreach (SqlInstanceOptions instance in registry.Instances)
            {
                items.Add(new { name = instance.Name, environment = instance.Environment });
            }
            return Results.Ok(items);
        });

        app.MapPost("/api/auth/login-check", async (
            LoginRequest request,
            IInstanceRegistry registry,
            HttpContext http,
            CancellationToken ct) =>
        {
            string client = http.Connection.RemoteIpAddress?.ToString() ?? "desconocido";

            if (IsBlocked(client))
            {
                return Results.Json(
                    new LoginResponse(false, false, "Demasiados intentos. Espere unos minutos.", Array.Empty<PermissionResult>()),
                    statusCode: StatusCodes.Status429TooManyRequests);
            }

            if (string.IsNullOrWhiteSpace(request.Instance) ||
                string.IsNullOrWhiteSpace(request.User) ||
                string.IsNullOrEmpty(request.Password) ||
                !registry.TryGet(request.Instance, out SqlInstanceOptions? instance) ||
                instance is null)
            {
                return Results.BadRequest(
                    new LoginResponse(false, false, "Complete instancia, usuario y contraseña.", Array.Empty<PermissionResult>()));
            }

            SqlConnectionStringBuilder builder = new(instance.ConnectionString)
            {
                IntegratedSecurity = false,
                UserID = request.User.Trim(),
                Password = request.Password,
                ApplicationName = "SqlGraphCopilot.Login",
                ApplicationIntent = ApplicationIntent.ReadOnly,
                ConnectTimeout = 10,
                Pooling = false
            };

            try
            {
                await using SqlConnection connection = new(builder.ConnectionString);
                await connection.OpenAsync(ct);

                List<PermissionResult> permissions = await ReadPermissionsAsync(connection, ct);
                bool readOnly = !permissions.Any(p => p.Granted);

                if (!readOnly)
                {
                    RegisterFailure(client);
                    return Results.Json(
                        new LoginResponse(true, false,
                            "Login rechazado: tiene permisos de escritura o administración. Use un login de solo lectura.",
                            permissions),
                        statusCode: StatusCodes.Status403Forbidden);
                }

                ClearFailures(client);
                return Results.Ok(new LoginResponse(true, true, "Login válido: solo lectura.", permissions));
            }
            catch (SqlException)
            {
                // Mensaje genérico: no revela si falló el usuario, la clave o la red.
                RegisterFailure(client);
                return Results.Json(
                    new LoginResponse(false, false, "No se pudo autenticar con esas credenciales.", Array.Empty<PermissionResult>()),
                    statusCode: StatusCodes.Status401Unauthorized);
            }
        }).ExcludeFromDescription();

        return app;
    }

    // ------------------------------------------------------------------ preflight

    private static readonly (string Code, string Label, string Sql)[] Checks =
    {
        ("SYSADMIN",     "Rol sysadmin",            "IS_SRVROLEMEMBER('sysadmin')"),
        ("SECURITYADMIN","Rol securityadmin",       "IS_SRVROLEMEMBER('securityadmin')"),
        ("CONTROLSRV",   "CONTROL SERVER",          "HAS_PERMS_BY_NAME(NULL, NULL, 'CONTROL SERVER')"),
        ("ALTERLOGIN",   "ALTER ANY LOGIN",         "HAS_PERMS_BY_NAME(NULL, NULL, 'ALTER ANY LOGIN')"),
        ("DBOWNER",      "Rol db_owner",            "IS_MEMBER('db_owner')"),
        ("DATAWRITER",   "Rol db_datawriter",       "IS_MEMBER('db_datawriter')"),
        ("DDLADMIN",     "Rol db_ddladmin",         "IS_MEMBER('db_ddladmin')"),
        ("INSERT",       "INSERT en la base",       "HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'INSERT')"),
        ("UPDATE",       "UPDATE en la base",       "HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'UPDATE')"),
        ("DELETE",       "DELETE en la base",       "HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'DELETE')"),
        ("ALTER",        "ALTER en la base",        "HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'ALTER')"),
        ("EXECUTE",      "EXECUTE en la base",      "HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'EXECUTE')")
    };

    private static async Task<List<PermissionResult>> ReadPermissionsAsync(SqlConnection connection, CancellationToken ct)
    {
        // Una sola consulta fija (sin entrada del usuario): SELECT expr1 AS c0, expr2 AS c1, ...
        List<string> columns = new();
        for (int i = 0; i < Checks.Length; i++)
        {
            columns.Add($"CAST(ISNULL({Checks[i].Sql}, 0) AS int) AS c{i}");
        }

        await using SqlCommand command = new("SELECT " + string.Join(", ", columns) + ";", connection);
        await using SqlDataReader reader = await command.ExecuteReaderAsync(ct);

        List<PermissionResult> results = new();
        if (await reader.ReadAsync(ct))
        {
            for (int i = 0; i < Checks.Length; i++)
            {
                bool granted = reader.GetInt32(i) == 1;
                results.Add(new PermissionResult(Checks[i].Code, Checks[i].Label, granted));
            }
        }
        return results;
    }

    // ------------------------------------------------------------------ límite de intentos

    private static bool IsBlocked(string client)
    {
        if (!Failures.TryGetValue(client, out FailureCounter? counter))
        {
            return false;
        }
        if (DateTime.UtcNow - counter.Since > Window)
        {
            Failures.TryRemove(client, out _);
            return false;
        }
        return counter.Count >= MaxFailures;
    }

    private static void RegisterFailure(string client)
    {
        FailureCounter counter = Failures.GetOrAdd(client, _ => new FailureCounter());
        if (DateTime.UtcNow - counter.Since > Window)
        {
            counter.Count = 0;
            counter.Since = DateTime.UtcNow;
        }
        Interlocked.Increment(ref counter.Count);
    }

    private static void ClearFailures(string client) => Failures.TryRemove(client, out _);

    private static IResult Static(string content, string contentType) =>
        Results.Content(content, contentType);

    // ------------------------------------------------------------------ HTML
    private const string Html = """
<!doctype html>
<html lang="es">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Acceso · SQL-Graph Copilot</title>
  <link rel="stylesheet" href="/login/login.css">
</head>
<body>
  <main class="shell">
    <section class="card" aria-labelledby="titulo">
      <header class="brand">
        <svg class="logo" viewBox="0 0 32 32" aria-hidden="true">
          <line x1="8" y1="17" x2="17" y2="9" /><line x1="8" y1="17" x2="19" y2="24" /><line x1="17" y1="9" x2="19" y2="24" />
          <circle cx="8" cy="17" r="3.2" class="n1" /><circle cx="17" cy="9" r="3.2" class="n2" /><circle cx="19" cy="24" r="3.2" class="n3" />
        </svg>
        <div>
          <h1 id="titulo">SQL-Graph Copilot</h1>
          <p>Red de dependencias SQL Server</p>
        </div>
      </header>

      <form id="form" novalidate autocomplete="off">
        <label class="field">
          <span>INSTANCIA</span>
          <select id="instance" required></select>
        </label>

        <label class="field">
          <span>USUARIO SQL</span>
          <input id="user" type="text" autocomplete="off" spellcheck="false" placeholder="sqlgraph_reader" required>
        </label>

        <label class="field">
          <span>CONTRASEÑA</span>
          <div class="pwd">
            <input id="password" type="password" autocomplete="new-password" placeholder="••••••••" required>
            <button type="button" id="toggle" class="ghost" aria-label="Mostrar u ocultar contraseña">Ver</button>
          </div>
        </label>

        <button id="submit" class="primary" type="submit">Validar acceso</button>
      </form>

      <ol id="steps" class="steps" hidden>
        <li data-step="auth"><i></i>Autenticando con SQL Server</li>
        <li data-step="perm"><i></i>Verificando que el login sea de solo lectura</li>
        <li data-step="done"><i></i>Resultado</li>
      </ol>

      <div id="banner" class="banner" role="status" hidden></div>

      <section id="perms" class="perms" hidden>
        <h2>PERMISOS DETECTADOS</h2>
        <ul id="permList"></ul>
      </section>

      <footer class="note">
        <span class="dot"></span>
        <span>Solo se leen vistas de catálogo <code>sys.*</code>. La contraseña no se guarda.</span>
      </footer>
    </section>
  </main>
  <script src="/login/login.js"></script>
</body>
</html>
""";

    // ------------------------------------------------------------------ CSS
    private const string Css = """
:root{
  --bg:#070b16; --surface:#0f172a; --surface-2:#131c33; --line:#1e293b; --line-2:#2a3754;
  --text:#e2e8f0; --text-2:#94a3b8; --text-3:#64748b;
  --accent:#22d3ee; --accent-2:#0e7490; --danger:#f43f5e; --ok:#34d399; --warn:#f59e0b;
  --k-table:#38bdf8; --k-proc:#a78bfa; --k-func:#f472b6;
  --radius:12px;
  --font:"Inter","Segoe UI",system-ui,-apple-system,sans-serif;
  --mono:"Cascadia Code","JetBrains Mono",Consolas,ui-monospace,monospace;
  color-scheme:dark;
}
*{box-sizing:border-box}
html,body{height:100%}
body{
  margin:0; font:14px/1.45 var(--font); color:var(--text);
  background:
    radial-gradient(1200px 600px at 70% -10%, rgba(34,211,238,.10), transparent 60%),
    radial-gradient(900px 500px at -10% 110%, rgba(167,139,250,.09), transparent 60%),
    var(--bg);
}
[hidden]{display:none !important}
code{font-family:var(--mono);font-size:.9em;background:rgba(148,163,184,.12);padding:.05em .35em;border-radius:5px;color:#f1f5f9}

.shell{min-height:100%;display:grid;place-items:center;padding:24px 16px}
.card{
  width:100%; max-width:400px; background:linear-gradient(180deg,var(--surface-2),var(--surface));
  border:1px solid var(--line-2); border-radius:16px; padding:28px 26px 20px;
  box-shadow:0 24px 60px rgba(0,0,0,.45), 0 0 0 1px rgba(34,211,238,.04);
}

.brand{display:flex;align-items:center;gap:12px;margin-bottom:24px}
.brand h1{margin:0;font-size:16px;font-weight:600;letter-spacing:-.01em}
.brand p{margin:0;font-size:12px;color:var(--text-3)}
.logo{width:38px;height:38px;flex:none}
.logo line{stroke:var(--line-2);stroke-width:1.4}
.logo .n1{fill:var(--k-table)} .logo .n2{fill:var(--k-proc)} .logo .n3{fill:var(--k-func)}

.field{display:block;margin-bottom:14px}
.field>span{display:block;font-size:10.5px;letter-spacing:.08em;color:var(--text-3);margin-bottom:5px}
input,select{
  width:100%; height:40px; padding:0 12px; color:var(--text); font:inherit;
  background:var(--bg); border:1px solid var(--line-2); border-radius:8px; outline:none;
  transition:border-color .15s, box-shadow .15s;
}
input::placeholder{color:var(--text-3)}
input:focus,select:focus{border-color:var(--accent);box-shadow:0 0 0 3px rgba(34,211,238,.14)}
.pwd{position:relative}
.pwd input{padding-right:58px}
.ghost{
  position:absolute;right:6px;top:6px;height:28px;padding:0 10px;border:0;border-radius:6px;
  background:transparent;color:var(--text-2);font:inherit;font-size:12px;cursor:pointer;
}
.ghost:hover{color:var(--accent)}

.primary{
  width:100%;height:42px;margin-top:6px;border:0;border-radius:8px;cursor:pointer;
  font:inherit;font-weight:600;color:#04222a;background:var(--accent);
  transition:filter .15s, transform .05s;
}
.primary:hover{filter:brightness(1.1)}
.primary:active{transform:translateY(1px)}
.primary:disabled{opacity:.55;cursor:progress}

.steps{list-style:none;margin:18px 0 0;padding:0;display:grid;gap:8px}
.steps li{display:flex;align-items:center;gap:10px;color:var(--text-3);font-size:13px}
.steps i{width:12px;height:12px;border-radius:50%;border:2px solid var(--line-2);flex:none}
.steps li.run{color:var(--text)} .steps li.run i{border-color:var(--accent);border-top-color:transparent;animation:spin .8s linear infinite}
.steps li.ok{color:var(--text-2)} .steps li.ok i{background:var(--ok);border-color:var(--ok)}
.steps li.bad{color:var(--text)} .steps li.bad i{background:var(--danger);border-color:var(--danger)}
@keyframes spin{to{transform:rotate(360deg)}}

.banner{margin-top:16px;padding:11px 13px;border-radius:8px;font-size:13px;border:1px solid}
.banner.ok{background:rgba(52,211,153,.08);border-color:rgba(52,211,153,.4);color:#a7f3d0}
.banner.bad{background:rgba(244,63,94,.08);border-color:rgba(244,63,94,.45);color:#fecdd3}

.perms{margin-top:16px}
.perms h2{margin:0 0 8px;font-size:10.5px;letter-spacing:.08em;color:var(--text-3);font-weight:600}
.perms ul{list-style:none;margin:0;padding:0;display:grid;grid-template-columns:1fr 1fr;gap:6px}
.perms li{
  display:flex;align-items:center;gap:7px;padding:6px 9px;font-size:12px;color:var(--text-2);
  background:var(--bg);border:1px solid var(--line);border-radius:7px;
}
.perms li::before{content:"";width:8px;height:8px;border-radius:50%;background:var(--ok);flex:none}
.perms li.granted{color:#fecdd3;border-color:rgba(244,63,94,.45)}
.perms li.granted::before{background:var(--danger)}

.note{display:flex;align-items:center;gap:8px;margin-top:20px;padding-top:14px;border-top:1px solid var(--line);color:var(--text-3);font-size:11.5px}
.dot{width:6px;height:6px;border-radius:50%;background:var(--accent);flex:none;box-shadow:0 0 8px var(--accent)}

@media (max-width:420px){.perms ul{grid-template-columns:1fr}}
""";

    // ------------------------------------------------------------------ JS
    private const string Js = """
(function () {
  'use strict';

  var form = document.getElementById('form');
  var instance = document.getElementById('instance');
  var user = document.getElementById('user');
  var password = document.getElementById('password');
  var toggle = document.getElementById('toggle');
  var submit = document.getElementById('submit');
  var steps = document.getElementById('steps');
  var banner = document.getElementById('banner');
  var perms = document.getElementById('perms');
  var permList = document.getElementById('permList');

  function setStep(name, state) {
    var li = steps.querySelector('[data-step="' + name + '"]');
    if (li) { li.className = state; }
  }

  function resetUi() {
    steps.hidden = false;
    banner.hidden = true;
    perms.hidden = true;
    permList.textContent = '';
    setStep('auth', ''); setStep('perm', ''); setStep('done', '');
  }

  function showBanner(kind, text) {
    banner.className = 'banner ' + kind;
    banner.textContent = text;   // textContent: nunca innerHTML con datos del servidor
    banner.hidden = false;
  }

  function renderPermissions(list) {
    permList.textContent = '';
    for (var i = 0; i < list.length; i++) {
      var li = document.createElement('li');
      li.textContent = list[i].label;
      if (list[i].granted) { li.className = 'granted'; }
      permList.appendChild(li);
    }
    perms.hidden = list.length === 0;
  }

  function loadInstances() {
    fetch('/api/auth/instances')
      .then(function (r) { return r.json(); })
      .then(function (items) {
        instance.textContent = '';
        for (var i = 0; i < items.length; i++) {
          var opt = document.createElement('option');
          opt.value = items[i].name;
          opt.textContent = items[i].name + ' · ' + items[i].environment;
          instance.appendChild(opt);
        }
      })
      .catch(function () { showBanner('bad', 'No se pudo cargar la lista de instancias.'); });
  }

  toggle.addEventListener('click', function () {
    var hidden = password.type === 'password';
    password.type = hidden ? 'text' : 'password';
    toggle.textContent = hidden ? 'Ocultar' : 'Ver';
  });

  form.addEventListener('submit', function (ev) {
    ev.preventDefault();
    if (!instance.value || !user.value.trim() || !password.value) {
      resetUi();
      showBanner('bad', 'Complete instancia, usuario y contraseña.');
      return;
    }

    resetUi();
    submit.disabled = true;
    setStep('auth', 'run');

    fetch('/api/auth/login-check', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ instance: instance.value, user: user.value.trim(), password: password.value })
    })
      .then(function (r) { return r.json().then(function (data) { return { status: r.status, data: data }; }); })
      .then(function (res) {
        var data = res.data;
        setStep('auth', data.authenticated ? 'ok' : 'bad');
        if (data.authenticated) {
          setStep('perm', data.readOnly ? 'ok' : 'bad');
          renderPermissions(data.permissions || []);
        }
        setStep('done', data.authenticated && data.readOnly ? 'ok' : 'bad');
        showBanner(data.authenticated && data.readOnly ? 'ok' : 'bad', data.message || 'Sin respuesta.');
      })
      .catch(function () {
        setStep('auth', 'bad'); setStep('done', 'bad');
        showBanner('bad', 'No se pudo contactar con el servidor.');
      })
      .then(function () {
        password.value = '';   // la clave no queda en el DOM
        submit.disabled = false;
      });
  });

  loadInstances();
})();
""";
}
