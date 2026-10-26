using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Hosting;
using Reguliq.Api.Services.NewDashboard;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Reguliq.Api.Controllers.NewDashboard;
using Reguliq.Api.Data;
using Reguliq.Api.Data.Entities;
using Reguliq.Api.Data.NewDashboard.Entities;
using Reguliq.Api.Infrastructure.NewDashboard;
using Xunit;

namespace Reguliq.Api.Tests;

public class RulebookCategoriesControllerTests : IDisposable
{
    private static readonly Guid Workspace = Guid.NewGuid();
    private readonly string _dbName = Guid.NewGuid().ToString();
    private readonly Guid _adminId = Guid.NewGuid();
    private readonly Guid _makerId = Guid.NewGuid();

    public RulebookCategoriesControllerTests()
    {
        WorkspaceScope.Set(new WorkspaceScopeState(Workspace, Workspace, true, false));
        using var db = CreateDb();
        db.NdProfiles.Add(new NdProfile { Id = _adminId, FullName = "Admin", Role = "super_admin" });
        db.NdProfiles.Add(new NdProfile { Id = _makerId, FullName = "Maker", Role = "maker" });
        db.SaveChanges();
    }

    public void Dispose() => WorkspaceScope.Set(null);

    private sealed class InMemoryAppDbContext(DbContextOptions<AppDbContext> options) : AppDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<NdLocalDocumentExtractionSection>().Ignore(s => s.Embedding);
            modelBuilder.Entity<NdLocalDocumentPassage>().Ignore(p => p.Embedding);
        }
    }

    private AppDbContext CreateDb() =>
        new InMemoryAppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(_dbName).Options);

    /// <summary>bcp-api, found by walking up from the test output folder (holds SeedData/).</summary>
    private static string ApiRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Bcp.Api.csproj"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("bcp-api folder not found");
    }

    private RulebookCategoriesController Controller(AppDbContext db, Guid userId)
    {
        var jwt = new SupabaseJwtValidator(
            Options.Create(new SupabaseJwtOptions()),
            NullLogger<SupabaseJwtValidator>.Instance,
            null!,
            new TestWebHostEnvironment());
        var http = new DefaultHttpContext();
        http.Items[WorkspaceResolutionMiddleware.JwtUserItemKey] = new JwtUser(userId, null);
        return new RulebookCategoriesController(
            db, jwt, new TestWebHostEnvironment { ContentRootPath = ApiRoot() },
            null!, null!, new NdDashboardCacheService(new MemoryCache(new MemoryCacheOptions())), null!,
            NullLogger<RulebookCategoriesController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
    }

    private static int Status(IActionResult result) => (result as ObjectResult)?.StatusCode ?? 200;

    [Fact]
    public async Task Import_sample_builds_the_authority_tree_once()
    {
        await using var db = CreateDb();
        var api = Controller(db, _adminId);

        Assert.Equal(200, Status(await api.ImportSample(CancellationToken.None)));

        var rows = await db.NdRulebookCategories.ToListAsync();
        var authorities = rows.Where(r => r.ParentId == null).ToList();
        Assert.Equal(
            ["Central Bank of the UAE", "Dubai Financial Services Authority", "Abu Dhabi Global Market", "Capital Market Authority"],
            authorities.OrderBy(a => a.SortOrder).Select(a => a.Name));
        var cbuae = authorities.Single(a => a.Name == "Central Bank of the UAE");
        var level2 = rows.Where(r => r.ParentId == cbuae.Id).ToList();
        var level3 = rows.Where(r => level2.Any(p => p.Id == r.ParentId)).ToList();
        var level4 = rows.Where(r => level3.Any(p => p.Id == r.ParentId)).ToList();
        Assert.Equal(4, level2.Count);
        Assert.Equal(41, level3.Count);
        Assert.Equal(164, level4.Count);
        var capital = level4.Single(r => r.Name == "Regulations Re Capital Adequacy");
        Assert.StartsWith("https://rulebook.centralbank.ae/", capital.DocumentUrl);
        Assert.EndsWith(".pdf", capital.DocumentUrl);
        Assert.Equal(134, level4.Count(r => r.DocumentUrl != null));
        Assert.All(rows, r => Assert.Equal(Workspace, r.TenantId));

        Assert.Equal(409, Status(await api.ImportSample(CancellationToken.None)));
        Assert.Equal(256, await db.NdRulebookCategories.CountAsync());
    }

    [Fact]
    public async Task Create_nests_four_levels_and_refuses_a_fifth_and_duplicates()
    {
        await using var db = CreateDb();
        var api = Controller(db, _adminId);

        async Task<Guid> Add(string name, Guid? parent)
        {
            var res = await api.Create(new(name, null, parent), CancellationToken.None);
            Assert.Equal(200, Status(res));
            return (await db.NdRulebookCategories.SingleAsync(c => c.Name == name && c.ParentId == parent)).Id;
        }

        var cbuae = await Add("Central Bank of the UAE", null);
        var banking = await Add("Banking", cbuae);
        var capital = await Add("Capital Adequacy", banking);
        var rules = await Add("Regulations Re Capital Adequacy", capital);

        Assert.Equal(400, Status(await api.Create(new("Too deep", null, rules), CancellationToken.None)));
        Assert.Equal(409, Status(await api.Create(new("banking", null, cbuae), CancellationToken.None)));
        Assert.Equal(200, Status(await api.Create(new("Capital Adequacy", null, null), CancellationToken.None)));
        Assert.Equal(400, Status(await api.Create(new("  ", null, null), CancellationToken.None)));
    }

    [Fact]
    public async Task Update_renames_and_delete_removes_the_whole_branch()
    {
        await using var db = CreateDb();
        var api = Controller(db, _adminId);
        await api.Create(new("Banking", null, null), CancellationToken.None);
        var banking = await db.NdRulebookCategories.SingleAsync();
        await api.Create(new("Liquidity", null, banking.Id), CancellationToken.None);
        var liquidity = await db.NdRulebookCategories.SingleAsync(c => c.Name == "Liquidity");
        await api.Create(new("Advances to Deposits Ratio", null, liquidity.Id), CancellationToken.None);
        await api.Create(new("Insurance", null, null), CancellationToken.None);

        Assert.Equal(200, Status(await api.Update(liquidity.Id, new("Liquidity Risk", "Bank liquidity", null), CancellationToken.None)));
        Assert.Equal("Liquidity Risk", (await db.NdRulebookCategories.AsNoTracking().SingleAsync(c => c.Id == liquidity.Id)).Name);

        Assert.Equal(200, Status(await api.Delete(banking.Id, CancellationToken.None)));
        Assert.Equal(["Insurance"], await db.NdRulebookCategories.Select(c => c.Name).ToListAsync());
    }

    [Fact]
    public async Task Only_admins_can_change_categories()
    {
        await using var db = CreateDb();
        var maker = Controller(db, _makerId);

        Assert.Equal(403, Status(await maker.Create(new("Banking", null, null), CancellationToken.None)));
        Assert.Equal(403, Status(await maker.ImportSample(CancellationToken.None)));
        Assert.Equal(200, Status(await maker.List(CancellationToken.None)));
        Assert.Equal(403, Status(await maker.ImportDocument(Guid.NewGuid(), CancellationToken.None)));
    }
}
