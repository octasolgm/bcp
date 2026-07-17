"use client";

import { useEffect, useState } from "react";
import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { TopNav } from "@/components/shell/TopNav";
import { StatusBadge } from "@/components/ui/StatusBadge";
import {
  getProfile,
  getReviewerHistory,
  getReviewerQueue,
} from "@/lib/api/bcp-api-client";
import type { UserProfile } from "@/lib/api/bcp-api-client";
import { getClientToken } from "@/lib/auth/client-token";
import type { AnalysisRunSummary } from "@/lib/types";
import { formatDate } from "@/lib/utils";

export default function ReviewerPage() {
  const searchParams = useSearchParams();
  const showHistory = searchParams.get("history") === "1";
  const [profile, setProfile] = useState<UserProfile | null>(null);
  const [runs, setRuns] = useState<AnalysisRunSummary[]>([]);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    (async () => {
      const token = await getClientToken();
      if (!token) return;
      const profRes = await getProfile(token);
      if (profRes.success && profRes.data) setProfile(profRes.data);
      const res = showHistory
        ? await getReviewerHistory(token)
        : await getReviewerQueue(token);
      if (res.success && res.data) setRuns(res.data as AnalysisRunSummary[]);
      setLoading(false);
    })();
  }, [showHistory]);

  if (!profile) {
    return <p className="p-6 text-sm text-[var(--text-muted)]">Loading…</p>;
  }

  return (
    <>
      <TopNav title={showHistory ? "Final Review History" : "Final Review Queue"} profile={profile} />
      <div className="p-6">
        <div className="mb-4 flex gap-4 text-sm">
          <Link
            href="/reviewer"
            className={!showHistory ? "text-[var(--accent)]" : "text-[var(--text-muted)]"}
          >
            Queue
          </Link>
          <Link
            href="/reviewer?history=1"
            className={showHistory ? "text-[var(--accent)]" : "text-[var(--text-muted)]"}
          >
            History
          </Link>
        </div>

        {loading ? (
          <p className="text-sm text-[var(--text-muted)]">Loading…</p>
        ) : (
          <div className="card divide-y divide-[var(--border)]">
            {runs.map((run) => (
              <Link
                key={run.id}
                href={`/reviewer/review/${run.id}`}
                className="flex flex-wrap items-center justify-between gap-4 px-4 py-3 hover:bg-[var(--bg-input)]"
              >
                <div>
                  <div className="font-medium">{run.name}</div>
                  <div className="text-xs text-[var(--text-muted)]">
                    {formatDate(run.createdAt)}
                  </div>
                </div>
                <StatusBadge status={run.status} />
              </Link>
            ))}
            {runs.length === 0 && (
              <p className="px-4 py-6 text-sm text-[var(--text-muted)]">No items.</p>
            )}
          </div>
        )}
      </div>
    </>
  );
}
