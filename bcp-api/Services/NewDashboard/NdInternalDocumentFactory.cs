using Reguliq.Api.Data.Entities;

namespace Reguliq.Api.Services.NewDashboard;

/// <summary>
/// Builds the stored-document row for an internal document upload. Shared by the normal upload endpoint and the
/// cloud import, so a file brought in from Google Drive is exactly the same kind of document as an uploaded one.
/// </summary>
public static class NdInternalDocumentFactory
{
    public static StoredDocument Create(
        NdStoredDocumentUploadService.PreparedUpload prepared,
        string title,
        string storageBucket,
        Guid uploadedBy)
    {
        var row = new StoredDocument
        {
            Title = title,
            OriginalFileName = prepared.OriginalFileName,
            FileType = prepared.FileType,
            DocKind = "document",
            StorageBucket = storageBucket,
            StoragePath = prepared.StoragePath,
            FileHash = prepared.FileHash,
            SizeBytes = prepared.SizeBytes,
            ContentType = prepared.ContentType,
            ParseStatus = "pending",
            UploadedBy = uploadedBy,
        };
        row.ExtractionCacheKey = NdRegulationCacheKeys.ForStoredDocument(row.Id);
        return row;
    }
}
