using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Data.NewDashboard.Entities;
using Reguliq.Api.Models;
using Reguliq.Api.Services.LandingAi;
using Reguliq.Api.Services.Storage;

namespace Reguliq.Api.Services.NewDashboard;

public class NdRegulationUploadService(
    AppDbContext db,
    SupabaseStorageService storage,
    LandingAiGovExtractService govExtract,
    ILogger<NdRegulationUploadService> logger)
{
    public async Task<NdRegulationDocument> UploadAndExtractAsync(
        byte[] bytes,
        string fileName,
        string contentType,
        Guid? departmentId,
        Guid userId,
        CancellationToken ct)
    {
        if (!storage.IsConfigured)
            throw new InvalidOperationException("Supabase Storage not configured.");

        var title = Path.GetFileNameWithoutExtension(fileName).Trim();
        var safeName = SanitizeFileName(fileName);
        var titleKey = NormalizeKey(title);
        var objectPath = $"regulations/nd/{titleKey}/{Guid.NewGuid():N}/{safeName}";

        await using (var stream = new MemoryStream(bytes))
            await storage.UploadAsync(objectPath, stream, contentType, upsert: true, ct);

        var fileHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        var stored = new Data.Entities.StoredDocument
        {
            Title = title,
            OriginalFileName = fileName,
            FileType = "PDF",
            Category = "Regulation",
            FilterKey = "regulation",
            DocKind = "regulation",
            ContentType = contentType,
            StorageBucket = storage.Bucket,
            StoragePath = objectPath,
            FileHash = fileHash,
            SizeBytes = bytes.Length,
            Pages = Math.Max(1, (int)Math.Round(bytes.Length / 45000.0)),
            UploadedBy = userId,
        };
        db.StoredDocuments.Add(stored);
        await db.SaveChangesAsync(ct);

        var regDoc = new NdRegulationDocument
        {
            StoredDocumentId = stored.Id,
            Name = title,
            FilePath = objectPath,
            DepartmentId = departmentId,
            ExtractionStatus = "pending",
            CreatedBy = userId,
        };
        db.NdRegulationDocuments.Add(regDoc);
        await db.SaveChangesAsync(ct);

        return regDoc;
    }

    /// <summary>Resolve ND or legacy stored-document id and run Landing AI extraction.</summary>
    public async Task<NdRegulationDocument> ExtractByRegulationIdAsync(
        Guid regulationId,
        Guid userId,
        CancellationToken ct)
    {
        var regDoc = await db.NdRegulationDocuments.FirstOrDefaultAsync(d => d.Id == regulationId, ct)
            ?? await db.NdRegulationDocuments.FirstOrDefaultAsync(d => d.StoredDocumentId == regulationId, ct);

        if (regDoc == null)
        {
            var stored = await db.StoredDocuments.FirstOrDefaultAsync(
                d => d.Id == regulationId && d.DocKind == "regulation", ct)
                ?? throw new InvalidOperationException("Regulation document not found.");

            regDoc = new NdRegulationDocument
            {
                StoredDocumentId = stored.Id,
                Name = stored.Title,
                FilePath = "",
                ExtractionStatus = "processing",
                CreatedBy = userId,
            };
            db.NdRegulationDocuments.Add(regDoc);
            await db.SaveChangesAsync(ct);
        }

        return await ExtractAsync(regDoc.Id, userId, ct);
    }

    public async Task<NdRegulationDocument> ExtractAsync(Guid docId, Guid userId, CancellationToken ct)
    {
        var regDoc = await db.NdRegulationDocuments.FirstOrDefaultAsync(d => d.Id == docId, ct)
            ?? throw new InvalidOperationException("Regulation document not found.");

        regDoc.ExtractionStatus = "processing";
        regDoc.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        byte[] bytes;
        string fileName;
        if (regDoc.StoredDocumentId is Guid storedId)
        {
            var stored = await db.StoredDocuments.FirstOrDefaultAsync(d => d.Id == storedId, ct)
                ?? throw new InvalidOperationException("Stored document not found.");
            bytes = await storage.DownloadAsync(stored.StoragePath, ct);
            fileName = stored.OriginalFileName;
        }
        else if (!string.IsNullOrWhiteSpace(regDoc.FilePath))
        {
            bytes = await storage.DownloadAsync(regDoc.FilePath, ct);
            fileName = regDoc.Name + ".pdf";
        }
        else
        {
            throw new InvalidOperationException("No file available for extraction.");
        }

        await ExtractInternalAsync(regDoc, bytes, fileName, userId, ct);
        return regDoc;
    }

    private async Task ExtractInternalAsync(
        NdRegulationDocument regDoc,
        byte[] bytes,
        string fileName,
        Guid userId,
        CancellationToken ct)
    {
        var result = await govExtract.ExtractFromUploadAsync(bytes, fileName, null, ct);

        var existingPoints = await db.NdRegulationPoints
            .Where(p => p.RegulationDocumentId == regDoc.Id)
            .ToListAsync(ct);
        if (existingPoints.Count > 0)
            db.NdRegulationPoints.RemoveRange(existingPoints);

        var pointsJson = JsonSerializer.Serialize(new { points = result.Points });
        regDoc.ExtractionResult = pointsJson;
        regDoc.ExtractionStatus = "completed";
        regDoc.ExtractedAt = DateTimeOffset.UtcNow;
        regDoc.ExtractedBy = userId;
        regDoc.UpdatedAt = DateTimeOffset.UtcNow;

        var order = 0;
        foreach (var p in result.Points)
        {
            var json = JsonSerializer.Serialize(p);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var pointId = root.TryGetProperty("point_id", out var pid) ? pid.GetString() ?? "" : "";
            var title = root.TryGetProperty("title", out var t) ? t.GetString() : null;
            var text = root.TryGetProperty("text", out var tx) ? tx.GetString() ?? "" : "";
            var section = root.TryGetProperty("section", out var s) ? s.GetString() : null;
            var pointType = root.TryGetProperty("point_type", out var pt) ? pt.GetString() : null;
            int? pageHint = null;
            if (root.TryGetProperty("page_hint", out var ph) && ph.ValueKind == JsonValueKind.Number && ph.TryGetInt32(out var hint) && hint > 0)
                pageHint = hint;

            var isAnnex = GovPointClassifier.IsAnnexPoint(pointId, title, section);
            var isIntro = GovPointClassifier.IsIntroductionPoint(pointId, title, text, section, pointType);

            db.NdRegulationPoints.Add(new NdRegulationPoint
            {
                RegulationDocumentId = regDoc.Id,
                PointNumber = pointId,
                PointTitle = title,
                PointContent = text,
                PageReference = FormatPointPageReference(section, pageHint),
                IsIntroductionPoint = isIntro,
                IsAnnexPoint = isAnnex,
            });
            order++;
        }

        if (regDoc.StoredDocumentId is Guid sid)
        {
            var stored = await db.StoredDocuments.FirstOrDefaultAsync(d => d.Id == sid, ct);
            if (stored != null)
            {
                stored.FileHash = result.FileHash;
                stored.PointCount = result.Points.Count;
                stored.UpdatedAt = DateTimeOffset.UtcNow;
            }
        }

        await db.SaveChangesAsync(ct);
    }

    private static string? FormatPointPageReference(string? section, int? pdfPage)
    {
        var sec = section?.Trim();
        if (pdfPage is > 0)
            return string.IsNullOrWhiteSpace(sec) ? $"p. {pdfPage}" : $"{sec} · p. {pdfPage}";
        return sec;
    }

    private static string NormalizeKey(string title)
    {
        var chars = title.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        var s = new string(chars);
        while (s.Contains("--", StringComparison.Ordinal)) s = s.Replace("--", "-", StringComparison.Ordinal);
        return s.Trim('-');
    }

    private static string SanitizeFileName(string name)
    {
        var baseName = Path.GetFileName(name);
        foreach (var c in Path.GetInvalidFileNameChars())
            baseName = baseName.Replace(c, '_');
        return baseName;
    }
}
