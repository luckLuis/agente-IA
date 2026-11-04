using System.Text.Json.Serialization;
using SqlGraphCopilot.Api.Endpoints;
using SqlGraphCopilot.Api.Infrastructure;
using SqlGraphCopilot.Core.DependencyInjection;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Motor completo (metadata real, índice, carga on-demand, análisis y agente). Sin mocks.
builder.Services.AddSqlGraphCopilot(builder.Configuration);
builder.Services.AddLoginValidation(builder.Configuration);                 // [LOGIN 1/4] NUEVA línea

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<DomainExceptionHandler>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

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
app.UseLoginValidation();                                                   // [LOGIN 2/4] NUEVA línea (ANTES de wwwroot)
app.UseDefaultFiles();   // wwwroot/index.html (el HTML ya no vive en un string de C#)
app.UseStaticFiles();

RouteGroupBuilder api = app.MapGroup("/api");
api.MapAuthEndpoints();                                                     // [LOGIN 3/4] NUEVA línea
api.MapCatalogEndpoints();
api.MapGraphEndpoints();
api.MapAnalysisEndpoints();
api.MapCopilotEndpoints();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }))
    .AllowAnonymous()                                                       // [LOGIN 4/4] NUEVA línea
    .ExcludeFromDescription();

app.Run();

public partial class Program;
