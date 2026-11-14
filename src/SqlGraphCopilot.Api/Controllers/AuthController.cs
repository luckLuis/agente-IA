using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using SqlGraphCopilot.Api.Contracts;
using SqlGraphCopilot.Api.Http;

namespace SqlGraphCopilot.Api.Controllers;

[ApiController]
[Route("api/auth")]
[Tags("Autenticación")]
public sealed class AuthController : ControllerBase
{
    public const string LoginRateLimitPolicy = "login";

    private readonly IOptions<AuthOptions> _options;

    public AuthController(IOptions<AuthOptions> options)
    {
        _options = options;
    }

    [HttpPost("login", Name = "Login")]
    [AllowAnonymous]
    [EnableRateLimiting(LoginRateLimitPolicy)]
    [EndpointSummary("Valida el usuario quemado y emite la cookie de sesión.")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        AuthOptions auth = _options.Value;
        if (!IsValid(request, auth))
        {
            return Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "Usuario o contraseña incorrectos.");
        }

        List<Claim> claims = new()
        {
            new Claim(ClaimTypes.Name, auth.Username)
        };
        ClaimsIdentity identity = new(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        ClaimsPrincipal principal = new(identity);

        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
        return Ok(new SessionDto(auth.Username));
    }

    [HttpPost("logout", Name = "Logout")]
    [AllowAnonymous]
    [EndpointSummary("Elimina la cookie de sesión.")]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return NoContent();
    }

    [HttpGet("me", Name = "CurrentSession")]
    [EndpointSummary("Devuelve la sesión actual (401 si no hay sesión).")]
    public IActionResult CurrentSession()
        => Ok(new SessionDto(User.Identity?.Name ?? string.Empty));

    private static bool IsValid(LoginRequest request, AuthOptions auth)
    {
        if (string.IsNullOrEmpty(auth.Username) || string.IsNullOrEmpty(auth.Password))
        {
            return false;
        }

        bool userOk = FixedTimeEquals(request.Username ?? string.Empty, auth.Username);
        bool passwordOk = FixedTimeEquals(request.Password ?? string.Empty, auth.Password);

        return userOk & passwordOk;
    }

    private static bool FixedTimeEquals(string value, string expected)
    {
        byte[] left = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        byte[] right = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        return CryptographicOperations.FixedTimeEquals(left, right);
    }
}
