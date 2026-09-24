using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;
using Reguliq.Api.Infrastructure.NewDashboard;

namespace Reguliq.Api.Infrastructure;

// Table/column names and the default id interpolated below are compile-time constants from this file.
#pragma warning disable EF1002

/// <summary>
/// Idempotent DDL for multi-workspace support (run from <see cref="NdIncrementalSchemaBootstrap"/>).
///
/// Every pre-existing row, including all demo data, is backfilled into the fixed Default workspace, so
/// nothing about the current (single-workspace) data or the demo experience changes.
///
/// A BEFORE INSERT trigger fills tenant_id when the app did not: child rows copy their parent's workspace,
/// root rows take their creator's home workspace, and anything else falls back to Default. The app sets
/// tenant_id itself inside a request; the trigger covers hosted workers and startup seeding.
/// </summary>
public static class NdWorkspaceSchemaBootstrap
{
    /// <summary>(table, parent table, parent FK, creator column): children copy the parent, roots copy the creator.</summary>
    private static readonly (string Table, string? ParentTable, string? ParentFk, string? CreatorColumn)[] Tables =
    [
        ("profiles", null, null, null),
        ("departments", null, null, "created_by"),
        ("regulation_documents", null, null, "created_by"),
        ("stored_documents", null, null, "uploaded_by"),
        ("libraries", null, null, "created_by"),
        ("analysis_runs", null, null, "created_by"),
        ("regulation_points", "regulation_documents", "regulation_document_id", null),
        ("nd_internal_document_sections", "stored_documents", "stored_document_id", null),
        ("library_points", "libraries", "library_id", null),
        ("analysis_points", "analysis_runs", "analysis_run_id", null),
        ("analysis_point_attachments", "analysis_points", "analysis_point_id", null),
        ("action_plan_history", "analysis_points", "analysis_point_id", null),
        ("analysis_reviews", "analysis_runs", "analysis_run_id", null),
        ("analysis_point_comments", "analysis_points", "analysis_point_id", null),
        ("action_plan_item_reviews", "analysis_points", "analysis_point_id", null),
        ("analysis_status_history", "analysis_runs", "analysis_run_id", null),
        ("temp_point_review_comments", "analysis_points", "analysis_point_id", null),
        ("analysis_action_plans", "analysis_runs", "analysis_run_id", null),
        ("analysis_gaps", "analysis_runs", "analysis_run_id", null),
        ("analysis_action_plan_assignees", "analysis_action_plans", "action_plan_id", null),
        ("analysis_action_plan_reviews", "analysis_action_plans", "action_plan_id", null),
        ("analysis_action_plan_date_history", "analysis_action_plans", "action_plan_id", null),
        ("analysis_action_plan_status_history", "analysis_action_plans", "action_plan_id", null),
        ("regul_forward_findings", "analysis_runs", "analysis_run_id", null),
        ("regul_internal_sections", "analysis_runs", "analysis_run_id", null),
        ("regul_reverse_mappings", "analysis_runs", "analysis_run_id", null),
        ("regul_qualitative_assessments", "analysis_runs", "analysis_run_id", null),
        ("nd_local_document_extractions", "stored_documents", "stored_document_id", null),
        ("nd_local_document_extraction_sections", "nd_local_document_extractions", "extraction_id", null),
        ("nd_ai_credit_ledger", null, null, "created_by"),
    ];

