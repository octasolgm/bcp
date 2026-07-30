using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Reguliq.Api.Data.Entities;
using Reguliq.Api.Data.NewDashboard.Entities;
using Reguliq.Api.Services.NewDashboard;

namespace Reguliq.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<DualVerifySession> DualVerifySessions => Set<DualVerifySession>();
    public DbSet<DualVerifyPointJob> DualVerifyPointJobs => Set<DualVerifyPointJob>();
    public DbSet<ComplianceSession> ComplianceSessions => Set<ComplianceSession>();
    public DbSet<StoredDocument> StoredDocuments => Set<StoredDocument>();
    public DbSet<DocumentAnalysisRun> DocumentAnalysisRuns => Set<DocumentAnalysisRun>();

    public DbSet<NdProfile> NdProfiles => Set<NdProfile>();
    public DbSet<NdDepartment> NdDepartments => Set<NdDepartment>();
    public DbSet<NdRegulationDocument> NdRegulationDocuments => Set<NdRegulationDocument>();
    public DbSet<NdRegulationPoint> NdRegulationPoints => Set<NdRegulationPoint>();
    public DbSet<NdLibrary> NdLibraries => Set<NdLibrary>();
    public DbSet<NdLibraryPoint> NdLibraryPoints => Set<NdLibraryPoint>();
    public DbSet<NdAnalysisRun> NdAnalysisRuns => Set<NdAnalysisRun>();
    public DbSet<NdAnalysisPoint> NdAnalysisPoints => Set<NdAnalysisPoint>();
    public DbSet<NdAnalysisPointAttachment> NdAnalysisPointAttachments => Set<NdAnalysisPointAttachment>();
    public DbSet<NdActionPlanHistory> NdActionPlanHistories => Set<NdActionPlanHistory>();
    public DbSet<NdAnalysisReview> NdAnalysisReviews => Set<NdAnalysisReview>();
    public DbSet<NdAnalysisPointComment> NdAnalysisPointComments => Set<NdAnalysisPointComment>();
    public DbSet<NdActionPlanItemReview> NdActionPlanItemReviews => Set<NdActionPlanItemReview>();
    public DbSet<NdAnalysisStatusHistory> NdAnalysisStatusHistories => Set<NdAnalysisStatusHistory>();
    public DbSet<NdHiddenLegacyRun> NdHiddenLegacyRuns => Set<NdHiddenLegacyRun>();
    public DbSet<NdSystemSetting> NdSystemSettings => Set<NdSystemSetting>();
    public DbSet<NdRegulForwardFinding> NdRegulForwardFindings => Set<NdRegulForwardFinding>();
    public DbSet<NdRegulInternalSection> NdRegulInternalSections => Set<NdRegulInternalSection>();
    public DbSet<NdRegulReverseMapping> NdRegulReverseMappings => Set<NdRegulReverseMapping>();
    public DbSet<NdRegulQualitativeAssessment> NdRegulQualitativeAssessments => Set<NdRegulQualitativeAssessment>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DualVerifySession>(e =>
        {
            e.ToTable("dual_verify_sessions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Status).HasColumnName("status");
            e.Property(x => x.Granularity).HasColumnName("granularity");
            e.Property(x => x.GovDocId).HasColumnName("gov_doc_id");
            e.Property(x => x.InternalDocId).HasColumnName("internal_doc_id");
            e.Property(x => x.GovFileHash).HasColumnName("gov_file_hash");
            e.Property(x => x.InternalFileHash).HasColumnName("internal_file_hash");
            e.Property(x => x.GovFileName).HasColumnName("gov_file_name");
            e.Property(x => x.InternalFileName).HasColumnName("internal_file_name");
            e.Property(x => x.TotalPoints).HasColumnName("total_points");
            e.Property(x => x.CompletedPoints).HasColumnName("completed_points");
            e.Property(x => x.FailedPoints).HasColumnName("failed_points");
            e.Property(x => x.RunningPoints).HasColumnName("running_points");
            e.Property(x => x.QueuedPoints).HasColumnName("queued_points");
            e.Property(x => x.Phase2Model).HasColumnName("phase2_model");
            e.Property(x => x.Pipeline).HasColumnName("pipeline");
            e.Property(x => x.ComplianceSessionKey).HasColumnName("compliance_session_key");
            e.Property(x => x.SummaryJson).HasColumnName("summary_json").HasColumnType("jsonb");
            e.Property(x => x.Transport).HasColumnName("transport");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.Property(x => x.CompletedAt).HasColumnName("completed_at");
            e.HasMany(x => x.PointJobs).WithOne(x => x.Session).HasForeignKey(x => x.SessionId);
        });

        modelBuilder.Entity<DualVerifyPointJob>(e =>
        {
            e.ToTable("dual_verify_point_jobs");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.SessionId).HasColumnName("session_id");
            e.Property(x => x.PointId).HasColumnName("point_id");
            e.Property(x => x.PointTitle).HasColumnName("point_title");
            e.Property(x => x.GovText).HasColumnName("gov_text");
            e.Property(x => x.Status).HasColumnName("status");
            e.Property(x => x.Attempt).HasColumnName("attempt");
            e.Property(x => x.MaxAttempts).HasColumnName("max_attempts");
            e.Property(x => x.LandingMessage).HasColumnName("landing_message");
            e.Property(x => x.LlmMessage).HasColumnName("llm_message");
            e.Property(x => x.AgreementJson).HasColumnName("agreement_json").HasColumnType("jsonb");
            e.Property(x => x.ErrorMessage).HasColumnName("error_message");
            e.Property(x => x.StartedAt).HasColumnName("started_at");
            e.Property(x => x.CompletedAt).HasColumnName("completed_at");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.HasIndex(x => new { x.SessionId, x.PointId }).IsUnique();
        });

        modelBuilder.Entity<ComplianceSession>(e =>
        {
            e.ToTable("landing_ai_compliance_sessions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.SessionKey).HasColumnName("session_key");
            e.Property(x => x.GovFileHash).HasColumnName("gov_file_hash");
            e.Property(x => x.InternalFileHash).HasColumnName("internal_file_hash");
            e.Property(x => x.GovFileName).HasColumnName("gov_file_name");
            e.Property(x => x.InternalFileName).HasColumnName("internal_file_name");
            e.Property(x => x.TotalGovPoints).HasColumnName("total_gov_points");
            e.Property(x => x.ComparedPoints).HasColumnName("compared_points");
            e.Property(x => x.SkippedPoints).HasColumnName("skipped_points");
            e.Property(x => x.SkippedJson).HasColumnName("skipped_json").HasColumnType("jsonb");
            e.Property(x => x.ResultsJson).HasColumnName("results_json").HasColumnType("jsonb");
            e.Property(x => x.SummaryJson).HasColumnName("summary_json").HasColumnType("jsonb");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.HasIndex(x => x.SessionKey).IsUnique();
        });

        modelBuilder.Entity<StoredDocument>(e =>
        {
            e.ToTable("stored_documents");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.WorkspaceId, x.DocKind, x.Title });
            e.HasIndex(x => x.FileHash);
            e.Property(x => x.HistoryJson).HasColumnType("jsonb");
            e.Property(x => x.FileHash).HasColumnName("file_hash");
            e.Property(x => x.PointCount).HasColumnName("point_count");
        });

        modelBuilder.Entity<DocumentAnalysisRun>(e =>
        {
            e.ToTable("document_analysis_runs");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.InternalDocumentId);
            e.HasIndex(x => x.DualVerifySessionId);
            e.HasIndex(x => x.InternalFileHash);
        });

        modelBuilder.Entity<NdRegulationDocument>()
            .Property(e => e.ExtractionResult).HasColumnType("jsonb");
        modelBuilder.Entity<NdRegulationPoint>(e =>
        {
            e.Property(p => p.Status).HasDefaultValue(NdRegulationPointStatus.Active);
            e.HasQueryFilter(p => p.Status == NdRegulationPointStatus.Active);
            e.HasOne<NdRegulationDocument>()
                .WithMany(d => d.Points)
                .HasForeignKey(p => p.RegulationDocumentId)
                .OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<NdLibraryPoint>(e =>
        {
            e.Property(p => p.PointSnapshot).HasColumnType("jsonb");
            e.HasOne<NdLibrary>()
                .WithMany(l => l.LibraryPoints)
                .HasForeignKey(p => p.LibraryId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne<NdRegulationPoint>()
                .WithMany()
                .HasForeignKey(p => p.RegulationPointId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne<NdRegulationDocument>()
                .WithMany()
                .HasForeignKey(p => p.RegulationDocumentId)
                .OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<NdAnalysisRun>()
            .Property(e => e.SelectedPointsSnapshot).HasColumnType("jsonb");
        modelBuilder.Entity<NdAnalysisRun>()
            .Property(e => e.SelectedInternalDocIds).HasColumnType("jsonb");
        modelBuilder.Entity<NdAnalysisRun>()
            .Property(e => e.SelectedRegulationDocIds).HasColumnType("jsonb");
        modelBuilder.Entity<NdRegulForwardFinding>(e =>
        {
            e.Property(f => f.ResultJson).HasColumnType("jsonb");
            e.HasIndex(f => f.AnalysisRunId);
        });
        modelBuilder.Entity<NdRegulReverseMapping>(e =>
        {
            e.Property(m => m.MappedClauseNos).HasColumnType("jsonb");
            e.Property(m => m.ResultJson).HasColumnType("jsonb");
            e.HasIndex(m => m.AnalysisRunId);
        });
        modelBuilder.Entity<NdRegulQualitativeAssessment>(e =>
        {
            e.Property(q => q.ResultJson).HasColumnType("jsonb");
            e.HasIndex(q => q.AnalysisRunId).IsUnique();
        });
        modelBuilder.Entity<NdRegulInternalSection>(e =>
        {
            e.HasIndex(s => s.AnalysisRunId);
        });
        modelBuilder.Entity<NdAnalysisPoint>(e =>
        {
            e.Property(p => p.PointSnapshot).HasColumnType("jsonb");
            e.Property(p => p.LandingAiResult).HasColumnType("jsonb");
            e.Property(p => p.GoogleAiResult).HasColumnType("jsonb");
            e.HasOne<NdAnalysisRun>()
                .WithMany(r => r.Points)
                .HasForeignKey(p => p.AnalysisRunId)
                .OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<NdAnalysisPointAttachment>(e =>
        {
            e.HasOne<NdAnalysisPoint>()
                .WithMany()
                .HasForeignKey(a => a.AnalysisPointId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne<StoredDocument>()
                .WithMany()
                .HasForeignKey(a => a.StoredDocumentId)
                .OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<NdAnalysisReview>(e =>
        {
            e.HasOne<NdAnalysisRun>()
                .WithMany()
                .HasForeignKey(r => r.AnalysisRunId)
                .OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<NdAnalysisStatusHistory>(e =>
        {
            e.HasOne<NdAnalysisRun>()
                .WithMany()
                .HasForeignKey(h => h.AnalysisRunId)
                .OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<NdActionPlanHistory>(e =>
        {
            e.HasOne<NdAnalysisPoint>()
                .WithMany()
                .HasForeignKey(h => h.AnalysisPointId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<NdSystemSetting>(e =>
        {
            e.ToTable("nd_system_settings");
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasColumnName("key");
            e.Property(x => x.ValueJson).HasColumnName("value_json").HasColumnType("jsonb");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.Property(x => x.UpdatedBy).HasColumnName("updated_by");
        });

        ConfigureUtcDateTimes(modelBuilder);
    }

    private static void ConfigureUtcDateTimes(ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                if (property.ClrType == typeof(DateTime))
                {
                    property.SetValueConverter(
                        new ValueConverter<DateTime, DateTime>(
                            v => ToUtc(v),
                            v => DateTime.SpecifyKind(v, DateTimeKind.Utc)));
                }
                else if (property.ClrType == typeof(DateTime?))
                {
                    property.SetValueConverter(
                        new ValueConverter<DateTime?, DateTime?>(
                            v => v.HasValue ? ToUtc(v.Value) : v,
                            v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v));
                }
            }
        }
    }

    private static DateTime ToUtc(DateTime value) =>
        value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };
}
