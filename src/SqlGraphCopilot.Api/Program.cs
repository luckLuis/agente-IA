using System.Text.Json.Serialization;
using SqlGraphCopilot.Api.Http;
using SqlGraphCopilot.Application.DependencyInjection;
using SqlGraphCopilot.Infrastructure.DependencyInjection;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Composition root: Application (casos de uso, agente, flujo, caché) + Infrastructure (SQL Server, ScriptDom, LLM, uso, verificador).
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddLoginValidation(builder.Configuration);                 // [LOGIN 1/4] NUEVA línea

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

builder.Services
    .AddControllers(options =>
    {
        options.Filters.Add<UsageActionFilter>();
        options.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true;
    })
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
        options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    });

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<DomainExceptionHandler>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Flujo de SP, verificador de migración, estadísticas de uso y caché del LLM: registrados en AddApplication()/AddInfrastructure().

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

app.MapControllers();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }))
    .AllowAnonymous()                                                       // [LOGIN 4/4] NUEVA línea
    .ExcludeFromDescription();

app.Run();

public partial class Program;
