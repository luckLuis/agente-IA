using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using SqlGraphCopilot.Application.Abstractions;
using SqlGraphCopilot.Application.Migration;
using SqlGraphCopilot.Application.Usage;

namespace SqlGraphCopilot.Api.Controllers;

public sealed record VerifyRequest(IReadOnlyList<VerifyFile>? Files, int Attempt = 0);

[ApiController]
[Route("api/migration")]
public sealed class MigrationController : ControllerBase
{
    private readonly IMigrationVerifier _verifier;
    private readonly IUsageTracker _usage;
    private readonly IWebHostEnvironment _environment;

    public MigrationController(IMigrationVerifier verifier, IUsageTracker usage, IWebHostEnvironment environment)
    {
        _verifier = verifier;
        _usage = usage;
        _environment = environment;
    }

    [HttpPost("verify")]
    public async Task<IActionResult> Verify([FromBody] VerifyRequest request, CancellationToken cancellationToken)
    {
        if (!_environment.IsDevelopment())
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "La validación de código solo está disponible en el entorno de desarrollo." });
        }

        try
        {
            Stopwatch watch = Stopwatch.StartNew();
            VerifyReport report = await _verifier.VerifyAsync(request.Files ?? [], cancellationToken);
            _usage.RecordVerification(new VerificationEntry(DateTimeOffset.UtcNow, request.Attempt, report.Compiled, report.Passed, report.Failed, (int)watch.ElapsedMilliseconds));
            return Ok(report);
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { message = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }
}
