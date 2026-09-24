using Reguliq.Api.Data.NewDashboard.Entities;

namespace Reguliq.Api.Services.NewDashboard;

/// <summary>
/// Slim list projection for regulation_documents — same idea as <see cref="NdAnalysisRunListQueries"/>.
///
/// The two heavy columns, extraction_result (~36 KB of JSON) and extraction_markdown (~18 KB), make an
/// average row ~21 KB, and list/nav-count queries returned every column. A single document list therefore
/// pulled hundreds of KB out of the database for cards that only show a name, a status and a count, which
/// was the largest source of Supabase egress on this project. Those two columns are loaded only where the
/// extracted content is actually used (document detail, extraction, analysis).
/// </summary>
public static class NdRegulationDocumentListQueries
{
    public static IQueryable<NdRegulationDocument> SelectListColumns(this IQueryable<NdRegulationDocument> q) =>
        q.Select(d => new NdRegulationDocument
        {
            Id = d.Id,
            StoredDocumentId = d.StoredDocumentId,
            Name = d.Name,
            FilePath = d.FilePath,
            FileUrl = d.FileUrl,
            DepartmentId = d.DepartmentId,
            ExtractionStatus = d.ExtractionStatus,
            ExtractionProgressLabel = d.ExtractionProgressLabel,
            ExtractionProgressPct = d.ExtractionProgressPct,
            ExtractionParseChunkCompleted = d.ExtractionParseChunkCompleted,
            ExtractedAt = d.ExtractedAt,
            ExtractedBy = d.ExtractedBy,
            IsManual = d.IsManual,
            Status = d.Status,
            CreatedBy = d.CreatedBy,
            CreatedAt = d.CreatedAt,
            UpdatedAt = d.UpdatedAt,
            TenantId = d.TenantId,
        });
}
