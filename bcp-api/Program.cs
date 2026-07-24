using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Infrastructure;
using Reguliq.Api.Services;
using Reguliq.Api.Services.LandingAi;
using Reguliq.Api.Services.Storage;
using Reguliq.Api.Workers;
using System.Net.Http.Headers;

AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

var builder = WebApplication.CreateBuilder(args);

// Optional local secrets file — published to Azure with deploy (gitignored). See appsettings.Secrets.example.json.
builder.Configuration.AddJsonFile("appsettings.Secrets.json", optional: true, reloadOnChange: false);

AzureHosting.ConfigureWebHost(builder);

DatabaseConfig dbConfig;
try
{
    dbConfig = DatabaseConfig.Resolve(builder.Configuration, builder.Environment);
}
catch (Exception ex)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine("=== BCP API STARTUP FAILED ===");
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine("==============================");
    throw;
}
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

CorsPolicySetup.AddBcpCors(builder.Services, builder.Configuration);

builder.Services.AddDbContext<AppDbContext>(opt =>
{
    if (dbConfig.UsePostgres)
        opt.UseNpgsql(dbConfig.PostgresConnection);
    else
        opt.UseSqlite($"Data Source={dbConfig.SqlitePath}");
});

builder.Services.Configure<LandingAiOptions>(o =>
{
    o.ApiKey = BcpConfiguration.GetString(builder.Configuration, "LandingAi:ApiKey", "VISION_AGENT_API_KEY") ?? "";
    o.ApiBase = BcpConfiguration.GetString(builder.Configuration, "LandingAi:ApiBase", "LANDING_AI_API_BASE")
        ?? "https://api.va.landing.ai";
    o.ParseModel = BcpConfiguration.GetString(builder.Configuration, "LandingAi:ParseModel", "LANDING_AI_PARSE_MODEL")
        ?? "dpt-2-latest";
    o.ExtractModel = BcpConfiguration.GetString(builder.Configuration, "LandingAi:ExtractModel", "LANDING_AI_EXTRACT_MODEL")
        ?? "extract-latest";
});
builder.Services.AddScoped<LandingAiCacheRepository>();
builder.Services.AddScoped<LandingAiCompareService>();
builder.Services.AddScoped<LandingAiGovExtractService>();

var httpTimeout = TimeSpan.FromMinutes(
    BcpConfiguration.GetInt(builder.Configuration, 15, "Bcp:HttpTimeoutMinutes", "BCP_HTTP_TIMEOUT_MINUTES"));
static void ConfigureAiHttpTimeout(HttpClient client, TimeSpan timeout) => client.Timeout = timeout;
builder.Services.AddHttpClient<LandingAiHttpClient>(c => ConfigureAiHttpTimeout(c, httpTimeout));

builder.Services.Configure<GeminiOptions>(o =>
{
    o.ApiKey = BcpConfiguration.GetString(
        builder.Configuration,
        "Gemini:ApiKey",
        "GEMINI_API_KEY") ?? "";
    o.DefaultModel = BcpConfiguration.GetString(
        builder.Configuration,
        "Gemini:DefaultModel",
        "GEMINI_DEFAULT_MODEL") ?? "gemini-3.5-flash";
    var fallbacks = builder.Configuration.GetSection("Gemini:FallbackModels").Get<string[]>();
    if (fallbacks is { Length: > 0 })
        o.FallbackModels = fallbacks;
});
builder.Services.Configure<NodeBridgeOptions>(o =>
{
    o.BaseUrl = BcpConfiguration.GetString(
        builder.Configuration,
        "NodeBridge:BaseUrl",
        "NODE_API_URL") ?? "http://localhost:4000";
    o.Enabled = !BcpConfiguration.IsFalse(builder.Configuration, "NodeBridge:Enabled", "NODE_BRIDGE_ENABLED");
});

builder.Services.AddHttpClient<GeminiService>(c => ConfigureAiHttpTimeout(c, httpTimeout));
builder.Services.AddMemoryCache();
builder.Services.AddScoped<Reguliq.Api.Services.Llm.DualVerifyLlmSettingsService>();
builder.Services.AddScoped<DualVerifyLlmService>();
builder.Services.AddHttpClient<Reguliq.Api.Services.Llm.OpenAiCompatibleLlmClient>(c => ConfigureAiHttpTimeout(c, httpTimeout));
builder.Services.AddHttpClient<Reguliq.Api.Services.Llm.AnthropicLlmClient>(c => ConfigureAiHttpTimeout(c, httpTimeout));
builder.Services.AddHttpClient(nameof(Reguliq.Api.Services.Llm.XAiLlmClient), c => ConfigureAiHttpTimeout(c, httpTimeout));
builder.Services.AddScoped<Reguliq.Api.Services.Llm.XAiLlmClient>();
builder.Services.AddHttpClient<NodeBridgeService>(c => ConfigureAiHttpTimeout(c, httpTimeout));

