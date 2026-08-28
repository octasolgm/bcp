using Microsoft.EntityFrameworkCore;
using Reguliq.Api.Data;

namespace Reguliq.Api.Infrastructure;

/// <summary>Align Supabase tables with EF entities (extra columns + safe create).</summary>
public static class SupabaseSchemaBootstrap
{
    private static readonly string[] PatchSql =
    [
        """
        CREATE TABLE IF NOT EXISTS nd_system_settings (
          key TEXT PRIMARY KEY,
          value_json JSONB NOT NULL DEFAULT jsonb_build_object(),
          updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
          updated_by UUID NULL
        );
        """,
        """
        ALTER TABLE dual_verify_sessions
          ADD COLUMN IF NOT EXISTS queued_points INTEGER NOT NULL DEFAULT 0;
        """,
        """
        ALTER TABLE dual_verify_sessions
          ADD COLUMN IF NOT EXISTS transport TEXT NOT NULL DEFAULT 'local';
        """,
        """
        ALTER TABLE analysis_runs
          ADD COLUMN IF NOT EXISTS compare_prompt_version TEXT NULL;
        """,
        """
        CREATE TABLE IF NOT EXISTS stored_documents (
          id UUID PRIMARY KEY,
          title TEXT NOT NULL DEFAULT '',
          original_file_name TEXT NOT NULL DEFAULT '',
          file_type TEXT NOT NULL DEFAULT 'PDF',
          category TEXT NOT NULL DEFAULT 'AML/CFT',
          filter_key TEXT NOT NULL DEFAULT 'aml',
          doc_kind TEXT NOT NULL DEFAULT 'document',
          version TEXT NOT NULL DEFAULT 'v1',
          version_number INTEGER NOT NULL DEFAULT 1,
          status TEXT NOT NULL DEFAULT 'review-due',
          gap_count INTEGER NULL,
          pages INTEGER NOT NULL DEFAULT 0,
          size_bytes BIGINT NOT NULL DEFAULT 0,
          content_type TEXT NOT NULL DEFAULT 'application/octet-stream',
          storage_bucket TEXT NOT NULL DEFAULT 'doc',
          storage_path TEXT NOT NULL DEFAULT '',
          file_hash TEXT NULL,
          point_count INTEGER NULL,
          workspace_id TEXT NOT NULL DEFAULT 'snb-uae-difc',
          history_json JSONB NOT NULL DEFAULT '[]'::jsonb,
          created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
          updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
        );
        """,
        """
        CREATE INDEX IF NOT EXISTS ix_stored_documents_workspace_kind_title
          ON stored_documents (workspace_id, doc_kind, title);
        """,
        """
        CREATE INDEX IF NOT EXISTS ix_stored_documents_file_hash
          ON stored_documents (file_hash);
        """,
        """
        ALTER TABLE stored_documents
          ADD COLUMN IF NOT EXISTS file_hash TEXT NULL;
        """,
        """
        ALTER TABLE stored_documents
          ADD COLUMN IF NOT EXISTS point_count INTEGER NULL;
        """,
        """
        ALTER TABLE stored_documents
          ADD COLUMN IF NOT EXISTS parse_status TEXT NOT NULL DEFAULT 'pending';
        """,
        """
        ALTER TABLE stored_documents
          ADD COLUMN IF NOT EXISTS parsed_at TIMESTAMPTZ NULL;
        """,
        """
        ALTER TABLE stored_documents
          ADD COLUMN IF NOT EXISTS parse_error TEXT NULL;
        """,
        """
        ALTER TABLE stored_documents
          ADD COLUMN IF NOT EXISTS uploaded_by UUID NULL;
        """,
        """
        ALTER TABLE stored_documents
          ADD COLUMN IF NOT EXISTS parsed_by UUID NULL;
        """,
        """
        ALTER TABLE stored_documents
          ADD COLUMN IF NOT EXISTS is_hidden BOOLEAN NOT NULL DEFAULT false;
        """,
        """
        ALTER TABLE stored_documents
          ADD COLUMN IF NOT EXISTS hidden_at TIMESTAMPTZ NULL;
        """,
        """
        ALTER TABLE stored_documents
          ADD COLUMN IF NOT EXISTS hidden_by UUID NULL;
        """,
        """
        ALTER TABLE stored_documents
          ADD COLUMN IF NOT EXISTS source_storage_path TEXT NULL;
        """,
        """
        ALTER TABLE stored_documents
          ADD COLUMN IF NOT EXISTS extraction_cache_key TEXT NULL;
        """,
        """
        UPDATE stored_documents
        SET extraction_cache_key = 'nd-reg:' || replace(id::text, '-', '')
        WHERE extraction_cache_key IS NULL
          AND doc_kind IN ('document', 'internal', 'regulation');
        """,
        """
        CREATE TABLE IF NOT EXISTS landing_ai_parse_cache (
          file_hash TEXT PRIMARY KEY,
          file_name TEXT NULL,
          markdown TEXT NOT NULL,
          parse_model TEXT NULL,
          updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS landing_ai_extract_cache (
          file_hash TEXT NOT NULL,
          schema_key TEXT NOT NULL,
          points_json JSONB NOT NULL DEFAULT jsonb_build_object(),
          extract_model TEXT NULL,
          updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
          PRIMARY KEY (file_hash, schema_key)
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS document_analysis_runs (
          id UUID PRIMARY KEY,
          workspace_id TEXT NOT NULL DEFAULT 'snb-uae-difc',
          internal_document_id UUID NULL,
          regulation_document_id UUID NULL,
          dual_verify_session_id UUID NULL,
          compliance_session_id UUID NULL,
          label TEXT NOT NULL DEFAULT '',
          regulation_file_name TEXT NULL,
          internal_file_name TEXT NULL,
          internal_file_hash TEXT NULL,
          gov_file_hash TEXT NULL,
          status TEXT NOT NULL DEFAULT 'queued',
          point_count INTEGER NOT NULL DEFAULT 0,
          completed_points INTEGER NOT NULL DEFAULT 0,
          granularity TEXT NOT NULL DEFAULT 'leaf',
          created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
          updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
        );
        """,
        """
        ALTER TABLE document_analysis_runs
          ALTER COLUMN dual_verify_session_id DROP NOT NULL;
        """,
        """
        CREATE INDEX IF NOT EXISTS ix_document_analysis_runs_internal_doc
          ON document_analysis_runs (internal_document_id);
        """,
        """
        CREATE INDEX IF NOT EXISTS ix_document_analysis_runs_session
          ON document_analysis_runs (dual_verify_session_id);
        """,
        """
        ALTER TABLE analysis_runs
          ADD COLUMN IF NOT EXISTS workflow_engine TEXT NOT NULL DEFAULT 'bcp_landing';
        """,
        """
        ALTER TABLE analysis_runs
          ADD COLUMN IF NOT EXISTS enable_qualitative BOOLEAN NOT NULL DEFAULT false;
        """,
        """
        ALTER TABLE analysis_runs
          ADD COLUMN IF NOT EXISTS regul_llm_provider TEXT NULL;
        """,
        """
        ALTER TABLE analysis_runs
          ADD COLUMN IF NOT EXISTS regul_llm_model TEXT NULL;
        """,
        """
        ALTER TABLE analysis_runs
          ADD COLUMN IF NOT EXISTS regul_pipeline_phase TEXT NULL;
        """,
        """
        ALTER TABLE analysis_runs
          ADD COLUMN IF NOT EXISTS regul_pipeline_error TEXT NULL;
        """,
        """
        ALTER TABLE analysis_runs
          ADD COLUMN IF NOT EXISTS regul_clauses_confirmed_at TIMESTAMPTZ NULL;
        """,
        """
        CREATE TABLE IF NOT EXISTS regul_forward_findings (
          id UUID PRIMARY KEY,
          analysis_run_id UUID NOT NULL REFERENCES analysis_runs(id) ON DELETE CASCADE,
          analysis_point_id UUID NULL,
          clause_no TEXT NOT NULL DEFAULT '',
          clause_text TEXT NOT NULL DEFAULT '',
          status TEXT NOT NULL DEFAULT 'pending',
          result_json JSONB NULL,
          error_message TEXT NULL,
          created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
          updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
        );
        """,
        """
        CREATE INDEX IF NOT EXISTS ix_regul_forward_findings_run
          ON regul_forward_findings (analysis_run_id);
        """,
        """
        CREATE TABLE IF NOT EXISTS regul_internal_sections (
          id UUID PRIMARY KEY,
          analysis_run_id UUID NOT NULL REFERENCES analysis_runs(id) ON DELETE CASCADE,
          section_ref TEXT NOT NULL DEFAULT '',
          section_text TEXT NOT NULL DEFAULT '',
          source_doc TEXT NULL,
          source_page INTEGER NULL,
          created_at TIMESTAMPTZ NOT NULL DEFAULT now()
        );
        """,
        """
        CREATE INDEX IF NOT EXISTS ix_regul_internal_sections_run
          ON regul_internal_sections (analysis_run_id);
        """,
        """
        CREATE TABLE IF NOT EXISTS regul_reverse_mappings (
          id UUID PRIMARY KEY,
          analysis_run_id UUID NOT NULL REFERENCES analysis_runs(id) ON DELETE CASCADE,
          internal_section_id UUID NOT NULL,
          status TEXT NOT NULL DEFAULT 'pending',
          mapping TEXT NULL,
          mapped_clause_nos JSONB NULL,
          result_json JSONB NULL,
          error_message TEXT NULL,
          created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
          updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
        );
        """,
        """
        CREATE INDEX IF NOT EXISTS ix_regul_reverse_mappings_run
          ON regul_reverse_mappings (analysis_run_id);
        """,
        """
        CREATE TABLE IF NOT EXISTS regul_qualitative_assessments (
          id UUID PRIMARY KEY,
          analysis_run_id UUID NOT NULL UNIQUE REFERENCES analysis_runs(id) ON DELETE CASCADE,
          status TEXT NOT NULL DEFAULT 'pending',
          result_json JSONB NULL,
          error_message TEXT NULL,
          created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
          updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
        );
        """,
        """
        ALTER TABLE stored_documents
          ADD COLUMN IF NOT EXISTS section_extract_status TEXT NOT NULL DEFAULT 'pending';
        """,
        """
        ALTER TABLE stored_documents
          ADD COLUMN IF NOT EXISTS section_extracted_at TIMESTAMPTZ NULL;
        """,
        """
        ALTER TABLE stored_documents
          ADD COLUMN IF NOT EXISTS section_extract_error TEXT NULL;
        """,
        """
        ALTER TABLE stored_documents
          ADD COLUMN IF NOT EXISTS section_count INTEGER NULL;
        """,
        """
        ALTER TABLE stored_documents
          ADD COLUMN IF NOT EXISTS section_extracted_by UUID NULL;
        """,
        """
        ALTER TABLE stored_documents
          ADD COLUMN IF NOT EXISTS section_extract_progress_label TEXT NULL;
        """,
        """
        ALTER TABLE stored_documents
          ADD COLUMN IF NOT EXISTS section_extract_progress_pct INTEGER NULL;
        """,
        """
        ALTER TABLE stored_documents
          ADD COLUMN IF NOT EXISTS parse_progress_label TEXT NULL;
        """,
        """
        ALTER TABLE stored_documents
          ADD COLUMN IF NOT EXISTS parse_progress_pct INTEGER NULL;
        """,
        """
        CREATE TABLE IF NOT EXISTS nd_internal_document_sections (
          id UUID PRIMARY KEY,
          stored_document_id UUID NOT NULL REFERENCES stored_documents(id) ON DELETE CASCADE,
          section_ref TEXT NOT NULL DEFAULT '',
          section_text TEXT NOT NULL DEFAULT '',
          source_page INTEGER NULL,
          display_order INTEGER NOT NULL DEFAULT 0,
          created_at TIMESTAMPTZ NOT NULL DEFAULT now()
        );
        """,
        """
        CREATE INDEX IF NOT EXISTS ix_nd_internal_document_sections_doc
          ON nd_internal_document_sections (stored_document_id);
        """,
        """
        ALTER TABLE landing_ai_extract_cache
          DROP CONSTRAINT IF EXISTS landing_ai_extract_cache_schema_key_check;
        """,
    ];

