using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Infrastructure;
using Reguliq.Api.Services;
using Reguliq.Api.Workers;

// Load bcp-api/.env when present (standalone app folder)
var repoEnv = Path.Combine(Directory.GetCurrentDirectory(), ".env");
if (File.Exists(repoEnv))
{
    foreach (var line in File.ReadAllLines(repoEnv))
    {
        var t = line.Trim();
        if (t.StartsWith('#') || t.StartsWith("//") || !t.Contains('=')) continue;
        var idx = t.IndexOf('=');
        var key = t[..idx].Trim();
        var val = t[(idx + 1)..].Trim().Trim('"');
        Environment.SetEnvironmentVariable(key, val);
    }
}

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddEnvironmentVariables();

var rawDatabaseUrl = Environment.GetEnvironmentVariable("DATABASE_URL")
    ?? Environment.GetEnvironmentVariable("SUPABASE_POOLER_URL")
    ?? Environment.GetEnvironmentVariable("DIRECT_URL");
var workingDatabaseUrl = SupabasePoolerResolver.ResolveWorkingConnection(rawDatabaseUrl);
if (!string.IsNullOrWhiteSpace(workingDatabaseUrl)
    && !string.Equals(workingDatabaseUrl, rawDatabaseUrl, StringComparison.Ordinal))
{
    Environment.SetEnvironmentVariable("DATABASE_URL", workingDatabaseUrl);
    Console.WriteLine("Supabase: using auto-discovered pooler connection.");
}
else if (!string.IsNullOrWhiteSpace(rawDatabaseUrl))
{
    LogDatabaseUrlHint(rawDatabaseUrl);
}

static void LogDatabaseUrlHint(string raw)
{
    try
    {
        if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri)) return;
        var user = uri.UserInfo.Split(':')[0];
        Console.WriteLine($"DATABASE_URL host={uri.Host} port={uri.Port} user={user}");
        if (uri.Host.StartsWith("db.", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine(
                "WARNING: Direct db.*.supabase.co often fails on Windows (IPv6). " +
                "Use Session pooler URI from Supabase Dashboard → Settings → Database.");
        }
    }
    catch { /* ignore */ }
}

var dbConfig = DatabaseConfig.Resolve(builder.Configuration, builder.Environment);
Directory.CreateDirectory(Path.Combine(builder.Environment.ContentRootPath, "data"));

builder.Services.AddSingleton(dbConfig);

builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
{
    var raw = Environment.GetEnvironmentVariable("BCP_CORS_ORIGINS")
        ?? Environment.GetEnvironmentVariable("REGULIQ_CORS_ORIGINS")
        ?? "http://localhost:3002,http://localhost:4200";
    var origins = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    p.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod();
}));

builder.Services.AddDbContext<AppDbContext>(opt =>
{
    if (dbConfig.UsePostgres)
        opt.UseNpgsql(dbConfig.PostgresConnection);
    else
        opt.UseSqlite($"Data Source={dbConfig.SqlitePath}");
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
builder.Services.AddScoped<LocalDataMigrationService>();
builder.Services.AddSingleton<DashboardService>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
    try
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await SupabaseSchemaBootstrap.EnsureAsync(db, dbConfig);
        var migrator = scope.ServiceProvider.GetRequiredService<LocalDataMigrationService>();
        await migrator.MigrateAsync();
    }
    catch (Exception ex)
    {
        PostgresConnectionDiagnostics.SetError(ex);
        logger.LogError(ex, "Supabase bootstrap failed.");
        if (dbConfig.RequireSupabase)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("FATAL: BCP_REQUIRE_SUPABASE=true but Postgres is not reachable.");
            Console.Error.WriteLine(PostgresConnectionDiagnostics.LastError);
            Console.Error.WriteLine("Fix SUPABASE_DB_PASSWORD in bcp-api/.env (reset in Supabase Dashboard → Database).");
            Console.Error.WriteLine("If you see ECIRCUITBREAKER, wait 10 minutes with API stopped, then restart.");
            throw;
        }
    }
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors();
app.MapControllers();

app.MapGet("/", () => Results.Ok(new
{
    name = "BCP API",
    version = "1.0.0",
    persistence = dbConfig.UsePostgres ? "supabase" : "sqlite",
}));

var port = Environment.GetEnvironmentVariable("BCP_API_PORT")
    ?? Environment.GetEnvironmentVariable("REGULIQ_API_PORT")
    ?? Environment.GetEnvironmentVariable("WEBSITES_PORT")
    ?? Environment.GetEnvironmentVariable("PORT")
    ?? "5100";
app.Urls.Add($"http://0.0.0.0:{port}");

app.Run();
