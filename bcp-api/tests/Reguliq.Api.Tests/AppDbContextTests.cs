using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Data.Entities;
using Xunit;

namespace Reguliq.Api.Tests;

public class AppDbContextTests
{
    [Fact]
    public void JsonColumns_AreMappedToJsonb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=bcp_test;Username=postgres;Password=postgres")
            .Options;

        using var db = new AppDbContext(options);
        var model = db.Model;

        AssertColumnType<DualVerifySession>(model, nameof(DualVerifySession.SummaryJson), "jsonb");
        AssertColumnType<DualVerifyPointJob>(model, nameof(DualVerifyPointJob.AgreementJson), "jsonb");
        AssertColumnType<ComplianceSession>(model, nameof(ComplianceSession.SkippedJson), "jsonb");
        AssertColumnType<ComplianceSession>(model, nameof(ComplianceSession.ResultsJson), "jsonb");
        AssertColumnType<ComplianceSession>(model, nameof(ComplianceSession.SummaryJson), "jsonb");
    }

    private static void AssertColumnType<TEntity>(Microsoft.EntityFrameworkCore.Metadata.IModel model, string propertyName, string expectedType)
    {
        var entity = model.FindEntityType(typeof(TEntity));
        Assert.NotNull(entity);
        var property = entity.FindProperty(propertyName);
        Assert.NotNull(property);
        Assert.Equal(expectedType, property.GetColumnType());
    }
}
