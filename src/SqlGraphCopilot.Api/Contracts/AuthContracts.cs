namespace SqlGraphCopilot.Api.Contracts;

/// <summary>Credenciales enviadas por la pantalla de login.</summary>
public sealed record LoginRequest(string Username, string Password);

/// <summary>Sesión activa (sin datos sensibles).</summary>
public sealed record SessionDto(string Username);
