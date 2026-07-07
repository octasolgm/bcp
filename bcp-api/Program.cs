using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Infrastructure;
using Reguliq.Api.Services;
using Reguliq.Api.Workers;

AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

var builder = WebApplication.CreateBuilder(args);

var dbConfig = DatabaseConfig.Resolve(builder.Configuration, builder.Environment);
Directory.CreateDirectory(Path.Combine(builder.Environment.ContentRootPath, "data"));

if (!string.IsNullOrWhiteSpace(dbConfig.PostgresConnection))
{
    LogDatabaseUrlHint(dbConfig.PostgresConnection);
}

static void LogDatabaseUrlHint(string connectionString)
{
    try
    {
        var builder = new Npgsql.NpgsqlConnectionStringBuilder(connectionString);
        Console.WriteLine($"PostgreSQL host={builder.Host} port={builder.Port} user={builder.Username}");
        if (builder.Host?.StartsWith("db.", StringComparison.OrdinalIgnoreCase) == true)
        {
            Console.WriteLine(
                "WARNING: Direct db.*.supabase.co often fails on Windows (IPv6). " +
                "Use Session pooler URI from Supabase Dashboard → Settings → Database.");
        }
    }
    catch { /* ignore */ }
}

builder.Services.AddSingleton(dbConfig);

builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
{
    var raw = BcpConfiguration.GetString(
        builder.Configuration,
        "Bcp:CorsOrigins",
        "BCP_CORS_ORIGINS",
        "REGULIQ_CORS_ORIGINS")
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
    o.ApiKey = BcpConfiguration.GetString(
        builder.Configuration,
        "Gemini:ApiKey",
        "GEMINI_API_KEY") ?? "";
    o.DefaultModel = BcpConfiguration.GetString(
        builder.Configuration,
        "Gemini:DefaultModel",
        "GEMINI_DEFAULT_MODEL") ?? "gemini-2.5-flash-lite";
});
builder.Services.Configure<NodeBridgeOptions>(o =>
{
    o.BaseUrl = BcpConfiguration.GetString(
        builder.Configuration,
        "NodeBridge:BaseUrl",
        "NODE_API_URL") ?? "http://localhost:4000";
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
            Console.Error.WriteLine("FATAL: Bcp:RequireSupabase=true but Postgres is not reachable.");
            Console.Error.WriteLine(PostgresConnectionDiagnostics.LastError);
            Console.Error.WriteLine(
                "Fix Supabase:DbPassword in appsettings.Development.json " +
                "(reset in Supabase Dashboard → Database).");
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

var port = BcpConfiguration.GetString(
    builder.Configuration,
    "Bcp:ApiPort",
    "BCP_API_PORT",
    "REGULIQ_API_PORT",
    "WEBSITES_PORT",
    "PORT")
    ?? "5100";
app.Urls.Add($"http://0.0.0.0:{port}");

app.Run();
