using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Data.NewDashboard.Entities;
using Reguliq.Api.Services.NewDashboard;
using Xunit;

namespace Reguliq.Api.Tests;

/// <summary>
/// extraction_result (~36 KB) and extraction_markdown (~18 KB) made an average regulation_documents row
/// ~21 KB. Lists and nav counts read every row on every call, which was the biggest single source of
/// database egress. These tests pin the slim projection so those columns cannot come back by accident.
/// </summary>
public class RegulationDocumentListQueryTests
{
    private static AppDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            // UseVector mirrors Program.cs: the model has a pgvector column and will not build without it.
            .UseNpgsql("Host=localhost;Database=bcp_test;Username=postgres;Password=postgres", o => o.UseVector())
            .Options;
        return new AppDbContext(options);
    }

    [Fact]
    public void SelectListColumns_does_not_read_the_heavy_extraction_columns()
    {
        using var db = CreateDb();

        var sql = db.NdRegulationDocuments.AsNoTracking().SelectListColumns().ToQueryString();

        Assert.DoesNotContain("extraction_result", sql);
        Assert.DoesNotContain("extraction_markdown", sql);
    }

    [Fact]
    public void SelectListColumns_still_reads_what_the_list_cards_show()
    {
        using var db = CreateDb();

        var sql = db.NdRegulationDocuments.AsNoTracking().SelectListColumns().ToQueryString();

        foreach (var column in new[]
                 {
                     "id", "stored_document_id", "name", "file_path", "department_id",
                     "extraction_status", "extracted_at", "is_manual", "status", "created_at", "tenant_id",
                 })
        {
            Assert.Contains(column, sql);
        }
    }

    [Fact]
    public void Unprojected_query_still_reads_everything_for_detail_and_extraction_paths()
    {
        using var db = CreateDb();

        var sql = db.NdRegulationDocuments.AsNoTracking().ToQueryString();

        Assert.Contains("extraction_markdown", sql);
    }
}