    public static async Task EnsureAsync(AppDbContext db, CancellationToken ct = default)
    {
        var defaultId = WorkspaceScope.DefaultWorkspaceId;

        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS nd_workspaces (
              id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
              name TEXT NOT NULL,
              slug TEXT NOT NULL,
              description TEXT NULL,
              is_active BOOLEAN NOT NULL DEFAULT true,
              created_by UUID NULL,
              created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
              updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ux_nd_workspaces_slug ON nd_workspaces (lower(slug));
            """,
            ct);

        await db.Database.ExecuteSqlRawAsync(
            $"""
            INSERT INTO nd_workspaces (id, name, slug, description)
            VALUES ('{defaultId}', 'Default workspace', 'default',
                    'Original workspace. Holds all data created before workspaces existed, and the demo accounts.')
            ON CONFLICT (id) DO NOTHING;
            """,
            ct);

        // Platform admin flag: every super admin that existed before workspaces keeps full rights, so
        // existing accounts (demo admins included) behave exactly as before. Runs only once, when the
        // column is first added, so later workspace admins are never promoted.
        await db.Database.ExecuteSqlRawAsync(
            """
            DO $$
            BEGIN
              IF NOT EXISTS (
                SELECT 1 FROM information_schema.columns
                WHERE table_name = 'profiles' AND column_name = 'is_platform_admin'
              ) THEN
                ALTER TABLE profiles ADD COLUMN is_platform_admin BOOLEAN NOT NULL DEFAULT false;
                UPDATE profiles SET is_platform_admin = true WHERE role = 'super_admin';
              END IF;
            END $$;
            ALTER TABLE profiles ADD COLUMN IF NOT EXISTS active_tenant_id UUID NULL;
            """,
            ct);

        // Prepaid AI credits per workspace: the balance is the SUM of this ledger, so top-ups, spend
        // and corrections are all one insert and nothing is overwritten.
        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS nd_ai_credit_ledger (
              id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
              tenant_id UUID NULL,
              kind TEXT NOT NULL DEFAULT 'usage',
              credits NUMERIC(18,4) NOT NULL DEFAULT 0,
              usd_cost NUMERIC(18,6) NOT NULL DEFAULT 0,
              usd_cost_estimated BOOLEAN NOT NULL DEFAULT false,
              provider TEXT NULL,
              model TEXT NULL,
              feature TEXT NULL,
              analysis_run_id UUID NULL,
              prompt_tokens BIGINT NOT NULL DEFAULT 0,
              completion_tokens BIGINT NOT NULL DEFAULT 0,
              cached_tokens BIGINT NOT NULL DEFAULT 0,
              generation_id TEXT NULL,
              note TEXT NULL,
              created_by UUID NULL,
              created_at TIMESTAMPTZ NOT NULL DEFAULT now()
            );
            CREATE INDEX IF NOT EXISTS idx_nd_ai_credit_ledger_tenant
              ON nd_ai_credit_ledger (tenant_id, created_at DESC);
            ALTER TABLE nd_ai_credit_ledger
              ADD COLUMN IF NOT EXISTS billed_usd NUMERIC(18,6) NULL;
            ALTER TABLE nd_workspaces
              ADD COLUMN IF NOT EXISTS ai_credit_low_threshold_pct INTEGER NOT NULL DEFAULT 20;
            """,
            ct);

        foreach (var (table, _, _, _) in Tables)
        {
            if (!await TableExistsAsync(db, table, ct)) continue;
            await db.Database.ExecuteSqlRawAsync(
                $"""
                ALTER TABLE {table} ADD COLUMN IF NOT EXISTS tenant_id UUID NULL;
                UPDATE {table} SET tenant_id = '{defaultId}' WHERE tenant_id IS NULL;
                CREATE INDEX IF NOT EXISTS idx_{table}_tenant ON {table} (tenant_id);
                """,
                ct);
        }

        // "Manual custom points" was one global row; each workspace now gets its own.
        await db.Database.ExecuteSqlRawAsync(
            """
            DROP INDEX IF EXISTS idx_regulation_documents_manual_singleton;
            CREATE UNIQUE INDEX IF NOT EXISTS idx_regulation_documents_manual_per_tenant
              ON regulation_documents (tenant_id) WHERE is_manual = true;
            """,
            ct);

        foreach (var (table, parentTable, parentFk, creatorColumn) in Tables)
        {
            if (!await TableExistsAsync(db, table, ct)) continue;

            string lookup;
            if (parentTable != null)
                lookup = $"SELECT tenant_id INTO t FROM {parentTable} WHERE id = NEW.{parentFk};";
            else if (creatorColumn != null)
                lookup = $"SELECT tenant_id INTO t FROM profiles WHERE id = NEW.{creatorColumn};";
            else
                lookup = "";

            var fn = $"nd_fill_tenant_{table}";
            await db.Database.ExecuteSqlRawAsync(
                $$"""
                CREATE OR REPLACE FUNCTION {{fn}}() RETURNS trigger AS $fn$
                DECLARE t UUID;
                BEGIN
                  IF NEW.tenant_id IS NULL THEN
                    {{lookup}}
                    NEW.tenant_id := COALESCE(t, '{{defaultId}}'::uuid);
                  END IF;
                  RETURN NEW;
                END;
                $fn$ LANGUAGE plpgsql;
                DROP TRIGGER IF EXISTS trg_{{fn}} ON {{table}};
                CREATE TRIGGER trg_{{fn}} BEFORE INSERT ON {{table}}
                  FOR EACH ROW EXECUTE FUNCTION {{fn}}();
                """,
                ct);
        }
    }

    private static async Task<bool> TableExistsAsync(AppDbContext db, string table, CancellationToken ct)
    {
        var count = await db.Database
            .SqlQueryRaw<int>(
                "SELECT COUNT(*)::int AS \"Value\" FROM information_schema.tables WHERE table_schema = 'public' AND table_name = {0}",
                table)
            .SingleAsync(ct);
        return count > 0;
    }
}
