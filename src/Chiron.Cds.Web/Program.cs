using Chiron.Cds.Engine.Rules.Renal;
using ReasoningEngine = Chiron.Cds.Engine.Engine;
using Chiron.Cds.Engine;
using Chiron.Cds.Web.CdsHooks;
using Chiron.Cds.Web.Configuration;
using Chiron.Cds.Web.FhirClient;
using Chiron.Cds.Web.Mappers;
using Chiron.Cds.Web.Panel;
using Chiron.Cds.Web.Persistence;
using Chiron.Cds.Web.SmartLaunch;
using Chiron.Cds.Web.Tenancy;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddOpenApi();

builder.Services
    .AddOptions<ChironOptions>()
    .Bind(builder.Configuration.GetSection(ChironOptions.SectionName))
    .Validate(o => !string.IsNullOrWhiteSpace(o.DefaultTenant) && o.Tenants.Count > 0,
        "Chiron configuration requires DefaultTenant and at least one tenant.")
    .ValidateOnStart();

builder.Services.AddSingleton<TenantRegistry>();
builder.Services.AddSingleton<ITokenStore, InMemoryTokenStore>();

// OverrideLog: durable SQLite by default (file lives in
// ContentRootPath/chiron-override-log.db). In-memory implementation
// stays available for integration tests via WebApplicationFactory
// service overrides.
var overrideLogPath = Path.Combine(builder.Environment.ContentRootPath, "chiron-override-log.db");
builder.Services.AddSingleton<IOverrideLog>(_ =>
    new SqliteOverrideLog($"Data Source={overrideLogPath}"));
builder.Services.AddSingleton<ReasoningEngine>(_ =>
    new ReasoningEngine().RegisterPack(typeof(MetforminRenalRule).Assembly));

builder.Services.AddHttpClient<SmartConfigurationClient>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddHttpClient<AuthorizationService>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(15);
});
builder.Services.AddHttpClient<IdTokenValidator>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(10);
});

builder.Services.AddSingleton<FhirToFactMapper>();
builder.Services.AddSingleton<AlertToCdsCardMapper>();
builder.Services.AddSingleton<PatientChartFetcher>();
builder.Services.AddSingleton<DiagnosticReportWriter>();
builder.Services.AddScoped<PatientViewService>();

builder.Services
    .AddOptions<PanelOptions>()
    .Bind(builder.Configuration.GetSection(PanelOptions.SectionName));
builder.Services.AddScoped<PanelService>();
builder.Services.AddScoped<PatientSearchService>();
builder.Services.AddScoped<ResultReviewService>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.MapControllers();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.Run();

/// <summary>Public type so <c>WebApplicationFactory&lt;Program&gt;</c> works in integration tests.</summary>
public partial class Program;
