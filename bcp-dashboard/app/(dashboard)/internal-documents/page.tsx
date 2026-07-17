"use client";

import { useEffect, useState } from "react";
import { TopNav } from "@/components/shell/TopNav";
import {
  getInternalDocuments,
  getProfile,
  uploadInternalDocument,
} from "@/lib/api/bcp-api-client";
import { getClientToken } from "@/lib/auth/client-token";
import type { InternalDocument } from "@/lib/types";
import type { UserProfile } from "@/lib/api/bcp-api-client";
import { formatBytes, formatDate } from "@/lib/utils";

export default function InternalDocumentsPage() {
  const [profile, setProfile] = useState<UserProfile | null>(null);
  const [docs, setDocs] = useState<InternalDocument[]>([]);
  const [file, setFile] = useState<File | null>(null);
  const [loading, setLoading] = useState(true);
  const [uploading, setUploading] = useState(false);
  const [error, setError] = useState("");

  async function load() {
    const token = await getClientToken();
    if (!token) return;
    const [profRes, docRes] = await Promise.all([
      getProfile(token),
      getInternalDocuments(token),
    ]);
    if (profRes.success && profRes.data) setProfile(profRes.data);
    if (docRes.success && docRes.data) setDocs(docRes.data as InternalDocument[]);
    setLoading(false);
  }

  useEffect(() => {
    load();
  }, []);

  async function handleUpload(e: React.FormEvent) {
    e.preventDefault();
    if (!file) return;
    setUploading(true);
    setError("");
    const token = await getClientToken();
    if (!token) return;
    const res = await uploadInternalDocument(token, file);
    if (res.success) {
      setFile(null);
      await load();
    } else {
      setError(res.message ?? "Upload failed");
    }
    setUploading(false);
  }

  const canUpload = profile?.role === "maker" || profile?.role === "super_admin";

  if (!profile) {
    return <p className="p-6 text-sm text-[var(--text-muted)]">Loading…</p>;
  }

  return (
    <>
      <TopNav title="Internal Documents" profile={profile} />
      <div className="p-6">
        {canUpload && (
          <form onSubmit={handleUpload} className="card mb-6 flex flex-wrap items-center gap-4 p-4">
            <input
              type="file"
              accept=".pdf"
              className="text-sm"
              onChange={(e) => setFile(e.target.files?.[0] ?? null)}
            />
            <button type="submit" className="btn-primary" disabled={!file || uploading}>
              {uploading ? "Uploading…" : "Upload"}
            </button>
          </form>
        )}

        {error && <p className="mb-4 text-sm text-red-400">{error}</p>}

        {loading ? (
          <p className="text-sm text-[var(--text-muted)]">Loading…</p>
        ) : (
          <div className="card divide-y divide-[var(--border)]">
            {docs.map((d) => (
              <div key={d.id} className="flex items-center justify-between px-4 py-3">
                <div>
                  <div className="font-medium">{d.title}</div>
                  <div className="text-xs text-[var(--text-muted)]">
                    {d.originalFileName} · {formatBytes(d.sizeBytes)} · {formatDate(d.uploaded)}
                  </div>
                </div>
              </div>
            ))}
            {docs.length === 0 && (
              <p className="px-4 py-6 text-sm text-[var(--text-muted)]">No internal documents yet.</p>
            )}
          </div>
        )}
      </div>
    </>
  );
}
