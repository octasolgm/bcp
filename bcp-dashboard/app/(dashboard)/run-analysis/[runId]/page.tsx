"use client";

import { use, useEffect, useState } from "react";
import Link from "next/link";
import { TopNav } from "@/components/shell/TopNav";
import { StatusBadge } from "@/components/ui/StatusBadge";
import {
  getAnalysisRun,
  getAnalysisRunStatus,
  getProfile,
  startAnalysisRun,
} from "@/lib/api/bcp-api-client";
import type { UserProfile } from "@/lib/api/bcp-api-client";
import { getClientToken } from "@/lib/auth/client-token";
import type { AnalysisPoint } from "@/lib/types";
import { parsePointSnapshot } from "@/lib/utils";

type RunStatus = {
  id: string;
  status: string;
  totalPointsCount: number;
  processedPointsCount: number;
  landingAiCompletedCount: number;
  dualVerifyCompletedCount: number;
  dualVerifyFailedCount: number;
  points: AnalysisPoint[];
};

type RunDetail = {
  run: {
    id: string;
    name: string;
    status: string;
    selectedPointsSnapshot: string;
    selectedInternalDocIds: string;
    selectedRegulationDocIds: string;
    totalPointsCount: number;
    processedPointsCount: number;
  };
  points: AnalysisPoint[];
};

function parseJsonArray(value: string | undefined): unknown[] {
  if (!value) return [];
  try {
    const parsed = JSON.parse(value);
    return Array.isArray(parsed) ? parsed : [];
  } catch {
    return [];
  }
}

export default function RunAnalysisProgressPage({
  params,
}: {
  params: Promise<{ runId: string }>;
}) {
  const { runId } = use(params);
  const [profile, setProfile] = useState<UserProfile | null>(null);
  const [detail, setDetail] = useState<RunDetail | null>(null);
  const [status, setStatus] = useState<RunStatus | null>(null);
  const [error, setError] = useState("");
  const [resuming, setResuming] = useState(false);

  useEffect(() => {
    (async () => {
      const token = await getClientToken();
      if (!token) return;
      const [profRes, runRes] = await Promise.all([
        getProfile(token),
        getAnalysisRun(token, runId),
      ]);
      if (profRes.success && profRes.data) setProfile(profRes.data);
      if (runRes.success && runRes.data) {
        setDetail(runRes.data as RunDetail);
      } else {
        setError(runRes.message ?? "Failed to load analysis run");
      }
    })();
  }, [runId]);

  useEffect(() => {
    let active = true;

    async function poll() {
      const token = await getClientToken();
      if (!token || !active) return;
      const res = await getAnalysisRunStatus(token, runId);
      if (res.success && res.data) {
        setStatus(res.data as RunStatus);
      }
    }

    poll();
    const interval = setInterval(poll, 2000);
    return () => {
      active = false;
      clearInterval(interval);
    };
  }, [runId]);

  async function handleResume() {
    setResuming(true);
    setError("");
    const token = await getClientToken();
    if (!token) return;
    const res = await startAnalysisRun(token, runId);
    if (!res.success) {
      setError(res.message ?? "Failed to start analysis");
    }
    setResuming(false);
  }

  if (!profile) {
    return <p className="p-6 text-sm text-[var(--text-muted)]">Loading…</p>;
  }

  const currentStatus = status?.status ?? detail?.run.status ?? "draft";
  const totalPoints = status?.totalPointsCount ?? detail?.run.totalPointsCount ?? 0;
  const processedPoints =
    status?.processedPointsCount ?? detail?.run.processedPointsCount ?? 0;
  const progress = totalPoints > 0 ? Math.round((processedPoints / totalPoints) * 100) : 0;

  const isDone = ["completed", "dual_verify_failed", "landing_ai_complete"].includes(
    currentStatus,
  );
  const canResume = ["draft", "running"].includes(currentStatus);

  const snapshotPoints = parseJsonArray(detail?.run.selectedPointsSnapshot);
  const internalDocIds = parseJsonArray(detail?.run.selectedInternalDocIds) as string[];
  const regulationDocIds = parseJsonArray(detail?.run.selectedRegulationDocIds) as string[];
  const displayPoints = status?.points ?? detail?.points ?? [];

  return (
    <>
      <TopNav title={detail?.run.name ?? "Analysis Progress"} profile={profile} />
      <div className="p-6">
        <div className="mb-4 flex flex-wrap items-center gap-3">
          <StatusBadge status={currentStatus} />
          <span className="text-sm text-[var(--text-muted)]">
            {processedPoints} / {totalPoints} points
          </span>
          {canResume && (
            <button
              type="button"
              className="btn-primary text-sm"
              disabled={resuming || currentStatus === "running"}
              onClick={handleResume}
            >
              {currentStatus === "running" ? "Running…" : resuming ? "Starting…" : "Resume"}
            </button>
          )}
        </div>

        {detail && (
          <div className="card mb-6 p-4 text-sm">
            <h3 className="mb-2 font-medium">Selected configuration</h3>
            <p className="text-[var(--text-muted)]">
              {snapshotPoints.length} regulation points · {internalDocIds.length} internal
              document(s) · {regulationDocIds.length} regulation document(s)
            </p>
          </div>
        )}

        {status && (
          <div className="card mb-6 p-4">
            <div className="mb-2 flex justify-between text-sm">
              <span>Overall progress</span>
              <span>{progress}%</span>
            </div>
            <div className="h-2 overflow-hidden rounded-full bg-[var(--bg-input)]">
              <div
                className="h-full bg-[var(--accent)] transition-all"
                style={{ width: `${progress}%` }}
              />
            </div>
            <div className="mt-3 grid grid-cols-3 gap-4 text-center text-sm">
              <div>
                <div className="text-lg font-semibold">{status.landingAiCompletedCount}</div>
                <div className="text-xs text-[var(--text-muted)]">Landing AI</div>
              </div>
              <div>
                <div className="text-lg font-semibold">{status.dualVerifyCompletedCount}</div>
                <div className="text-xs text-[var(--text-muted)]">Dual verify</div>
              </div>
              <div>
                <div className="text-lg font-semibold text-red-400">
                  {status.dualVerifyFailedCount}
                </div>
                <div className="text-xs text-[var(--text-muted)]">Failed</div>
              </div>
            </div>
          </div>
        )}

        <div className="space-y-2">
          {displayPoints.map((p) => {
            const snap = parsePointSnapshot(p.pointSnapshot);
            return (
              <div
                key={p.id}
                className="card flex flex-wrap items-center justify-between gap-2 p-3 text-sm"
              >
                <span>
                  {snap.pointNumber}
                  {snap.pointTitle ? ` — ${snap.pointTitle}` : ""}
                </span>
                <div className="flex flex-wrap gap-2">
                  <StatusBadge status={p.landingAiStatus} />
                  <StatusBadge status={p.dualVerifyStatus} />
                  {p.finalStatus && <StatusBadge status={p.finalStatus} />}
                </div>
              </div>
            );
          })}
          {displayPoints.length === 0 && (
            <p className="text-sm text-[var(--text-muted)]">No points loaded.</p>
          )}
        </div>

        {isDone && (
          <div className="mt-6">
            <Link href={`/results/${runId}`} className="btn-primary inline-block">
              View results
            </Link>
          </div>
        )}

        {error && <p className="mt-4 text-sm text-red-400">{error}</p>}
      </div>
    </>
  );
}
