using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;
using SqlGraphCopilot.Core.Migration;
using SqlGraphCopilot.Core.Usage;

namespace SqlGraphCopilot.Api.Endpoints;

/// <summary>Attempt: 0 = primera validación; 1, 2… = tras una corrección automática (para medir la calidad al primer intento).</summary>
public sealed record VerifyRequest(IReadOnlyList<VerifyFile>? Files, int Attempt = 0);

public static class MigrationEndpoints
{
    /// <summary>POST /api/migration/verify  { files: [{ name, content }] }  (se llama sobre el grupo "/api").</summary>
    public static RouteGroupBuilder MapMigrationEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/migration/verify", async (
            VerifyRequest request,
            MigrationVerifier verifier,
            UsageTracker usage,
            IWebHostEnvironment environment,
            CancellationToken cancellationToken) =>
        {
            // Compilar y ejecutar código generado solo se permite en desarrollo.
            if (!environment.IsDevelopment())
            {
                return Results.Json(new { message = "La validación de código solo está disponible en el entorno de desarrollo." }, statusCode: StatusCodes.Status403Forbidden);
            }

            try
            {
                Stopwatch watch = Stopwatch.StartNew();
                VerifyReport report = await verifier.VerifyAsync(request.Files ?? [], cancellationToken);
                usage.RecordVerification(new VerificationEntry(DateTimeOffset.UtcNow, request.Attempt, report.Compiled, report.Passed, report.Failed, (int)watch.ElapsedMilliseconds));
                return Results.Ok(report);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new { message = ex.Message }, statusCode: StatusCodes.Status403Forbidden);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });
        return api;
    }
}
