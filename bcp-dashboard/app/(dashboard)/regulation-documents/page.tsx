"use client";

import { useEffect, useState } from "react";
import Link from "next/link";
import { TopNav } from "@/components/shell/TopNav";
import { StatusBadge } from "@/components/ui/StatusBadge";
import {
  extractRegulationDocument,
  getDepartments,
  getProfile,
  getRegulationDocuments,
  uploadRegulationDocument,
} from "@/lib/api/bcp-api-client";
import { getClientToken } from "@/lib/auth/client-token";
import type { UserProfile } from "@/lib/api/bcp-api-client";
import type { Department, RegulationDocument } from "@/lib/types";
import { formatDate } from "@/lib/utils";

export default function RegulationDocumentsPage() {
  const [profile, setProfile] = useState<UserProfile | null>(null);
  const [docs, setDocs] = useState<RegulationDocument[]>([]);
  const [departments, setDepartments] = useState<Department[]>([]);
  const [deptFilter, setDeptFilter] = useState("");
  const [statusFilter, setStatusFilter] = useState("");
  const [uploadDept, setUploadDept] = useState("");
  const [file, setFile] = useState<File | null>(null);
  const [loading, setLoading] = useState(true);
  const [uploading, setUploading] = useState(false);
  const [extractingId, setExtractingId] = useState<string | null>(null);
  const [error, setError] = useState("");
  const [message, setMessage] = useState("");

  async function load() {
    const token = await getClientToken();
    if (!token) return;
    const [profRes, deptRes, docRes] = await Promise.all([
      getProfile(token),
      getDepartments(token),
      getRegulationDocuments(token, {
        departmentId: deptFilter || undefined,
        status: statusFilter || undefined,
      }),
    ]);
    if (profRes.success && profRes.data) setProfile(profRes.data);
    if (deptRes.success && deptRes.data) setDepartments(deptRes.data as Department[]);
    if (docRes.success && docRes.data) setDocs(docRes.data as RegulationDocument[]);
    setLoading(false);
  }

  useEffect(() => {
    load();
  }, [deptFilter, statusFilter]);

  async function handleUpload(e: React.FormEvent) {
    e.preventDefault();
    if (!file) return;
    setUploading(true);
    setError("");
    const token = await getClientToken();
    if (!token) return;
    const res = await uploadRegulationDocument(token, file, uploadDept || undefined);
    if (res.success) {
      setMessage("Document uploaded");
      setFile(null);
      await load();
    } else {
      setError(res.message ?? "Upload failed");
    }
    setUploading(false);
  }

  async function handleExtract(docId: string) {
    setExtractingId(docId);
    setError("");
    const token = await getClientToken();
    if (!token) return;
    const res = await extractRegulationDocument(token, docId);
    if (res.success) {
      setMessage("Extraction complete");
      await load();
    } else {
      setError(res.message ?? "Extraction failed");
    }
    setExtractingId(null);
  }

  const canUpload = profile?.role === "maker" || profile?.role === "super_admin";

  if (!profile) {
    return <p className="p-6 text-sm text-[var(--text-muted)]">Loading…</p>;
  }

  return (
    <>
      <TopNav title="Regulation Documents" profile={profile} />
      <div className="p-6">
        <div className="mb-6 flex flex-wrap gap-3">
          <select
            className="input max-w-xs"
            value={deptFilter}
            onChange={(e) => setDeptFilter(e.target.value)}
          >
            <option value="">All departments</option>
            {departments.map((d) => (
              <option key={d.id} value={d.id}>
                {d.name}
              </option>
            ))}
          </select>
          <select
            className="input max-w-xs"
            value={statusFilter}
            onChange={(e) => setStatusFilter(e.target.value)}
          >
            <option value="">All statuses</option>
            <option value="pending">Pending</option>
            <option value="processing">Processing</option>
            <option value="completed">Completed</option>
            <option value="failed">Failed</option>
          </select>
        </div>

        {canUpload && (
          <form onSubmit={handleUpload} className="card mb-6 space-y-4 p-4">
            <h2 className="font-medium">Upload document</h2>
            <div className="flex flex-wrap gap-4">
              <input
                type="file"
                accept=".pdf"
                className="text-sm"
                onChange={(e) => setFile(e.target.files?.[0] ?? null)}
              />
              {profile.role === "super_admin" && (
                <select
                  className="input max-w-xs"
                  value={uploadDept}
                  onChange={(e) => setUploadDept(e.target.value)}
                >
                  <option value="">Default department</option>
                  {departments.map((d) => (
                    <option key={d.id} value={d.id}>
                      {d.name}
                    </option>
                  ))}
                </select>
              )}
              <button type="submit" className="btn-primary" disabled={!file || uploading}>
                {uploading ? "Uploading…" : "Upload"}
              </button>
            </div>
          </form>
        )}

        {message && <p className="mb-4 text-sm text-green-400">{message}</p>}
        {error && <p className="mb-4 text-sm text-red-400">{error}</p>}

        {loading ? (
          <p className="text-sm text-[var(--text-muted)]">Loading…</p>
        ) : (
          <div className="card divide-y divide-[var(--border)]">
            {docs.map((d) => (
              <div key={d.id} className="flex flex-wrap items-center justify-between gap-4 px-4 py-3">
                <div>
                  <Link href={`/regulation-documents/${d.id}`} className="font-medium hover:text-[var(--accent)]">
                    {d.name}
                  </Link>
                  <div className="text-xs text-[var(--text-muted)]">
                    {d.pointCount} points · {formatDate(d.createdAt)}
                  </div>
                </div>
                <div className="flex items-center gap-3">
                  <StatusBadge status={d.extractionStatus} />
                  {canUpload && d.extractionStatus !== "completed" && (
                    <button
                      type="button"
                      className="btn-secondary text-sm"
                      disabled={extractingId === d.id}
                      onClick={() => handleExtract(d.id)}
                    >
                      {extractingId === d.id ? "Extracting…" : "Extract now"}
                    </button>
                  )}
                </div>
              </div>
            ))}
            {docs.length === 0 && (
              <p className="px-4 py-6 text-sm text-[var(--text-muted)]">No documents found.</p>
            )}
          </div>
        )}
      </div>
    </>
  );
}
