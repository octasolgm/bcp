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
    ];

    public static async Task EnsureAsync(AppDbContext db, DatabaseConfig dbConfig, CancellationToken ct = default)
    {
        if (!dbConfig.UsePostgres)
        {
            await db.Database.EnsureCreatedAsync(ct);
            return;
        }

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

        // Existing DBs: EnsureCreated is a no-op when any table already exists,
        // so explicitly create/patch stored_documents and dual_verify columns.
        await db.Database.EnsureCreatedAsync(ct);

        foreach (var sql in PatchSql)
            await db.Database.ExecuteSqlRawAsync(sql, ct);
    }
}
