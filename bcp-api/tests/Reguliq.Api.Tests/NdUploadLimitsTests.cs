using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Data.Entities;
using Reguliq.Api.Data.NewDashboard.Entities;
using Reguliq.Api.Infrastructure.NewDashboard;
using Reguliq.Api.Services.NewDashboard;
using Xunit;

namespace Reguliq.Api.Tests;

public class NdUploadLimitsTests
{
    private const long Mb = 1024L * 1024L;

    private sealed class InMemoryAppDbContext(DbContextOptions<AppDbContext> options) : AppDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<NdLocalDocumentExtractionSection>().Ignore(s => s.Embedding);
            modelBuilder.Entity<NdLocalDocumentPassage>().Ignore(p => p.Embedding);
        }
    }

    /// <summary>With no request scope the checks read the Default workspace, so the limits go on that row.</summary>
    private static async Task<AppDbContext> CreateDb(int? doc, int? upload, int? storage, long usedMb = 0, long hiddenMb = 0)
    {
        var db = new InMemoryAppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        db.NdWorkspaces.Add(new NdWorkspace
        {
            Id = WorkspaceScope.DefaultWorkspaceId,
            Name = "Bank",
            Slug = "bank",
            MaxDocumentMb = doc,
            MaxUploadMb = upload,
            MaxStorageMb = storage,
        });
        if (usedMb > 0) db.StoredDocuments.Add(Stored(usedMb, false));
        if (hiddenMb > 0) db.StoredDocuments.Add(Stored(hiddenMb, true));
        await db.SaveChangesAsync();
        return db;
    }

    private static StoredDocument Stored(long mb, bool hidden) => new()
    {
        Title = "doc",
        OriginalFileName = "doc.pdf",
        FileType = "PDF",
        DocKind = "document",
        StorageBucket = "b",
        StoragePath = Guid.NewGuid().ToString(),
        FileHash = Guid.NewGuid().ToString(),
        ContentType = "application/pdf",
        SizeBytes = mb * Mb,
        IsHidden = hidden,
        TenantId = WorkspaceScope.DefaultWorkspaceId,
    };

    private static (string?, long) File(string name, long mb) => (name, mb * Mb);

    [Fact]
    public async Task No_limits_allow_any_upload()
    {
        await using var db = await CreateDb(null, null, null, usedMb: 500);
        Assert.Null(await NdUploadLimits.CheckBatchAsync(db, [File("a.pdf", 49), File("b.pdf", 49)], default));
    }

    [Fact]
    public async Task A_file_above_the_per_document_limit_is_refused()
    {
        await using var db = await CreateDb(2, 20, 20);
        var problem = await NdUploadLimits.CheckBatchAsync(db, [File("small.pdf", 1), File("big.pdf", 3)], default);
        Assert.Contains("big.pdf", problem);
        Assert.Contains("per document", problem);
    }

    [Fact]
    public async Task A_batch_above_the_per_upload_limit_is_refused_even_when_each_file_fits()
    {
        await using var db = await CreateDb(2, 3, 20);
        var problem = await NdUploadLimits.CheckBatchAsync(db, [File("a.pdf", 2), File("b.pdf", 2)], default);
        Assert.Contains("per upload", problem);
    }

    [Fact]
    public async Task A_single_file_above_the_per_upload_limit_is_refused()
    {
        await using var db = await CreateDb(null, 3, null);
        var problem = await NdUploadLimits.CheckAsync(db, "a.pdf", 4 * Mb, default);
        Assert.Contains("per upload", problem);
    }

    [Theory]
    [InlineData(18, 2, true)]
    [InlineData(19, 2, false)]
    public async Task Total_storage_cannot_be_exceeded(long usedMb, long fileMb, bool fits)
    {
        await using var db = await CreateDb(null, null, 20, usedMb);
        var problem = await NdUploadLimits.CheckAsync(db, "a.pdf", fileMb * Mb, default);
        Assert.Equal(fits, problem == null);
    }

    [Fact]
    public async Task Hidden_documents_do_not_count_but_restoring_one_is_checked()
    {
        await using var db = await CreateDb(null, null, 20, usedMb: 15, hiddenMb: 10);
        Assert.Null(await NdUploadLimits.CheckAsync(db, "a.pdf", 5 * Mb, default));

        var problem = await NdUploadLimits.CheckRestoreAsync(db, "doc.pdf", 10 * Mb, default);
        Assert.Contains("cannot be restored", problem);
        Assert.Null(await NdUploadLimits.CheckRestoreAsync(db, "doc.pdf", 5 * Mb, default));
    }
}
