using SqlGraphCopilot.Api.Endpoints;
using SqlGraphCopilot.Api.Infrastructure;
using SqlGraphCopilot.Core.Agent;
using SqlGraphCopilot.Core.DependencyInjection;
using SqlGraphCopilot.Core.Flow;
using SqlGraphCopilot.Core.Migration;
using SqlGraphCopilot.Core.Usage;
using System.Text.Json.Serialization;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Motor completo (metadata real, índice, carga on-demand, análisis y agente). Sin mocks.
builder.Services.AddSqlGraphCopilot(builder.Configuration);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<DomainExceptionHandler>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Datos de entrada de un SP (análisis de flujo)
builder.Services.AddSingleton<ProcedureFlowService>();

// Validación del código C# migrado (solo desarrollo)
builder.Services.Configure<MigrationVerifierOptions>(builder.Configuration.GetSection("MigrationVerifier"));
builder.Services.AddSingleton<MigrationVerifier>();

// Estadísticas de uso (tokens, tiempo, ahorro, calidad)
builder.Services.Configure<UsageOptions>(builder.Configuration.GetSection("Usage"));
builder.Services.AddSingleton<UsageTracker>();

// Caché de respuestas del LLM (mismo prompt = cero tokens)
builder.Services.Configure<LlmResponseCacheOptions>(builder.Configuration.GetSection("LlmCache"));
builder.Services.AddSingleton<LlmResponseCache>();

WebApplication app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseSecurityHeaders();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
else
{
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseDefaultFiles();   // wwwroot/index.html (el HTML ya no vive en un string de C#)
app.UseStaticFiles();

RouteGroupBuilder api = app.MapGroup("/api");
api.AddEndpointFilter<UsageEndpointFilter>();   // mide cada respuesta del copiloto
api.MapCatalogEndpoints();
api.MapGraphEndpoints();
api.MapAnalysisEndpoints();
api.MapCopilotEndpoints();
api.MapFlowEndpoints();
api.MapMigrationEndpoints();
api.MapUsageEndpoints();

app.MapGet("/health", () => Results.Ok(new { status = "ok" })).ExcludeFromDescription();

app.Run();

public partial class Program;
