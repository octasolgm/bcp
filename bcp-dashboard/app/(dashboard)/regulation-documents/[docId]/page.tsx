"use client";

import { use, useEffect, useState } from "react";
import Link from "next/link";
import { TopNav } from "@/components/shell/TopNav";
import { StatusBadge } from "@/components/ui/StatusBadge";
import {
  getDocumentPoints,
  getProfile,
  getRegulationDocument,
} from "@/lib/api/bcp-api-client";
import { getClientToken } from "@/lib/auth/client-token";
import type { UserProfile } from "@/lib/api/bcp-api-client";
import type { RegulationDocument, RegulationPoint } from "@/lib/types";

const PAGE_SIZE = 15;

export default function RegulationDocumentDetailPage({
  params,
}: {
  params: Promise<{ docId: string }>;
}) {
  const { docId } = use(params);
  const [profile, setProfile] = useState<UserProfile | null>(null);
  const [doc, setDoc] = useState<RegulationDocument | null>(null);
  const [points, setPoints] = useState<RegulationPoint[]>([]);
  const [page, setPage] = useState(0);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    (async () => {
      const token = await getClientToken();
      if (!token) return;
      const [profRes, docRes, ptsRes] = await Promise.all([
        getProfile(token),
        getRegulationDocument(token, docId),
        getDocumentPoints(token, docId),
      ]);
      if (profRes.success && profRes.data) setProfile(profRes.data);
      if (docRes.success && docRes.data) setDoc(docRes.data as RegulationDocument);
      if (ptsRes.success && ptsRes.data) setPoints(ptsRes.data as RegulationPoint[]);
      setLoading(false);
    })();
  }, [docId]);

  const totalPages = Math.ceil(points.length / PAGE_SIZE);
  const pagePoints = points.slice(page * PAGE_SIZE, (page + 1) * PAGE_SIZE);

  if (!profile || loading) {
    return <p className="p-6 text-sm text-[var(--text-muted)]">Loading…</p>;
  }

  return (
    <>
      <TopNav title={doc?.name ?? "Regulation Document"} profile={profile} />
      <div className="p-6">
        <Link href="/regulation-documents" className="text-sm text-[var(--accent)]">
          ← Back to documents
        </Link>

        {doc && (
          <div className="mt-4 flex flex-wrap items-center gap-3">
            <StatusBadge status={doc.extractionStatus} />
            <span className="text-sm text-[var(--text-muted)]">{doc.pointCount} points</span>
          </div>
        )}

        <div className="mt-6 space-y-3">
          {pagePoints.map((pt) => (
            <div key={pt.id} className="card p-4">
              <div className="font-medium">
                {pt.pointNumber}
                {pt.pointTitle ? ` — ${pt.pointTitle}` : ""}
              </div>
              {pt.pageReference && (
                <div className="text-xs text-[var(--text-muted)]">Page: {pt.pageReference}</div>
              )}
              <p className="mt-2 text-sm whitespace-pre-wrap">{pt.pointContent}</p>
            </div>
          ))}
        </div>

        {totalPages > 1 && (
          <div className="mt-6 flex items-center justify-center gap-4">
            <button
              type="button"
              className="btn-secondary"
              disabled={page === 0}
              onClick={() => setPage((p) => p - 1)}
            >
              Previous
            </button>
            <span className="text-sm text-[var(--text-muted)]">
              Page {page + 1} of {totalPages}
            </span>
            <button
              type="button"
              className="btn-secondary"
              disabled={page >= totalPages - 1}
              onClick={() => setPage((p) => p + 1)}
            >
              Next
            </button>
          </div>
        )}
      </div>
    </>
  );
}
