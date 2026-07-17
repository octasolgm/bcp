"use client";

import { useCallback, useEffect, useMemo, useState } from "react";
import Link from "next/link";
import {
  getActionPlanHistory,
  getResults,
  rerunAllFailedDualVerify,
  rerunDualVerify,
  rerunPoint,
  resubmitForReview,
  submitForReview,
  updateActionPlan,
} from "@/lib/api/bcp-api-client";
import { getClientToken } from "@/lib/auth/client-token";
import { StatusBadge } from "@/components/ui/StatusBadge";
import { exportResultsExcel } from "@/lib/export/export-excel";
import { exportResultsPdf } from "@/lib/export/export-pdf";
import type { ActionPlanHistoryEntry, AnalysisPoint, ResultsData } from "@/lib/types";
import { formatDate, parsePointSnapshot } from "@/lib/utils";
import type { UserProfile } from "@/lib/api/bcp-api-client";

export function ResultsView({
  runId,
  profile,
  readOnly = false,
}: {
  runId: string;
  profile: UserProfile;
  readOnly?: boolean;
}) {
  const [data, setData] = useState<ResultsData | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");
  const [statusFilter, setStatusFilter] = useState("all");
  const [search, setSearch] = useState("");
  const [editingPointId, setEditingPointId] = useState<string | null>(null);
  const [editContent, setEditContent] = useState("");
  const [historyOpen, setHistoryOpen] = useState(false);
  const [history, setHistory] = useState<ActionPlanHistoryEntry[]>([]);
  const [historyPointId, setHistoryPointId] = useState<string | null>(null);
  const [actionLoading, setActionLoading] = useState(false);

  const load = useCallback(async () => {
    const token = await getClientToken();
    if (!token) return;
    const res = await getResults(token, runId);
    if (res.success && res.data) {
      setData(res.data as ResultsData);
    } else {
      setError(res.message ?? "Failed to load results");
    }
    setLoading(false);
  }, [runId]);

  useEffect(() => {
    load();
  }, [load]);

  const filteredPoints = useMemo(() => {
    if (!data) return [];
    return data.points.filter((p) => {
      if (statusFilter === "dual_verify_failed" && p.dualVerifyStatus !== "failed") return false;
      if (
        statusFilter !== "all" &&
        statusFilter !== "dual_verify_failed" &&
        p.finalStatus !== statusFilter
      )
        return false;
      if (!search.trim()) return true;
      const snap = parsePointSnapshot(p.pointSnapshot);
      const hay = `${snap.pointNumber ?? ""} ${snap.pointTitle ?? ""} ${snap.pointContent ?? ""}`.toLowerCase();
      return hay.includes(search.toLowerCase());
    });
  }, [data, statusFilter, search]);

  async function saveActionPlan(pointId: string) {
    const token = await getClientToken();
    if (!token) return;
    const res = await updateActionPlan(token, runId, pointId, editContent);
    if (res.success) {
      setEditingPointId(null);
      await load();
    } else {
      setError(res.message ?? "Failed to save");
    }
  }

  async function openHistory(pointId: string) {
    const token = await getClientToken();
    if (!token) return;
    const res = await getActionPlanHistory(token, runId, pointId);
    if (res.success && res.data) {
      setHistory(res.data as ActionPlanHistoryEntry[]);
      setHistoryPointId(pointId);
      setHistoryOpen(true);
    }
  }

  async function handleSubmitReview() {
    setActionLoading(true);
    const token = await getClientToken();
    if (!token) return;
    const fn =
      data?.run.status === "pulled_back" ? resubmitForReview : submitForReview;
    const res = await fn(token, runId);
    if (res.success) await load();
    else setError(res.message ?? "Failed to submit");
    setActionLoading(false);
  }

  async function handleRerun(pointId: string, dualOnly = false) {
    const token = await getClientToken();
    if (!token) return;
    const res = dualOnly
      ? await rerunDualVerify(token, runId, pointId)
      : await rerunPoint(token, runId, pointId);
    if (!res.success) setError(res.message ?? "Rerun failed");
    else await load();
  }

  async function useHistoryVersion(versionNumber: number, content: string) {
    if (!historyPointId) return;
    const token = await getClientToken();
    if (!token) return;
    const res = await updateActionPlan(token, runId, historyPointId, content, versionNumber);
    if (res.success) {
      setHistoryOpen(false);
      await load();
    } else {
      setError(res.message ?? "Failed to restore version");
    }
  }

  async function handleRerunAllFailed() {
    const token = await getClientToken();
    if (!token) return;
    const res = await rerunAllFailedDualVerify(token, runId);
    if (!res.success) setError(res.message ?? "Rerun failed");
    else await load();
  }

  const canExport =
    data &&
    ["completed", "dual_verify_failed", "landing_ai_complete", "submitted_for_review", "checker_approved", "reviewer_approved", "pulled_back"].includes(
      data.run.status,
    );

  const pulledBackReview = data?.reviews
    .filter((r) => r.action === "pulled_back" && r.reviewerRole === "checker")
    .sort((a, b) => new Date(b.createdAt).getTime() - new Date(a.createdAt).getTime())[0];

  const canEdit = Boolean(
    !readOnly &&
      (profile.role === "maker" || profile.role === "super_admin") &&
      data &&
      !["submitted_for_review", "checker_approved", "reviewer_approved"].includes(data.run.status),
  );

  const canSubmit =
    canEdit &&
    data &&
    ["completed", "dual_verify_failed", "landing_ai_complete"].includes(data.run.status);

  if (loading) {
    return <p className="p-6 text-sm text-[var(--text-muted)]">Loading results…</p>;
  }

  if (!data) {
    return <p className="p-6 text-sm text-red-400">{error || "No data"}</p>;
  }

  return (
    <div className="flex flex-1 flex-col">
      <div className="border-b border-[var(--border)] px-6 py-4">
        {data.run.status === "pulled_back" && pulledBackReview && (
          <div className="mb-4 rounded-lg border border-amber-500/40 bg-amber-500/10 p-4 text-sm">
            <div className="font-medium text-amber-200">Pulled back by checker</div>
            <p className="mt-1 text-[var(--text-muted)]">
              {formatDate(pulledBackReview.createdAt)}
            </p>
            {pulledBackReview.overallComment && (
              <p className="mt-2 whitespace-pre-wrap">{pulledBackReview.overallComment}</p>
            )}
          </div>
        )}
        <div className="flex flex-wrap items-start justify-between gap-4">
          <div>
            <h2 className="text-lg font-semibold">{data.run.name}</h2>
            <p className="text-sm text-[var(--text-muted)]">
              {data.run.createdByName ?? "—"} · {formatDate(data.run.createdAt)}
            </p>
            <div className="mt-2">
              <StatusBadge status={data.run.status} />
            </div>
          </div>
          <div className="flex flex-wrap gap-2">
            <button
              type="button"
              className="btn-secondary text-sm"
              disabled={!canExport}
              onClick={() => exportResultsPdf(data)}
            >
              Export PDF
            </button>
            <button
              type="button"
              className="btn-secondary text-sm"
              disabled={!canExport}
              onClick={() => exportResultsExcel(data)}
            >
              Export Excel
            </button>
            {canSubmit && (
              <button
                type="button"
                className="btn-primary text-sm"
                disabled={actionLoading}
                onClick={handleSubmitReview}
              >
                Submit for review
              </button>
            )}
            {canEdit && data.run.dualVerifyFailedCount > 0 && (
              <button type="button" className="btn-secondary text-sm" onClick={handleRerunAllFailed}>
                Rerun failed dual-verify
              </button>
            )}
            <Link href={`/run-analysis/${runId}`} className="btn-secondary text-sm">
              Progress
            </Link>
          </div>
        </div>

        <div className="mt-4 flex flex-wrap gap-3">
          <input
            className="input max-w-xs"
            placeholder="Search points…"
            value={search}
            onChange={(e) => setSearch(e.target.value)}
          />
          <select
            className="input max-w-xs"
            value={statusFilter}
            onChange={(e) => setStatusFilter(e.target.value)}
          >
            <option value="all">All statuses</option>
            <option value="compliant">Compliant</option>
            <option value="partial_compliant">Partial</option>
            <option value="non_compliant">Non-compliant</option>
            <option value="dual_verify_failed">Dual verify failed</option>
          </select>
        </div>
      </div>

      <div className="space-y-4 p-6">
        {filteredPoints.map((p) => (
          <PointCard
            key={p.id}
            point={p}
            comments={data.comments.filter((c) => c.analysisPointId === p.id)}
            canEdit={canEdit}
            editing={editingPointId === p.id}
            editContent={editContent}
            onEditStart={() => {
              setEditingPointId(p.id);
              setEditContent(p.finalActionPlan ?? p.originalAiActionPlan ?? "");
            }}
            onEditChange={setEditContent}
            onEditSave={() => saveActionPlan(p.id)}
            onEditCancel={() => setEditingPointId(null)}
            onHistory={() => openHistory(p.id)}
            onRerun={() => handleRerun(p.id)}
            onRerunDual={() => handleRerun(p.id, true)}
          />
        ))}
        {filteredPoints.length === 0 && (
          <p className="text-sm text-[var(--text-muted)]">No points match filters.</p>
        )}
      </div>

      {error && <p className="px-6 pb-4 text-sm text-red-400">{error}</p>}

      {historyOpen && (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/60 p-4">
          <div className="card max-h-[80vh] w-full max-w-lg overflow-y-auto p-6">
            <div className="mb-4 flex items-center justify-between">
              <h3 className="font-semibold">Action plan history</h3>
              <button type="button" className="btn-secondary text-sm" onClick={() => setHistoryOpen(false)}>
                Close
              </button>
            </div>
            <div className="space-y-3">
              {history.map((h) => (
                <div key={h.id} className="rounded-lg border border-[var(--border)] p-3 text-sm">
                  <div className="flex flex-wrap items-center justify-between gap-2 text-xs text-[var(--text-muted)]">
                    <span>
                      v{h.versionNumber} · {h.changeType}
                      {h.isCurrent && " · current"}
                    </span>
                    <span>
                      {h.changedByName ?? "—"} · {formatDate(h.createdAt)}
                    </span>
                  </div>
                  <p className="mt-2 whitespace-pre-wrap">{h.actionPlanContent}</p>
                  {canEdit && !h.isCurrent && (
                    <button
                      type="button"
                      className="btn-secondary mt-2 text-xs"
                      onClick={() => useHistoryVersion(h.versionNumber, h.actionPlanContent)}
                    >
                      Use this version
                    </button>
                  )}
                </div>
              ))}
              {history.length === 0 && (
                <p className="text-sm text-[var(--text-muted)]">No history</p>
              )}
            </div>
          </div>
        </div>
      )}
    </div>
  );
}

