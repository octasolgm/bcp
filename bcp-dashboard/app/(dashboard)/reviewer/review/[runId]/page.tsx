"use client";

import { use, useEffect, useState } from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { TopNav } from "@/components/shell/TopNav";
import { StatusBadge } from "@/components/ui/StatusBadge";
import {
  finalizeAnalysis,
  getProfile,
  getResults,
  pullBackToChecker,
} from "@/lib/api/bcp-api-client";
import type { UserProfile } from "@/lib/api/bcp-api-client";
import { getClientToken } from "@/lib/auth/client-token";
import type { AnalysisPoint, ResultsData } from "@/lib/types";
import { parsePointSnapshot } from "@/lib/utils";

export default function ReviewerReviewPage({
  params,
}: {
  params: Promise<{ runId: string }>;
}) {
  const { runId } = use(params);
  const router = useRouter();
  const [profile, setProfile] = useState<UserProfile | null>(null);
  const [data, setData] = useState<ResultsData | null>(null);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [overallComment, setOverallComment] = useState("");
  const [loading, setLoading] = useState(true);
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState("");

  useEffect(() => {
    (async () => {
      const token = await getClientToken();
      if (!token) return;
      const [profRes, resRes] = await Promise.all([
        getProfile(token),
        getResults(token, runId),
      ]);
      if (profRes.success && profRes.data) setProfile(profRes.data);
      if (resRes.success && resRes.data) {
        const d = resRes.data as ResultsData;
        setData(d);
        if (d.points.length > 0) setSelectedId(d.points[0].id);
      }
      setLoading(false);
    })();
  }, [runId]);

  const selected = data?.points.find((p) => p.id === selectedId) ?? null;

  async function handleFinalize() {
    setSubmitting(true);
    setError("");
    const token = await getClientToken();
    if (!token) return;
    const res = await finalizeAnalysis(token, runId, {
      overallComment: overallComment.trim() || undefined,
    });
    if (res.success) router.push("/reviewer");
    else setError(res.message ?? "Failed to finalize");
    setSubmitting(false);
  }

  async function handlePullBack() {
    if (!overallComment.trim()) {
      setError("Comment required to pull back");
      return;
    }
    setSubmitting(true);
    const token = await getClientToken();
    if (!token) return;
    const res = await pullBackToChecker(token, runId, {
      overallComment: overallComment.trim(),
    });
    if (res.success) router.push("/reviewer");
    else setError(res.message ?? "Failed to pull back");
    setSubmitting(false);
  }

  if (!profile || loading) {
    return <p className="p-6 text-sm text-[var(--text-muted)]">Loading…</p>;
  }

  return (
    <>
      <TopNav title={`Final Review: ${data?.run.name ?? ""}`} profile={profile} />
      <div className="border-b border-[var(--border)] px-6 py-2">
        <Link href="/reviewer" className="text-sm text-[var(--accent)]">
          ← Back to queue
        </Link>
      </div>

      <div className="grid flex-1 grid-cols-1 lg:grid-cols-2">
        <div className="border-r border-[var(--border)] p-4">
          <h3 className="mb-3 text-sm font-medium">Points</h3>
          <div className="max-h-[calc(100vh-16rem)] space-y-2 overflow-y-auto">
            {data?.points.map((p) => {
              const snap = parsePointSnapshot(p.pointSnapshot);
              return (
                <button
                  key={p.id}
                  type="button"
                  onClick={() => setSelectedId(p.id)}
                  className={`w-full rounded-lg border p-3 text-left text-sm ${
                    p.id === selectedId
                      ? "border-[var(--accent)] bg-[var(--bg-card)]"
                      : "border-[var(--border)]"
                  }`}
                >
                  <div className="flex items-center justify-between gap-2">
                    <span className="font-medium">{snap.pointNumber}</span>
                    {p.finalStatus && <StatusBadge status={p.finalStatus} />}
                  </div>
                </button>
              );
            })}
          </div>
        </div>

        <div className="flex flex-col p-4">
          {selected && (
            <div className="space-y-4">
              {(() => {
                const snap = parsePointSnapshot(selected.pointSnapshot);
                const plan = selected.finalActionPlan ?? selected.originalAiActionPlan;
                return (
                  <>
                    <h3 className="font-medium">
                      {snap.pointNumber}
                      {snap.pointTitle ? ` — ${snap.pointTitle}` : ""}
                    </h3>
                    <p className="text-sm text-[var(--text-muted)]">{snap.pointContent}</p>
                    {selected.landingAiResult && (
                      <div className="rounded-lg bg-[var(--bg-input)] p-3 text-sm">
                        <p className="whitespace-pre-wrap">{selected.landingAiResult}</p>
                      </div>
                    )}
                    {plan && (
                      <div className="text-sm">
                        <div className="text-xs text-[var(--text-muted)]">Action plan</div>
                        <p className="mt-1 whitespace-pre-wrap">{plan}</p>
                      </div>
                    )}
                  </>
                );
              })()}
            </div>
          )}

          <div className="mt-auto space-y-4 border-t border-[var(--border)] pt-4">
            <label className="block text-sm">
              Overall comment
              <textarea
                className="input mt-1 min-h-20"
                value={overallComment}
                onChange={(e) => setOverallComment(e.target.value)}
              />
            </label>
            {error && <p className="text-sm text-red-400">{error}</p>}
            <div className="flex gap-3">
              <button
                type="button"
                className="btn-primary"
                disabled={submitting}
                onClick={handleFinalize}
              >
                Finalize
              </button>
              <button
                type="button"
                className="btn-secondary"
                disabled={submitting}
                onClick={handlePullBack}
              >
                Pull back to checker
              </button>
              <Link href={`/results/${runId}`} className="btn-secondary">
                Full results
              </Link>
            </div>
          </div>
        </div>
      </div>
    </>
  );
}
