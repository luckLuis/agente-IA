using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.FileProviders;
using SqlGraphCopilot.Api.Endpoints;

namespace SqlGraphCopilot.Api.Infrastructure;

/// <summary>
/// Login de validación AISLADO. Todo lo del login vive aquí para que
/// Program.cs solo tenga 2 llamadas: AddLoginValidation() y UseLoginValidation().
/// Para quitar el login: borrar esas 2 líneas (+ MapAuthEndpoints y AllowAnonymous).
/// </summary>
public static class LoginValidationExtensions
{
    public const string LoginFolder = "login";      // carpeta física: SqlGraphCopilot.Api/wwwroot/login
    public const string LoginPath = "/login";       // URL pública: https://localhost:xxxx/login/

    // ---------------- Servicios (builder.Services) ----------------
    public static IServiceCollection AddLoginValidation(this IServiceCollection services, IConfiguration configuration)
    {
        // 1) Usuario quemado desde appsettings / user-secrets (sección "Auth").
        services.AddOptions<AuthOptions>()
            .Bind(configuration.GetSection(AuthOptions.SectionName))
            .Validate(
                o => !string.IsNullOrWhiteSpace(o.Username) && !string.IsNullOrWhiteSpace(o.Password),
                "Configure Auth:Username y Auth:Password (user-secrets o appsettings.Development.json).")
            .ValidateOnStart();

        AuthOptions settings = configuration.GetSection(AuthOptions.SectionName).Get<AuthOptions>() ?? new AuthOptions();

        // 2) Sesión por cookie (mismo origen que wwwroot, no hace falta JWT).
        services
            .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.Cookie.Name = "sgc.session";
                options.Cookie.HttpOnly = true;                              // JS no puede leerla
                options.Cookie.SameSite = SameSiteMode.Strict;               // mitiga CSRF
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                options.ExpireTimeSpan = TimeSpan.FromHours(settings.SessionHours);
                options.SlidingExpiration = true;

                // /api/* sin sesión → 401 (ProblemDetails). Pantalla sin sesión → redirige al login.
                options.Events.OnRedirectToLogin = context =>
                {
                    if (context.Request.Path.StartsWithSegments("/api"))
                    {
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    }
                    else
                    {
                        context.Response.Redirect(LoginPath + "/");
                    }
                    return Task.CompletedTask;
                };
                options.Events.OnRedirectToAccessDenied = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };
            });

        // 3) Todo exige sesión, salvo lo marcado con AllowAnonymous (login, logout, health).
        services.AddAuthorization(options =>
        {
            options.FallbackPolicy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build();
        });

        // 4) Máximo 5 intentos de login por minuto por IP.
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(AuthEndpoints.LoginRateLimitPolicy, httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(
                    httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 5,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0
                    }));
        });

        return services;
    }

    // ---------------- Pipeline (app) ----------------
    // IMPORTANTE: llamar ANTES de app.UseDefaultFiles()/UseStaticFiles() para que wwwroot quede protegido.
    public static WebApplication UseLoginValidation(this WebApplication app)
    {
        // a) Pantalla de login (wwwroot/login): PÚBLICA, se sirve antes de exigir sesión.
        //    El resto de wwwroot (dashboard) queda protegido más abajo.
        FileServerOptions loginFiles = new()
        {
            FileProvider = new PhysicalFileProvider(Path.Combine(app.Environment.WebRootPath, LoginFolder)),
            RequestPath = LoginPath,
            EnableDefaultFiles = true
        };
        loginFiles.DefaultFilesOptions.DefaultFileNames.Clear();
        loginFiles.DefaultFilesOptions.DefaultFileNames.Add("login.html");
        app.UseFileServer(loginFiles);

        // b) Desde aquí, todo lo que sigue exige sesión (wwwroot y /api).
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();

        return app;
    }
}
