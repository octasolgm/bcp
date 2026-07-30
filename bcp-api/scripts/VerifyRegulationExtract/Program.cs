using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Reguliq.Api.Data;
using Reguliq.Api.Infrastructure;
using Reguliq.Api.Services.NewDashboard;

var docIdArg = args.FirstOrDefault(a => !a.StartsWith('-'));
var docNameFilter = args.FirstOrDefault(a => a.StartsWith("--name="))?.Split('=', 2)[1]
    ?? "CBUAE";

var apiRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
var config = new ConfigurationBuilder()
    .SetBasePath(apiRoot)
    .AddJsonFile("appsettings.json", optional: true)
    .AddJsonFile("appsettings.Development.json", optional: true)
    .AddJsonFile("appsettings.Secrets.json", optional: true)
    .AddEnvironmentVariables()
    .Build();

var pgRaw = config.GetConnectionString("PostgreSQL")
    ?? config.GetConnectionString("DirectUrl");
var cs = DatabaseConnectionHelper.ResolvePostgresConnection(pgRaw, config);
if (string.IsNullOrWhiteSpace(cs))
{
    Console.Error.WriteLine("FAIL: No PostgreSQL connection string in appsettings.");
    return 1;
}

var options = new DbContextOptionsBuilder<AppDbContext>()
    .UseNpgsql(cs)
    .Options;

await using var db = new AppDbContext(options);

var docQuery = db.NdRegulationDocuments.AsNoTracking().AsQueryable();
if (Guid.TryParse(docIdArg, out var docId))
    docQuery = docQuery.Where(d => d.Id == docId);
else
    docQuery = docQuery.Where(d => d.Name.Contains(docNameFilter));

var doc = await docQuery.OrderByDescending(d => d.CreatedAt).FirstOrDefaultAsync();
if (doc == null)
{
    Console.Error.WriteLine($"FAIL: No regulation document found (filter: {docIdArg ?? docNameFilter}).");
    return 1;
}

Console.WriteLine($"Document: {doc.Name}");
Console.WriteLine($"  Id: {doc.Id}");
Console.WriteLine($"  Status: {doc.ExtractionStatus}");
Console.WriteLine($"  Stored doc: {doc.StoredDocumentId}");

var points = await db.NdRegulationPoints.AsNoTracking()
    .Where(p => p.RegulationDocumentId == doc.Id)
    .ToListAsync();

var active = points.Where(p => p.Status == NdRegulationPointStatus.Active).ToList();
var removed = points.Where(p => p.Status == NdRegulationPointStatus.Removed).ToList();
var junkActive = active.Where(p => GovPointExtractNormalizer.IsJunkExtractPointId(p.PointNumber)).ToList();
var numericActive = active.Where(p => GovPointExtractNormalizer.IsValidExtractPointId(p.PointNumber)).ToList();
var partActive = active.Where(p => p.PointNumber.StartsWith("Part ", StringComparison.OrdinalIgnoreCase)).ToList();

Console.WriteLine();
Console.WriteLine($"Points in DB: {points.Count} total, {active.Count} active, {removed.Count} soft-deleted");
Console.WriteLine($"  Valid numeric/legal ids (active): {numericActive.Count}");
Console.WriteLine($"  Junk ids (active): {junkActive.Count}");
Console.WriteLine($"  Part * ids (active): {partActive.Count}");

var sample4114 = active.FirstOrDefault(p =>
    p.PointNumber.Trim().Equals("4.1.1.4", StringComparison.OrdinalIgnoreCase));
if (sample4114 != null)
{
    Console.WriteLine();
    Console.WriteLine("4.1.1.4 found:");
    Console.WriteLine($"  Title: {sample4114.PointTitle}");
    Console.WriteLine($"  Content length: {sample4114.PointContent?.Length ?? 0} chars");
    Console.WriteLine($"  Page ref: {sample4114.PageReference}");
    var ok = (sample4114.PointContent?.Length ?? 0) >= 400
        && sample4114.PointContent!.Contains("ML/FT business risk assessment", StringComparison.OrdinalIgnoreCase);
    Console.WriteLine($"  Full text check: {(ok ? "PASS" : "FAIL")}");
}
else
{
    Console.WriteLine();
    Console.WriteLine("WARN: 4.1.1.4 not found among active points.");
}

var nraFragment = active.Where(p =>
    (p.PointTitle ?? "").Contains("NRA", StringComparison.OrdinalIgnoreCase)
    || (p.PointContent ?? "").Contains("topical risk assessment", StringComparison.OrdinalIgnoreCase))
    .Take(5)
    .ToList();
if (nraFragment.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine("NRA-related active points (sample):");
    foreach (var p in nraFragment)
    {
        Console.WriteLine($"  - {p.PointNumber}: title={p.PointTitle?.Length ?? 0}c, content={p.PointContent?.Length ?? 0}c");
    }
}

if (doc.StoredDocumentId is Guid storedId)
{
    var stored = await db.StoredDocuments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == storedId);
    Console.WriteLine();
    Console.WriteLine($"Stored document: {storedId}");
    Console.WriteLine($"  Extraction cache key: {stored?.ExtractionCacheKey ?? "(none)"}");
    Console.WriteLine($"  File hash: {(string.IsNullOrWhiteSpace(stored?.FileHash) ? "(none)" : "set")}");
    Console.WriteLine($"  Pages: {stored?.Pages}");
}

var recentRun = await db.NdAnalysisRuns.AsNoTracking()
    .Where(r => r.SelectedRegulationDocIds != null && r.SelectedRegulationDocIds.Contains(doc.Id.ToString()))
    .OrderByDescending(r => r.CreatedAt)
    .FirstOrDefaultAsync();

if (recentRun != null)
{
    var runPoints = await db.NdAnalysisPoints.AsNoTracking()
        .Where(p => p.AnalysisRunId == recentRun.Id)
        .Take(3)
        .ToListAsync();
    Console.WriteLine();
    Console.WriteLine($"Recent analysis run: {recentRun.Name} ({recentRun.Status}, {recentRun.ProcessedPointsCount}/{recentRun.TotalPointsCount})");
    foreach (var rp in runPoints)
    {
        var snapLen = rp.PointSnapshot?.Length ?? 0;
        var hasLanding = !string.IsNullOrWhiteSpace(rp.LandingAiResult);
        Console.WriteLine($"  Point {rp.Id}: snapshot={snapLen}c, landing={(hasLanding ? "yes" : "no")}, status={rp.LandingAiStatus}");
    }
}

var pass = junkActive.Count == 0 && partActive.Count == 0 && numericActive.Count >= 50;
Console.WriteLine();
Console.WriteLine(pass ? "OVERALL: PASS (extract quality looks OK)" : "OVERALL: NEEDS ATTENTION (run Repair or re-extract)");
return pass ? 0 : 2;