builder.Services.AddSingleton<KafkaConfig>();
builder.Services.AddSingleton<KafkaProducerService>();
builder.Services.AddSingleton<SessionPdfCache>();
builder.Services.AddSingleton<GovPointsService>();
builder.Services.AddSingleton<LocalJobQueue>();
builder.Services.AddSingleton<DualVerifyJobStageTracker>();
builder.Services.AddSingleton<SessionCancellationTracker>();
builder.Services.AddSingleton<DualVerifyJobProcessor>();
builder.Services.AddHostedService<DualVerifyWorkerHosted>();
builder.Services.AddScoped<DualVerifyStoreService>();
builder.Services.AddScoped<CompliancePdfResolver>();
builder.Services.AddScoped<DualVerifyService>();
builder.Services.AddScoped<LocalDataMigrationService>();
builder.Services.AddScoped<TfsGuidelinesSeedService>();
builder.Services.AddScoped<AnalysisBundleSeedService>();
builder.Services.AddSingleton<DashboardService>();

builder.Services.Configure<SupabaseStorageOptions>(o =>
{
    o.Url = BcpConfiguration.GetString(builder.Configuration, "Supabase:Url", "SUPABASE_URL") ?? "";
    o.ServiceRoleKey = BcpConfiguration.GetString(
        builder.Configuration, "Supabase:ServiceRoleKey", "SUPABASE_SERVICE_ROLE_KEY") ?? "";
    o.Bucket = BcpConfiguration.GetString(builder.Configuration, "Supabase:StorageBucket", "SUPABASE_STORAGE_BUCKET")
        ?? "doc";
});
builder.Services.AddHttpClient<SupabaseStorageService>(c => c.Timeout = TimeSpan.FromMinutes(5));

// New Dashboard (enterprise platform)
builder.Services.Configure<Reguliq.Api.Infrastructure.NewDashboard.SupabaseJwtOptions>(o =>
{
    o.JwtSecret = BcpConfiguration.GetString(builder.Configuration, "Supabase:JwtSecret", "SUPABASE_JWT_SECRET") ?? "";
    o.Url = BcpConfiguration.GetString(builder.Configuration, "Supabase:Url", "SUPABASE_URL") ?? "";
    o.ServiceRoleKey = BcpConfiguration.GetString(
        builder.Configuration, "Supabase:ServiceRoleKey", "SUPABASE_SERVICE_ROLE_KEY") ?? "";
});
builder.Services.AddSingleton<Reguliq.Api.Infrastructure.NewDashboard.SupabaseJwtValidator>();
builder.Services.AddHttpClient(nameof(Reguliq.Api.Infrastructure.NewDashboard.SupabaseJwtValidator), c =>
{
    c.Timeout = TimeSpan.FromSeconds(15);
    c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
});
builder.Services.AddScoped<Reguliq.Api.Services.NewDashboard.NdRegulationUploadService>();
builder.Services.AddScoped<Reguliq.Api.Services.NewDashboard.NdInternalParseService>();
builder.Services.AddScoped<Reguliq.Api.Services.NewDashboard.NdAnalysisProcessor>();
builder.Services.AddScoped<Reguliq.Api.Services.NewDashboard.DemoAnalysisSeedService>();
builder.Services.AddHttpClient();

var ndJwtSecret = BcpConfiguration.GetString(builder.Configuration, "Supabase:JwtSecret", "SUPABASE_JWT_SECRET");
var ndSupabaseUrl = BcpConfiguration.GetString(builder.Configuration, "Supabase:Url", "SUPABASE_URL");
if (string.IsNullOrWhiteSpace(ndJwtSecret))
{
    Console.WriteLine(
        "WARNING: Supabase:JwtSecret is not set. ND auth will validate tokens via Supabase Auth API (slower). " +
        "For local HS256 validation, copy JWT secret from Supabase Dashboard → Project Settings → API → JWT Settings " +
        "into appsettings.Development.json (Supabase:JwtSecret) and re-run scripts/sync-secrets.ps1.");
}
else
{
    Console.WriteLine(
        $"ND JWT validator: secret prefix={ndJwtSecret[..Math.Min(5, ndJwtSecret.Length)]}..., " +
        $"issuer={(string.IsNullOrWhiteSpace(ndSupabaseUrl) ? "(set Supabase:Url)" : $"{ndSupabaseUrl.TrimEnd('/')}/auth/v1")}");
}

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
    try
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await SupabaseSchemaBootstrap.EnsureAsync(db, dbConfig);
        await NdSchemaBootstrap.EnsureAsync(db);
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

app.UseRouting();
app.UseCors();
app.MapControllers();

app.MapGet("/", () => Results.Ok(new
{
    name = "BCP API",
    version = "1.0.0",
    persistence = dbConfig.UsePostgres ? "supabase" : "sqlite",
}));

app.Run();
