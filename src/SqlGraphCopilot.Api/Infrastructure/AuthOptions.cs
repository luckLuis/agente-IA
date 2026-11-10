namespace SqlGraphCopilot.Api.Infrastructure;

/// <summary>
/// Usuario quemado para validación (sección "Auth" de appsettings).
/// Solo para DEV: no es un mecanismo de identidad real.
/// </summary>
public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    public string Username { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public int SessionHours { get; set; } = 8;
}