function PointCard({
  point,
  comments,
  canEdit,
  editing,
  editContent,
  onEditStart,
  onEditChange,
  onEditSave,
  onEditCancel,
  onHistory,
  onRerun,
  onRerunDual,
}: {
  point: AnalysisPoint;
  comments: { comment: string; createdAt: string }[];
  canEdit: boolean;
  editing: boolean;
  editContent: string;
  onEditStart: () => void;
  onEditChange: (v: string) => void;
  onEditSave: () => void;
  onEditCancel: () => void;
  onHistory: () => void;
  onRerun: () => void;
  onRerunDual: () => void;
}) {
  const snap = parsePointSnapshot(point.pointSnapshot);
  const actionPlan = point.finalActionPlan ?? point.originalAiActionPlan;

  return (
    <div className="card p-4">
      <div className="flex flex-wrap items-start justify-between gap-2">
        <div>
          <div className="font-medium">
            {snap.pointNumber}
            {snap.pointTitle ? ` — ${snap.pointTitle}` : ""}
          </div>
          <p className="mt-1 text-sm text-[var(--text-muted)]">{snap.pointContent}</p>
        </div>
        {point.finalStatus && <StatusBadge status={point.finalStatus} />}
        {point.dualVerifyStatus === "failed" && (
          <StatusBadge status="dual_verify_failed" />
        )}
      </div>

      {point.landingAiResult && (
        <div className="mt-3 rounded-lg bg-[var(--bg-input)] p-3 text-sm">
          <div className="text-xs font-medium text-[var(--text-muted)]">Landing AI result</div>
          <p className="mt-1 whitespace-pre-wrap">{point.landingAiResult}</p>
        </div>
      )}

      {point.googleAiResult && (
        <div className="mt-3 rounded-lg bg-[var(--bg-input)] p-3 text-sm">
          <div className="text-xs font-medium text-[var(--text-muted)]">Google AI result</div>
          <p className="mt-1 whitespace-pre-wrap">{point.googleAiResult}</p>
        </div>
      )}

      {!point.googleAiResult && point.googleAiError && (
        <div className="mt-3 rounded-lg bg-red-500/10 p-3 text-sm text-red-300">
          Google AI: {point.googleAiError}
        </div>
      )}

      <div className="mt-3">
        <div className="mb-1 flex items-center justify-between">
          <span className="text-xs font-medium text-[var(--text-muted)]">Action plan</span>
          <div className="flex gap-2">
            <button type="button" className="btn-secondary px-2 py-1 text-xs" onClick={onHistory}>
              History
            </button>
            {canEdit && !editing && (
              <button type="button" className="btn-secondary px-2 py-1 text-xs" onClick={onEditStart}>
                Edit
              </button>
            )}
          </div>
        </div>
        {editing ? (
          <div className="space-y-2">
            <textarea
              className="input min-h-24"
              value={editContent}
              onChange={(e) => onEditChange(e.target.value)}
            />
            <div className="flex gap-2">
              <button type="button" className="btn-primary text-sm" onClick={onEditSave}>
                Save
              </button>
              <button type="button" className="btn-secondary text-sm" onClick={onEditCancel}>
                Cancel
              </button>
            </div>
          </div>
        ) : (
          <p className="whitespace-pre-wrap text-sm">{actionPlan || "—"}</p>
        )}
      </div>

      {comments.length > 0 && (
        <div className="mt-3 border-t border-[var(--border)] pt-3">
          <div className="text-xs font-medium text-[var(--text-muted)]">Review comments</div>
          {comments.map((c, i) => (
            <p key={i} className="mt-1 text-sm text-amber-200/90">
              {c.comment}
            </p>
          ))}
        </div>
      )}

      {canEdit && (
        <div className="mt-3 flex gap-2">
          <button type="button" className="btn-secondary text-xs" onClick={onRerun}>
            Rerun point
          </button>
          <button type="button" className="btn-secondary text-xs" onClick={onRerunDual}>
            Rerun dual-verify
          </button>
        </div>
      )}
    </div>
  );
}
