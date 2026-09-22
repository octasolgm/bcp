using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Reguliq.Api.Controllers.NewDashboard;
using Reguliq.Api.Data;
using Reguliq.Api.Data.Entities;
using Reguliq.Api.Data.NewDashboard.Entities;
using Reguliq.Api.Infrastructure.NewDashboard;
using Reguliq.Api.Services.NewDashboard;
using Xunit;

namespace Reguliq.Api.Tests;

public class WorkspaceScopingTests
{
    private static readonly Guid BankA = Guid.NewGuid();
    private static readonly Guid BankB = Guid.NewGuid();

    private static AppDbContext CreateDb(string name)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(name)
            .Options;
        return new InMemoryAppDbContext(options);
    }

    /// <summary>The in-memory provider cannot map pgvector columns; nothing here touches embeddings.</summary>
    private sealed class InMemoryAppDbContext(DbContextOptions<AppDbContext> options) : AppDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<NdLocalDocumentExtractionSection>().Ignore(s => s.Embedding);
        }
    }

    private static void Enter(Guid? workspaceId) =>
        WorkspaceScope.Set(workspaceId is Guid id ? new WorkspaceScopeState(id, id, true, false) : null);

    [Fact]
    public async Task Inserts_are_stamped_and_queries_are_filtered_to_the_active_workspace()
    {
        var dbName = Guid.NewGuid().ToString();
        try
        {
            Enter(BankA);
            await using (var db = CreateDb(dbName))
            {
                db.NdDepartments.Add(new NdDepartment { Name = "A compliance" });
                db.StoredDocuments.Add(new StoredDocument { Title = "A policy" });
                await db.SaveChangesAsync();
            }

            Enter(BankB);
            await using (var db = CreateDb(dbName))
            {
                db.NdDepartments.Add(new NdDepartment { Name = "B compliance" });
                await db.SaveChangesAsync();

                Assert.Equal(["B compliance"], await db.NdDepartments.Select(d => d.Name).ToListAsync());
                Assert.Empty(await db.StoredDocuments.ToListAsync());
            }

            Enter(BankA);
            await using (var db = CreateDb(dbName))
            {
                var dept = Assert.Single(await db.NdDepartments.ToListAsync());
                Assert.Equal("A compliance", dept.Name);
                Assert.Equal(BankA, dept.TenantId);
                Assert.Equal("A policy", Assert.Single(await db.StoredDocuments.ToListAsync()).Title);
            }

            // Hosted workers and startup run with no scope and must see every row.
            Enter(null);
            await using (var db = CreateDb(dbName))
            {
                Assert.Equal(2, await db.NdDepartments.CountAsync());
            }
        }
        finally
        {
            Enter(null);
        }
    }

    [Fact]
    public async Task Explicit_tenant_on_insert_is_kept()
    {
        var dbName = Guid.NewGuid().ToString();
        try
        {
            Enter(BankA);
            await using (var db = CreateDb(dbName))
            {
                db.NdLibraries.Add(new NdLibrary { Name = "For B", TenantId = BankB });
                await db.SaveChangesAsync();
                Assert.Empty(await db.NdLibraries.ToListAsync());
            }

            Enter(BankB);
            await using (var db = CreateDb(dbName))
            {
                Assert.Equal("For B", Assert.Single(await db.NdLibraries.ToListAsync()).Name);
            }
        }
        finally
        {
            Enter(null);
        }
    }

    [Fact]
    public async Task Regulation_points_keep_the_active_status_filter_and_add_the_tenant_filter()
    {
        var dbName = Guid.NewGuid().ToString();
        try
        {
            Enter(null);
            await using (var db = CreateDb(dbName))
            {
                db.NdRegulationPoints.AddRange(
                    new NdRegulationPoint { PointNumber = "A-1", TenantId = BankA },
                    new NdRegulationPoint { PointNumber = "A-old", TenantId = BankA, Status = NdRegulationPointStatus.Removed },
                    new NdRegulationPoint { PointNumber = "B-1", TenantId = BankB });
                await db.SaveChangesAsync();
            }

            Enter(BankA);
            await using (var db = CreateDb(dbName))
            {
                Assert.Equal(["A-1"], await db.NdRegulationPoints.Select(p => p.PointNumber).ToListAsync());
            }
        }
        finally
        {
            Enter(null);
        }
    }

    [Fact]
    public async Task Profiles_are_not_filtered_so_auth_and_name_lookups_work_across_workspaces()
    {
        var dbName = Guid.NewGuid().ToString();
        try
        {
            Enter(null);
            var platformAdmin = Guid.NewGuid();
            await using (var db = CreateDb(dbName))
            {
                db.NdProfiles.Add(new NdProfile
                {
                    Id = platformAdmin,
                    FullName = "Owner",
                    Role = "super_admin",
                    IsPlatformAdmin = true,
                    TenantId = WorkspaceScope.DefaultWorkspaceId,
                });
                await db.SaveChangesAsync();
            }

            Enter(BankA);
            await using (var db = CreateDb(dbName))
            {
                Assert.NotNull(await db.NdProfiles.FirstOrDefaultAsync(p => p.Id == platformAdmin));
            }
        }
        finally
        {
            Enter(null);
        }
    }

    [Fact]
    public async Task Dashboard_cache_never_serves_one_workspace_the_other_workspaces_numbers()
    {
        var cache = new NdDashboardCacheService(new MemoryCache(new MemoryCacheOptions()));
        try
        {
            Enter(BankA);
            var a = await cache.GetOrCreateAsync("overview", _ => Task.FromResult("numbers of A"));
            Enter(BankB);
            var b = await cache.GetOrCreateAsync("overview", _ => Task.FromResult("numbers of B"));

            Assert.Equal("numbers of A", a);
            Assert.Equal("numbers of B", b);
        }
        finally
        {
            Enter(null);
        }
    }

    [Fact]
    public void Legacy_data_only_surfaces_in_the_default_workspace()
    {
        try
        {
            Enter(null);
            Assert.True(WorkspaceScope.InDefaultWorkspace);
            Enter(WorkspaceScope.DefaultWorkspaceId);
            Assert.True(WorkspaceScope.InDefaultWorkspace);
            Enter(BankA);
            Assert.False(WorkspaceScope.InDefaultWorkspace);
        }
        finally
        {
            Enter(null);
        }
    }

    [Theory]
    [InlineData("admin", "super_admin")]
    [InlineData("Admin", "super_admin")]
    [InlineData("super_admin", "super_admin")]
    [InlineData("maker", "maker")]
    [InlineData("reviewer", "reviewer")]
    [InlineData("owner", null)]
    [InlineData("", null)]
    public void Managed_roles_accept_admin_as_the_workspace_admin_role(string raw, string? expected) =>
        Assert.Equal(expected, UsersController.NormalizeManagedRole(raw));

    [Theory]
    [InlineData("Emirates Bank", "emirates-bank")]
    [InlineData("  ADCB / UAE (Retail) ", "adcb-uae-retail")]
    [InlineData("***", "")]
    public void Workspace_short_names_are_slugified(string raw, string expected) =>
        Assert.Equal(expected, WorkspacesController.Slugify(raw));
}
