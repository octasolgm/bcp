using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Reguliq.Api.Data;
using Reguliq.Api.Data.Entities;
using Reguliq.Api.Data.NewDashboard.Entities;
using Reguliq.Api.Services.NewDashboard.CorrectedDocs;
using Xunit;

namespace Reguliq.Api.Tests;

/// <summary>End-to-end over an in-memory DB: a run with two attached documents, a resolved gap whose
/// clause's retrieval named one of them, and an unresolved gap that must be left out entirely.</summary>
public class NdActionPlanEmbedResolverTests
{
    private static AppDbContext CreateDb() => new InMemoryAppDbContext(
        new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private sealed class InMemoryAppDbContext(DbContextOptions<AppDbContext> options) : AppDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<NdLocalDocumentExtractionSection>().Ignore(s => s.Embedding);
        }
    }

    private static string RetrievalJson(Guid docId, string docName, int page) => JsonSerializer.Serialize(new
    {
        acronymMatches = Array.Empty<object>(),
        synonymMatches = Array.Empty<object>(),
        bm25Matches = Array.Empty<object>(),
        matches = Array.Empty<object>(),
        subObligations = Array.Empty<string>(),
        fusedMatches = new[]
        {
            new
            {
                sectionId = Guid.NewGuid(),
                clauseNo = "3.1",
                textPreview = "preview",
                sourceDocumentId = docId,
                sourceDocumentName = docName,
                sourcePage = page,
                fusedScore = 0.9,
                bm25Score = (double?)null,
                embeddingSimilarity = (double?)null,
                matchedSubObligation = (string?)null,
            },
        },
    });

    private static string ResultJson(string documentReference, params string[] policyExtract) => JsonSerializer.Serialize(new
    {
        design_status = "compliant",
        operating_status = "compliant",
        overall_status = "compliant",
        confidence = 0.9,
        interpretation = "reasoning",
        policy_extract = policyExtract,
        document_reference = documentReference,
        gap_description = "",
        suggested_action = "",
        gap_direction = "",
    });

    [Fact]
    public async Task Resolves_the_document_and_page_from_a_hybrid_clauses_own_retrieval()
    {
        await using var db = CreateDb();
        var runId = Guid.NewGuid();
        var manualDocId = Guid.NewGuid();
        var procedureDocId = Guid.NewGuid();
        var pointId = Guid.NewGuid();

        db.NdAnalysisRuns.Add(new NdAnalysisRun
        {
            Id = runId,
            Name = "Test run",
            WorkflowEngine = "regul_pipeline_hybrid_v5",
            SelectedInternalDocIds = JsonSerializer.Serialize(new[] { manualDocId, procedureDocId }),
        });
        db.StoredDocuments.Add(new StoredDocument { Id = manualDocId, Title = "AML Manual", OriginalFileName = "aml-manual.pdf" });
        db.StoredDocuments.Add(new StoredDocument { Id = procedureDocId, Title = "KYC Procedure", OriginalFileName = "kyc-procedure.docx" });
        db.NdAnalysisPoints.Add(new NdAnalysisPoint
        {
            Id = pointId,
            AnalysisRunId = runId,
            PointSnapshot = JsonSerializer.Serialize(new { pointNumber = "3.1", pointTitle = "Summary of Minimum Statutory Obligations" }),
            OriginalAiActionPlan = "Overall gap description for clause 3.1.",
        });
        db.NdRegulForwardFindings.Add(new NdRegulForwardFinding
        {
            AnalysisRunId = runId,
            AnalysisPointId = pointId,
            ClauseNo = "3.1",
            RetrievalJson = RetrievalJson(manualDocId, "AML Manual", 29),
            ResultJson = ResultJson("AML Manual, p.29", "cited passage"),
        });
        db.NdAnalysisGaps.Add(new NdAnalysisGap
        {
            AnalysisRunId = runId,
            AnalysisPointId = pointId,
            GapIndex = 1,
            Status = GapStatuses.Resolved,
            ResolvedAt = new DateTimeOffset(2026, 9, 26, 0, 0, 0, TimeSpan.Zero),
        });
        db.NdAnalysisActionPlans.Add(new NdAnalysisActionPlan
        {
            AnalysisRunId = runId,
            AnalysisPointId = pointId,
            GapIndex = 1,
            Status = ActionPlanStatuses.Resolved,
            ActionPlan = "Added the missing retention section.",
            ResponsibilityLabel = "Compliance Department",
        });
        await db.SaveChangesAsync();

        var resolver = new NdActionPlanEmbedResolver(db, NullLogger<NdActionPlanEmbedResolver>.Instance);
        var jobs = await resolver.ResolveForRunAsync(runId, CancellationToken.None);

        var job = Assert.Single(jobs);
        Assert.Equal(manualDocId, job.StoredDocumentId);
        var target = Assert.Single(job.Targets);
        Assert.Equal("3.1", target.ClauseNo);
        Assert.Equal(29, target.Page);
        Assert.Equal("Added the missing retention section.", target.ActionText);
        Assert.Equal("Compliance Department", target.ResponsibilityLabel);
    }

    [Fact]
    public async Task A_resolved_action_plan_embeds_even_while_its_gap_is_still_open()
    {
        // A gap with two action plans where only one is resolved never reaches GapStatuses.Resolved
        // (that rollup is NdGapStatusResolver's job, unrelated to this class) — but the one resolved
        // action plan should still be embedded on its own, not held back until its sibling catches up.
        await using var db = CreateDb();
        var runId = Guid.NewGuid();
        var docId = Guid.NewGuid();
        var pointId = Guid.NewGuid();

        db.NdAnalysisRuns.Add(new NdAnalysisRun
        {
            Id = runId,
            Name = "Test run",
            WorkflowEngine = "regul_pipeline_hybrid_v5",
            SelectedInternalDocIds = JsonSerializer.Serialize(new[] { docId }),
        });
        db.StoredDocuments.Add(new StoredDocument { Id = docId, Title = "AML Manual", OriginalFileName = "aml-manual.pdf" });
        db.NdAnalysisPoints.Add(new NdAnalysisPoint
        {
            Id = pointId,
            AnalysisRunId = runId,
            PointSnapshot = JsonSerializer.Serialize(new { pointNumber = "3.5" }),
        });
        db.NdRegulForwardFindings.Add(new NdRegulForwardFinding
        {
            AnalysisRunId = runId,
            AnalysisPointId = pointId,
            ClauseNo = "3.5",
            RetrievalJson = RetrievalJson(docId, "AML Manual", 6),
            ResultJson = ResultJson("AML Manual, p.6"),
        });
        db.NdAnalysisGaps.Add(new NdAnalysisGap
        {
            AnalysisRunId = runId,
            AnalysisPointId = pointId,
            GapIndex = 1,
            Status = GapStatuses.Pending, // still open — its sibling action plan hasn't been resolved
        });
        db.NdAnalysisActionPlans.Add(new NdAnalysisActionPlan
        {
            AnalysisRunId = runId,
            AnalysisPointId = pointId,
            GapIndex = 1,
            Status = ActionPlanStatuses.Resolved,
            ActionPlan = "Clarified the money laundering definition.",
        });
        db.NdAnalysisActionPlans.Add(new NdAnalysisActionPlan
        {
            AnalysisRunId = runId,
            AnalysisPointId = pointId,
            GapIndex = 1,
            Status = ActionPlanStatuses.Pending, // sibling, still open
            ActionPlan = "Second action, not resolved yet.",
        });
        await db.SaveChangesAsync();

        var resolver = new NdActionPlanEmbedResolver(db, NullLogger<NdActionPlanEmbedResolver>.Instance);
        var jobs = await resolver.ResolveForRunAsync(runId, CancellationToken.None);

        var job = Assert.Single(jobs);
        var target = Assert.Single(job.Targets);
        Assert.Equal("Clarified the money laundering definition.", target.ActionText);
    }

    [Fact]
    public async Task Falls_back_to_matching_the_document_reference_text_when_there_is_no_retrieval_json()
    {
        await using var db = CreateDb();
        var runId = Guid.NewGuid();
        var manualDocId = Guid.NewGuid();
        var otherDocId = Guid.NewGuid();
        var pointId = Guid.NewGuid();

        db.NdAnalysisRuns.Add(new NdAnalysisRun
        {
            Id = runId,
            Name = "Test run",
            WorkflowEngine = "regul_pipeline_full", // V4: no per-clause retrieval preview
            SelectedInternalDocIds = JsonSerializer.Serialize(new[] { manualDocId, otherDocId }),
        });
        db.StoredDocuments.Add(new StoredDocument { Id = manualDocId, Title = "Internal AML Manual", OriginalFileName = "internal-aml-manual.pdf" });
        db.StoredDocuments.Add(new StoredDocument { Id = otherDocId, Title = "Unrelated Policy" });
        db.NdAnalysisPoints.Add(new NdAnalysisPoint
        {
            Id = pointId,
            AnalysisRunId = runId,
            PointSnapshot = JsonSerializer.Serialize(new { pointNumber = "3.5" }),
        });
        db.NdRegulForwardFindings.Add(new NdRegulForwardFinding
        {
            AnalysisRunId = runId,
            AnalysisPointId = pointId,
            ClauseNo = "3.5",
            RetrievalJson = null,
            ResultJson = ResultJson("Internal AML Manual, p.6-7 (Definitions section)"),
        });
        db.NdAnalysisGaps.Add(new NdAnalysisGap
        {
            AnalysisRunId = runId,
            AnalysisPointId = pointId,
            GapIndex = 1,
            Status = GapStatuses.Resolved,
        });
        db.NdAnalysisActionPlans.Add(new NdAnalysisActionPlan
        {
            AnalysisRunId = runId,
            AnalysisPointId = pointId,
            GapIndex = 1,
            Status = ActionPlanStatuses.Resolved,
            ActionPlan = "Clarified the money laundering definition.",
        });
        await db.SaveChangesAsync();

        var resolver = new NdActionPlanEmbedResolver(db, NullLogger<NdActionPlanEmbedResolver>.Instance);
        var jobs = await resolver.ResolveForRunAsync(runId, CancellationToken.None);

        var job = Assert.Single(jobs);
        Assert.Equal(manualDocId, job.StoredDocumentId); // matched by name, not the unrelated doc
    }

    [Fact]
    public async Task Single_attached_document_gets_embed_targets_without_retrieval_or_reference()
    {
        await using var db = CreateDb();
        var runId = Guid.NewGuid();
        var onlyDocId = Guid.NewGuid();
        var pointId = Guid.NewGuid();

        db.NdAnalysisRuns.Add(new NdAnalysisRun
        {
            Id = runId,
            Name = "Single-doc run",
            SelectedInternalDocIds = JsonSerializer.Serialize(new[] { onlyDocId }),
        });
        db.StoredDocuments.Add(new StoredDocument { Id = onlyDocId, Title = "Policy Manual", OriginalFileName = "policy-manual.pdf" });
        db.NdAnalysisPoints.Add(new NdAnalysisPoint
        {
            Id = pointId,
            AnalysisRunId = runId,
            PointSnapshot = JsonSerializer.Serialize(new { pointNumber = "2.1" }),
        });
        db.NdRegulForwardFindings.Add(new NdRegulForwardFinding
        {
            AnalysisRunId = runId,
            AnalysisPointId = pointId,
            ClauseNo = "2.1",
            RetrievalJson = null,
            ResultJson = ResultJson(""),
        });
        db.NdAnalysisGaps.Add(new NdAnalysisGap
        {
            AnalysisRunId = runId,
            AnalysisPointId = pointId,
            GapIndex = 1,
            Status = GapStatuses.Resolved,
        });
        db.NdAnalysisActionPlans.Add(new NdAnalysisActionPlan
        {
            AnalysisRunId = runId,
            AnalysisPointId = pointId,
            GapIndex = 1,
            Status = ActionPlanStatuses.Resolved,
            ActionPlan = "Documented the control.",
        });
        await db.SaveChangesAsync();

        var resolver = new NdActionPlanEmbedResolver(db, NullLogger<NdActionPlanEmbedResolver>.Instance);
        var jobs = await resolver.ResolveForRunAsync(runId, CancellationToken.None);

        var job = Assert.Single(jobs);
        Assert.Equal(onlyDocId, job.StoredDocumentId);
        Assert.Single(job.Targets);
    }
}
