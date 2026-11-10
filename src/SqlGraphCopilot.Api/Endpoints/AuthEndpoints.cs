using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;
using SqlGraphCopilot.Api.Contracts;
using SqlGraphCopilot.Api.Infrastructure;

namespace SqlGraphCopilot.Api.Endpoints;

/// <summary>Paso 0 del flujo: login de validación con usuario quemado (cookie de sesión).</summary>
public static class AuthEndpoints
{
    public const string LoginRateLimitPolicy = "login";

    public static RouteGroupBuilder MapAuthEndpoints(this RouteGroupBuilder api)
    {
        RouteGroupBuilder group = api.MapGroup("/auth").WithTags("Autenticación");

        group.MapPost("/login", async (LoginRequest request, HttpContext http, IOptions<AuthOptions> options) =>
        {
            AuthOptions auth = options.Value;
            if (!IsValid(request, auth))
            {
                // Mismo mensaje para usuario y contraseña: no revela cuál falló.
                return Results.Problem(
                    statusCode: StatusCodes.Status401Unauthorized,
                    title: "Usuario o contraseña incorrectos.");
            }

            List<Claim> claims = new()
            {
                new Claim(ClaimTypes.Name, auth.Username)
            };
            ClaimsIdentity identity = new(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            ClaimsPrincipal principal = new(identity);

            await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
            return Results.Ok(new SessionDto(auth.Username));
        })
            .AllowAnonymous()
            .RequireRateLimiting(LoginRateLimitPolicy)
            .WithName("Login")
            .WithSummary("Valida el usuario quemado y emite la cookie de sesión.");

        group.MapPost("/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.NoContent();
        })
            .AllowAnonymous()
            .WithName("Logout")
            .WithSummary("Elimina la cookie de sesión.");

        group.MapGet("/me", (ClaimsPrincipal user) => new SessionDto(user.Identity?.Name ?? string.Empty))
            .WithName("CurrentSession")
            .WithSummary("Devuelve la sesión actual (401 si no hay sesión).");

        return api;
    }

    private static bool IsValid(LoginRequest request, AuthOptions auth)
    {
        if (string.IsNullOrEmpty(auth.Username) || string.IsNullOrEmpty(auth.Password))
        {
            return false;
        }

        bool userOk = FixedTimeEquals(request.Username ?? string.Empty, auth.Username);
        bool passwordOk = FixedTimeEquals(request.Password ?? string.Empty, auth.Password);

        // '&' (no '&&') para comparar siempre ambos campos y no filtrar tiempos.
        return userOk & passwordOk;
    }

    private static bool FixedTimeEquals(string value, string expected)
    {
        // Se hashea primero para que ambos lados tengan la misma longitud.
        byte[] left = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        byte[] right = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        return CryptographicOperations.FixedTimeEquals(left, right);
    }
}
