import Link from "next/link";
import { TopNav } from "@/components/shell/TopNav";
import { StatusBadge } from "@/components/ui/StatusBadge";
import {
  getAnalysisRuns,
  getCheckerHistory,
  getCheckerQueue,
  getDepartments,
  getInternalDocuments,
  getLibraries,
  getRegulationDocuments,
  getReviewerHistory,
  getReviewerQueue,
  getUsers,
} from "@/lib/api/bcp-api-client";
import { getServerProfile, getSessionToken } from "@/lib/auth/helpers";
import type { AnalysisRunSummary } from "@/lib/types";
import { formatDate } from "@/lib/utils";

type StatCard = { label: string; value: number | string; href?: string };

export default async function DashboardPage() {
  const profile = await getServerProfile();
  if (!profile) return null;

  const token = await getSessionToken();
  const stats: StatCard[] = [];
  let recentRuns: AnalysisRunSummary[] = [];

  if (token) {
    const role = profile.role;

    if (role === "super_admin") {
      const [depts, users, regs, libs, runs, checkerQ, reviewerQ] = await Promise.all([
        getDepartments(token),
        getUsers(token),
        getRegulationDocuments(token),
        getLibraries(token),
        getAnalysisRuns(token),
        getCheckerQueue(token),
        getReviewerQueue(token),
      ]);
      stats.push(
        { label: "Departments", value: depts.data?.length ?? 0, href: "/admin/departments" },
        { label: "Users", value: users.data?.length ?? 0, href: "/admin/users" },
        { label: "Regulation docs", value: regs.data?.length ?? 0, href: "/regulation-documents" },
        { label: "Libraries", value: libs.data?.length ?? 0, href: "/libraries" },
        { label: "Analysis runs", value: runs.data?.length ?? 0, href: "/run-analysis" },
        { label: "Checker queue", value: checkerQ.data?.length ?? 0, href: "/checker" },
        { label: "Reviewer queue", value: reviewerQ.data?.length ?? 0, href: "/reviewer" },
      );
      recentRuns = (runs.data as AnalysisRunSummary[]) ?? [];
    } else if (role === "maker") {
      const [regs, libs, internal, runs] = await Promise.all([
        getRegulationDocuments(token),
        getLibraries(token, profile.departmentId ?? undefined),
        getInternalDocuments(token),
        getAnalysisRuns(token, { mineOnly: true }),
      ]);
      stats.push(
        { label: "Regulation docs", value: regs.data?.length ?? 0, href: "/regulation-documents" },
        { label: "Internal docs", value: internal.data?.length ?? 0, href: "/internal-documents" },
        { label: "Libraries", value: libs.data?.length ?? 0, href: "/libraries" },
        { label: "My analysis runs", value: runs.data?.length ?? 0, href: "/run-analysis" },
      );
      recentRuns = (runs.data as AnalysisRunSummary[]) ?? [];
    } else if (role === "checker") {
      const [queue, history] = await Promise.all([
        getCheckerQueue(token),
        getCheckerHistory(token),
      ]);
      stats.push(
        { label: "Review queue", value: queue.data?.length ?? 0, href: "/checker" },
        { label: "Review history", value: history.data?.length ?? 0, href: "/checker?history=1" },
      );
      recentRuns = (queue.data as AnalysisRunSummary[]) ?? [];
    } else if (role === "reviewer") {
      const [queue, history] = await Promise.all([
        getReviewerQueue(token),
        getReviewerHistory(token),
      ]);
      stats.push(
        { label: "Final review queue", value: queue.data?.length ?? 0, href: "/reviewer" },
        { label: "Final review history", value: history.data?.length ?? 0, href: "/reviewer?history=1" },
      );
      recentRuns = (queue.data as AnalysisRunSummary[]) ?? [];
    }
  }

  const welcome =
    profile.role === "checker"
      ? "Review compliance analysis submissions."
      : profile.role === "reviewer"
        ? "Finalize approved compliance reviews."
        : "Manage regulation documents, libraries, and compliance analysis.";

  return (
    <>
      <TopNav title="Overview" profile={profile} />
      <div className="p-6">
        <p className="mb-6 text-sm text-[var(--text-muted)]">
          Welcome back, {profile.fullName}. {welcome}
        </p>

        <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4">
          {stats.map((s) => (
            <div key={s.label} className="card p-4">
              <div className="text-sm text-[var(--text-muted)]">{s.label}</div>
              <div className="mt-1 text-2xl font-semibold">{s.value}</div>
              {s.href && (
                <Link href={s.href} className="mt-2 inline-block text-sm text-[var(--accent)]">
                  View →
                </Link>
              )}
            </div>
          ))}
        </div>

        {recentRuns.length > 0 && (
          <div className="mt-8">
            <h2 className="mb-3 font-medium">
              {profile.role === "checker" || profile.role === "reviewer"
                ? "Queue"
                : "Recent analysis runs"}
            </h2>
            <div className="card divide-y divide-[var(--border)]">
              {recentRuns.slice(0, 5).map((run) => (
                <Link
                  key={run.id}
                  href={
                    profile.role === "checker"
                      ? `/checker/review/${run.id}`
                      : profile.role === "reviewer"
                        ? `/reviewer/review/${run.id}`
                        : run.status === "draft" || run.status === "running"
                          ? `/run-analysis/${run.id}`
                          : `/results/${run.id}`
                  }
                  className="flex items-center justify-between px-4 py-3 hover:bg-[var(--bg-input)]"
                >
                  <div>
                    <div className="font-medium">{run.name}</div>
                    <div className="text-xs text-[var(--text-muted)]">
                      {formatDate(run.createdAt)}
                      {run.makerName ? ` · ${run.makerName}` : ""}
                    </div>
                  </div>
                  <StatusBadge status={run.status} />
                </Link>
              ))}
            </div>
          </div>
        )}

        {profile.departmentName && (
          <p className="mt-6 text-xs text-[var(--text-muted)]">
            Department: {profile.departmentName}
          </p>
        )}
      </div>
    </>
  );
}
