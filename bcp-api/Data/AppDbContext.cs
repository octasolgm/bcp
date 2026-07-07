using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Reguliq.Api.Data.Entities;

namespace Reguliq.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<DualVerifySession> DualVerifySessions => Set<DualVerifySession>();
    public DbSet<DualVerifyPointJob> DualVerifyPointJobs => Set<DualVerifyPointJob>();
    public DbSet<ComplianceSession> ComplianceSessions => Set<ComplianceSession>();

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
