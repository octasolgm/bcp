using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Infrastructure;
using Reguliq.Api.Services;
using Reguliq.Api.Workers;

// Load repo root .env when present
var repoEnv = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "..", "..", ".env"));
if (File.Exists(repoEnv))
{
    foreach (var line in File.ReadAllLines(repoEnv))
    {
        var t = line.Trim();
        if (t.StartsWith('#') || !t.Contains('=')) continue;
        var idx = t.IndexOf('=');
        var key = t[..idx].Trim();
        var val = t[(idx + 1)..].Trim().Trim('"');
        Environment.SetEnvironmentVariable(key, val);
    }
}

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddEnvironmentVariables();

builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.WithOrigins("http://localhost:4200", "http://localhost:4201", "http://localhost:3002")
        .AllowAnyHeader().AllowAnyMethod()));

var pgRaw = Environment.GetEnvironmentVariable("REGULIQ_DATABASE_URL")
    ?? Environment.GetEnvironmentVariable("DIRECT_URL")
    ?? Environment.GetEnvironmentVariable("DATABASE_URL")
    ?? builder.Configuration.GetConnectionString("PostgreSQL");
var pgConn = DatabaseConnectionHelper.ResolvePostgresConnection(pgRaw);

var sqlitePath = Path.Combine(builder.Environment.ContentRootPath, "data", "reguliq.db");
Directory.CreateDirectory(Path.GetDirectoryName(sqlitePath)!);

builder.Services.AddDbContext<AppDbContext>(opt =>
{
    var usePostgres = Environment.GetEnvironmentVariable("REGULIQ_USE_POSTGRES") == "true";
    if (usePostgres && !string.IsNullOrWhiteSpace(pgConn))
        opt.UseNpgsql(pgConn);
    else
        opt.UseSqlite($"Data Source={sqlitePath}");
});

builder.Services.Configure<GeminiOptions>(o =>
{
    o.ApiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? "";
    o.DefaultModel = Environment.GetEnvironmentVariable("GEMINI_DEFAULT_MODEL") ?? "gemini-2.5-flash-lite";
});
builder.Services.Configure<NodeBridgeOptions>(o =>
{
    o.BaseUrl = Environment.GetEnvironmentVariable("NODE_API_URL") ?? "http://localhost:4000";
    o.Enabled = true;
});

builder.Services.AddHttpClient<GeminiService>();
builder.Services.AddHttpClient<NodeBridgeService>();

builder.Services.AddSingleton<KafkaConfig>();
builder.Services.AddSingleton<KafkaProducerService>();
builder.Services.AddSingleton<SessionPdfCache>();
builder.Services.AddSingleton<GovPointsService>();
builder.Services.AddSingleton<LocalJobQueue>();
builder.Services.AddSingleton<DualVerifyJobProcessor>();
builder.Services.AddHostedService<DualVerifyWorkerHosted>();
builder.Services.AddScoped<DualVerifyStoreService>();
builder.Services.AddScoped<DualVerifyService>();
builder.Services.AddSingleton<DashboardService>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.EnsureCreatedAsync();
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors();
app.MapControllers();

app.MapGet("/", () => Results.Ok(new { name = "Reguliq .NET API", version = "1.0.0" }));

var port = Environment.GetEnvironmentVariable("REGULIQ_API_PORT") ?? "5100";
app.Urls.Add($"http://localhost:{port}");

app.Run();
