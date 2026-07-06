'use client';

import Link from 'next/link';
import { useEffect, useMemo, useState } from 'react';
import {
  Activity,
  CheckCircle2,
  Database,
  Layers,
  Radio,
  Zap,
} from 'lucide-react';
import { Cell, Pie, PieChart, ResponsiveContainer, Tooltip } from 'recharts';
import { AppShell } from '@/components/layout/AppShell';
import { SeverityBadge } from '@/components/ui/SeverityBadge';
import { getDashboard } from '@/lib/api';
import {
  getDualVerifyHealth,
  listComplianceSessions,
  listDualVerifySessions,
  type DualVerifyHealth,
  type DualVerifySessionSummary,
} from '@/lib/dual-verify-api';
import type { BcpwebDashboardMetrics } from '@/types';

type RecentRow = {
  id: string;
  title: string;
  date: string;
  findings: number;
  critical: number;
  high: number;
  href: string;
  kind: 'kafka' | 'sync' | 'demo';
  status?: string;
};

/** Compliance dashboard — Reguliq MIS + live Kafka pipeline */
export default function DashboardPage() {
  const [seed, setSeed] = useState<BcpwebDashboardMetrics | null>(null);
  const [health, setHealth] = useState<DualVerifyHealth | null>(null);
  const [kafkaSessions, setKafkaSessions] = useState<DualVerifySessionSummary[]>([]);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    let cancelled = false;
    void (async () => {
      try {
        const [dash, h, sessions, compliance] = await Promise.all([
          getDashboard(),
          getDualVerifyHealth(),
          listDualVerifySessions(),
          listComplianceSessions('dual-leaf', 10),
        ]);
        if (cancelled) return;
        setSeed(dash);
        setHealth(h);
        const merged = [...sessions];
        for (const c of compliance) {
          if (!merged.some((s) => s.id === c.id)) {
            merged.push({
              id: c.id,
              status: 'saved',
              granularity: c.granularity ?? 'dual-leaf',
              totalPoints: c.comparedPoints,
              completedPoints: c.comparedPoints,
              failedPoints: 0,
              phase2Model: 'saved',
              transport: 'db',
              updatedAt: c.label,
              label: c.label,
            });
          }
        }
        setKafkaSessions(merged);
      } catch (e) {
        console.error(e);
      } finally {
        if (!cancelled) setLoading(false);
      }
    })();
    return () => {
      cancelled = true;
    };
  }, []);

  const pipelineStats = useMemo(() => {
    const completed = kafkaSessions.reduce((n, s) => n + s.completedPoints, 0);
    const failed = kafkaSessions.reduce((n, s) => n + s.failedPoints, 0);
    const active = kafkaSessions.filter(
      (s) => s.status === 'running' || s.status === 'queued',
    ).length;
    return { completed, failed, active, sessionCount: kafkaSessions.length };
  }, [kafkaSessions]);

  const recentRows: RecentRow[] = useMemo(() => {
    const rows: RecentRow[] = kafkaSessions.slice(0, 8).map((s) => ({
      id: s.id,
      title: `Kafka dual verify · ${s.granularity}`,
      date: s.updatedAt.slice(0, 16).replace('T', ' '),
      findings: s.completedPoints,
      critical: s.failedPoints,
      high: 0,
      href: `/dual-verify?session=${encodeURIComponent(s.id)}`,
      kind: 'kafka' as const,
      status: s.status,
    }));
    if (seed) {
      for (const a of seed.recentAnalyses) {
        rows.push({
          id: a.id,
          title: a.title,
          date: a.date,
          findings: a.findings,
          critical: a.critical,
          high: a.high,
          href: `/analyse/report/${a.id}`,
          kind: a.id.includes('demo') ? 'demo' : 'sync',
        });
      }
    }
    return rows;
  }, [kafkaSessions, seed]);

  const persistenceMode = health?.persistence?.mode ?? 'memory';
  const persistenceOk = persistenceMode === 'supabase' || persistenceMode === 'file';

  if (loading || !seed) {
    return (
      <AppShell>
        <p className="text-slate-400">Loading dashboard…</p>
      </AppShell>
    );
  }

  const compliantPct =
    seed.totalFindings > 0
      ? Math.round((seed.compliantItems / seed.totalFindings) * 100)
      : 0;

  return (
    <AppShell>
      <div className="mb-6 flex flex-wrap items-start justify-between gap-4">
        <div>
          <p className="text-xs font-semibold uppercase tracking-wider text-emerald-400">
            Reguliq Compliance
          </p>
          <h1 className="text-2xl font-bold">Compliance Dashboard</h1>
          <p className="text-sm text-slate-400">
            SNB UAE / DIFC Branch · Last analysis: {seed.lastAnalysisDate}
          </p>
        </div>
        <div className="flex flex-wrap gap-2">
          <Link
            href="/dual-verify"
            className="rounded-lg border border-emerald-500/40 bg-emerald-500/10 px-4 py-2 text-sm font-medium text-emerald-300 hover:bg-emerald-500/20"
          >
            Kafka Dual Verify →
          </Link>
          <Link
            href="/analyse"
            className="rounded-lg bg-emerald-500 px-4 py-2 text-sm font-medium text-white hover:bg-emerald-400"
          >
            Sync Analyse →
          </Link>
        </div>
      </div>

      <div className="mb-6 grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
        <PipelineCard
          icon={<Radio className="h-4 w-4 text-emerald-400" />}
          label="Transport"
          value={health?.transport ?? '—'}
          sub={health?.kafkaConfigured ? 'Azure Event Hubs' : 'Local queue'}
          tone="text-emerald-400"
        />
        <PipelineCard
          icon={<Database className="h-4 w-4 text-sky-400" />}
          label="Persistence"
          value={
            persistenceMode === 'supabase'
              ? 'Supabase'
              : persistenceMode === 'file'
                ? 'Disk'
                : 'None'
          }
          sub={persistenceOk ? 'Results saved' : 'Do not run paid jobs'}
          tone={
            persistenceMode === 'memory'
              ? 'text-red-400'
              : persistenceMode === 'supabase'
                ? 'text-emerald-400'
                : 'text-sky-400'
          }
        />
        <PipelineCard
          icon={<Layers className="h-4 w-4 text-violet-400" />}
          label="Dual verify points"
          value={String(pipelineStats.completed)}
          sub={`${pipelineStats.sessionCount} session(s) · ${pipelineStats.failed} failed`}
          tone="text-violet-300"
        />
        <PipelineCard
          icon={<Activity className="h-4 w-4 text-amber-400" />}
          label="Active jobs"
          value={String(pipelineStats.active)}
          sub={pipelineStats.active > 0 ? 'Pipeline running' : 'Idle'}
          tone="text-amber-300"
        />
      </div>

      {health?.persistence?.hint && !persistenceOk && (
        <div className="mb-6 rounded-xl border border-red-500/40 bg-red-500/10 px-4 py-3 text-sm text-red-200">
          {health.persistence.hint}
        </div>
      )}

      <div className="mb-6 grid gap-4 sm:grid-cols-2 xl:grid-cols-4">
        <MetricCard label="Critical Gaps" value={seed.criticalGaps} tone="text-red-400" sub="Immediate action required" />
        <MetricCard label="High Risk" value={seed.highRisk} tone="text-orange-400" sub="Significant exposure" />
        <MetricCard label="Total Findings" value={seed.totalFindings} tone="text-white" sub="Demo + live sessions" />
        <MetricCard label="Compliant Items" value={seed.compliantItems} tone="text-blue-400" sub="Fully addressed" />
      </div>

      <div className="mb-6 grid gap-6 lg:grid-cols-2">
        <div className="rounded-xl border border-white/10 bg-white/5 p-5">
          <h2 className="mb-4 font-semibold">Risk breakdown</h2>
          <div className="flex items-center gap-6">
            <div className="relative h-40 w-40">
              <ResponsiveContainer width="100%" height="100%">
                <PieChart>
                  <Pie data={seed.riskBreakdown} dataKey="value" innerRadius={45} outerRadius={70}>
                    {seed.riskBreakdown.map((entry) => (
                      <Cell key={entry.name} fill={entry.color} />
                    ))}
                  </Pie>
                  <Tooltip />
                </PieChart>
              </ResponsiveContainer>
              <div className="pointer-events-none absolute inset-0 flex items-center justify-center">
                <span className="text-lg font-semibold text-slate-300">{compliantPct}%</span>
              </div>
            </div>
            <div className="space-y-2 text-sm">
              {seed.riskBreakdown.map((r) => (
                <div key={r.name} className="flex items-center gap-2">
                  <span className="h-2 w-2 rounded-full" style={{ background: r.color }} />
                  <span className="text-slate-400">{r.name}</span>
                  <span className="font-medium">{r.value}</span>
                </div>
              ))}
            </div>
          </div>
        </div>

        <div className="rounded-xl border border-white/10 bg-white/5 p-5">
          <div className="mb-4 flex items-center justify-between">
            <h2 className="font-semibold">Kafka pipeline</h2>
            <Link href="/dual-verify" className="text-sm text-emerald-400 hover:underline">
              Open workbench →
            </Link>
          </div>
          <ul className="space-y-2 text-sm">
            {kafkaSessions.length === 0 ? (
              <li className="rounded-lg border border-dashed border-white/10 p-4 text-slate-500">
                No Kafka sessions yet.{' '}
                <Link href="/dual-verify" className="text-emerald-400 hover:underline">
                  Start dual verify
                </Link>
              </li>
            ) : (
              kafkaSessions.slice(0, 5).map((s) => (
                <li key={s.id}>
                  <Link
                    href={`/dual-verify?session=${encodeURIComponent(s.id)}`}
                    className="flex items-center justify-between rounded-lg border border-white/5 bg-black/20 px-3 py-2 hover:bg-white/5"
                  >
                    <div className="min-w-0">
                      <p className="truncate font-medium">{s.label}</p>
                      <p className="text-xs text-slate-500">
                        {s.phase2Model} · {s.transport}
                      </p>
                    </div>
                    <span
                      className={`shrink-0 rounded-full px-2 py-0.5 text-[10px] font-medium uppercase ${
                        s.status === 'completed'
                          ? 'bg-emerald-500/20 text-emerald-400'
                          : s.status === 'failed'
                            ? 'bg-red-500/20 text-red-400'
                            : 'bg-amber-500/20 text-amber-400'
                      }`}
                    >
                      {s.status}
                    </span>
                  </Link>
                </li>
              ))
            )}
          </ul>
          {health && (
            <p className="mt-3 text-xs text-slate-500">
              Topics: {health.topics.jobs} · {health.topics.results}
            </p>
          )}
        </div>
      </div>

      <div className="mb-6 rounded-xl border border-white/10 bg-white/5 p-5">
        <h2 className="mb-4 font-semibold">Remediation tracker</h2>
        <table className="w-full text-sm">
          <thead>
            <tr className="text-left text-slate-500">
              <th className="pb-2">Item</th>
              <th className="pb-2">Severity</th>
              <th className="pb-2">Target</th>
              <th className="pb-2">Status</th>
            </tr>
          </thead>
          <tbody>
            {seed.remediationItems.map((row) => (
              <tr key={row.item} className="border-t border-white/5">
                <td className="py-2">{row.item}</td>
                <td className="py-2">
                  <SeverityBadge severity={row.severity} />
                </td>
                <td className="py-2 text-slate-400">{row.target}</td>
                <td className="py-2 text-slate-400">{row.status}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      <div className="rounded-xl border border-white/10 bg-white/5 p-5">
        <div className="mb-4 flex items-center justify-between">
          <h2 className="font-semibold">Recent analyses</h2>
          <Link href="/dual-verify" className="text-sm text-emerald-400 hover:underline">
            All dual verify →
          </Link>
        </div>
        <div className="space-y-3">
          {recentRows.map((a) => (
            <Link
              key={`${a.kind}-${a.id}`}
              href={a.href}
              className="flex items-center justify-between rounded-lg border border-white/5 bg-black/20 p-3 hover:bg-white/5"
            >
              <div className="flex items-center gap-3">
                <span
                  className={`rounded px-2 py-1 text-xs ${
                    a.kind === 'kafka'
                      ? 'bg-violet-500/20 text-violet-300'
                      : a.kind === 'demo'
                        ? 'bg-red-500/20 text-red-400'
                        : 'bg-sky-500/20 text-sky-300'
                  }`}
                >
                  {a.kind === 'kafka' ? 'KAFKA' : a.kind === 'demo' ? 'DEMO' : 'SYNC'}
                </span>
                <div>
                  <p className="font-medium">{a.title}</p>
                  <p className="text-xs text-slate-500">
                    {a.date} · {a.findings} findings
                    {a.status ? ` · ${a.status}` : ''}
                  </p>
                </div>
              </div>
              <div className="flex gap-2">
                {a.critical > 0 && (
                  <span className="rounded-full bg-red-500/20 px-2 py-0.5 text-xs text-red-400">
                    {a.critical} {a.kind === 'kafka' ? 'Failed' : 'Critical'}
                  </span>
                )}
                {a.high > 0 && (
                  <span className="rounded-full bg-orange-500/20 px-2 py-0.5 text-xs text-orange-400">
                    {a.high} High
                  </span>
                )}
                {a.kind === 'kafka' && a.critical === 0 && (
                  <CheckCircle2 className="h-4 w-4 text-emerald-500" />
                )}
              </div>
            </Link>
          ))}
        </div>
      </div>

      <div className="mt-6 grid gap-4 sm:grid-cols-3">
        <QuickAction
          href="/dual-verify"
          icon={<Zap className="h-5 w-5 text-emerald-400" />}
          title="Kafka Dual Verify"
          desc="Landing AI + Gemini · async · export PDF/Excel"
        />
        <QuickAction
          href="/analyse"
          icon={<Activity className="h-5 w-5 text-sky-400" />}
          title="Sync Analyse"
          desc="Upload regulation + policy · instant Gemini gap report"
        />
        <QuickAction
          href="/reg-library"
          icon={<Layers className="h-5 w-5 text-violet-400" />}
          title="Regulation Library"
          desc="CBUAE TFS guidelines and clause catalogue"
        />
      </div>
    </AppShell>
  );
}

function PipelineCard({
  icon,
  label,
  value,
  sub,
  tone,
}: {
  icon: React.ReactNode;
  label: string;
  value: string;
  sub: string;
  tone: string;
}) {
  return (
    <div className="rounded-xl border border-white/10 bg-gradient-to-br from-white/5 to-transparent p-4">
      <div className="mb-2 flex items-center gap-2 text-xs uppercase tracking-wide text-slate-500">
        {icon}
        {label}
      </div>
      <p className={`text-2xl font-bold capitalize ${tone}`}>{value}</p>
      <p className="mt-1 text-xs text-slate-500">{sub}</p>
    </div>
  );
}

function MetricCard({
  label,
  value,
  tone,
  sub,
}: {
  label: string;
  value: number;
  tone: string;
  sub: string;
}) {
  return (
    <div className="rounded-xl border border-white/10 bg-white/5 p-5">
      <p className="text-xs uppercase tracking-wide text-slate-500">{label}</p>
      <p className={`mt-1 text-4xl font-bold ${tone}`}>{value}</p>
      <p className="mt-1 text-xs text-slate-500">{sub}</p>
    </div>
  );
}

function QuickAction({
  href,
  icon,
  title,
  desc,
}: {
  href: string;
  icon: React.ReactNode;
  title: string;
  desc: string;
}) {
  return (
    <Link
      href={href}
      className="rounded-xl border border-white/10 bg-white/5 p-4 transition hover:border-emerald-500/30 hover:bg-emerald-500/5"
    >
      <div className="mb-2">{icon}</div>
      <p className="font-medium">{title}</p>
      <p className="mt-1 text-xs text-slate-500">{desc}</p>
    </Link>
  );
}