    public static async Task EnsureAsync(AppDbContext db, DatabaseConfig dbConfig, CancellationToken ct = default)
    {
        if (!dbConfig.UsePostgres)
        {
            await db.Database.EnsureCreatedAsync(ct);
            return;
        }

        // Existing live DB — skip 50+ patch round-trips on every restart (each can take 60s+ on Supabase).
        if (await NdSchemaAlreadyPresentAsync(db, ct))
            return;

        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE OR REPLACE FUNCTION set_updated_at()
            RETURNS TRIGGER AS $$
            BEGIN
              NEW.updated_at = now();
              RETURN NEW;
            END;
            $$ LANGUAGE plpgsql;
            """,
            ct);

        // EnsureCreated scans all pg_catalog tables — very slow on remote Supabase (~60–120s).
        if (!await NdSchemaAlreadyPresentAsync(db, ct))
            await db.Database.EnsureCreatedAsync(ct);

        foreach (var sql in PatchSql)
            await db.Database.ExecuteSqlRawAsync(sql, ct);
    }

    /// <summary>True when enterprise ND tables already exist (live Supabase).</summary>
    public static async Task<bool> NdSchemaAlreadyPresentAsync(AppDbContext db, CancellationToken ct = default)
    {
        return await db.Database
            .SqlQueryRaw<bool>(
                """
                SELECT EXISTS (
                  SELECT 1 FROM information_schema.tables
                  WHERE table_schema = 'public' AND table_name = 'analysis_runs'
                ) AS "Value"
                """)
            .FirstAsync(ct);
    }
}
