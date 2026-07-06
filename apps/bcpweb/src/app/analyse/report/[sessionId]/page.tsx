'use client';

import { useEffect, useState } from 'react';
import { AppShell } from '@/components/layout/AppShell';
import { GapItemRow } from '@/components/report/GapItemRow';
import { getExcelExportUrl, getSession, getSessionItems } from '@/lib/api';
import type { BcpwebAnalysisSession, BcpwebComplianceItem, BcpwebSeverity } from '@/types';
import { cn } from '@/lib/utils';

const FILTERS: { id: BcpwebSeverity | 'all'; label: string }[] = [
  { id: 'all', label: 'All' },
  { id: 'critical', label: 'Critical' },
  { id: 'high', label: 'High' },
  { id: 'medium', label: 'Medium' },
  { id: 'low', label: 'Low' },
  { id: 'compliant', label: 'Compliant' },
];

/** Full gap analysis report workbench */
export default function ReportPage({ params }: { params: { sessionId: string } }) {
  const { sessionId } = params;
  const [session, setSession] = useState<BcpwebAnalysisSession | null>(null);
  const [items, setItems] = useState<BcpwebComplianceItem[]>([]);
  const [filter, setFilter] = useState<BcpwebSeverity | 'all'>('all');

  useEffect(() => {
    getSession(sessionId).then(setSession).catch(console.error);
    getSessionItems(sessionId).then(setItems).catch(console.error);
  }, [sessionId]);

  const filtered =
    filter === 'all' ? items : items.filter((i) => i.severity === filter);

  const counts = {
    critical: items.filter((i) => i.severity === 'critical').length,
    high: items.filter((i) => i.severity === 'high').length,
    medium: items.filter((i) => i.severity === 'medium').length,
    compliant: items.filter((i) => i.severity === 'compliant').length,
  };

  if (!session) {
    return (
      <AppShell>
        <p className="text-slate-400">Loading report…</p>
      </AppShell>
    );
  }

  return (
    <AppShell>
      <div className="mb-4 flex flex-wrap items-start justify-between gap-4">
        <div>
          <h1 className="text-xl font-bold">Gap Analysis — Working Document</h1>
          <p className="text-sm text-slate-400">
            {session.internalDocName} vs. {session.regulationTitle} — {session.analysisDate}
          </p>
        </div>
        <div className="flex gap-2">
          <a
            href={getExcelExportUrl(sessionId)}
            className="rounded-lg border border-white/20 px-4 py-2 text-sm hover:bg-white/5"
          >
            Export XLSX
          </a>
          <button type="button" className="rounded-lg bg-emerald-500 px-4 py-2 text-sm font-medium">
            Re-run
          </button>
        </div>
      </div>

      <div className="mb-4 grid gap-3 sm:grid-cols-4">
        <SummaryCard label="Critical" value={counts.critical} sub="Immediate action" tone="text-red-400" />
        <SummaryCard label="High" value={counts.high} sub="Significant gap" tone="text-orange-400" />
        <SummaryCard label="Medium" value={counts.medium} sub="Partial coverage" tone="text-yellow-400" />
        <SummaryCard label="Compliant" value={counts.compliant} sub="Fully addressed" tone="text-blue-400" />
      </div>

      <div className="mb-4 rounded-xl border border-cyan-500/30 bg-cyan-500/5 p-3 text-xs text-slate-400">
        <strong className="text-cyan-400">AI draft</strong> — Columns C and J are extracted verbatim from
        source documents. Interpretation, gaps and recommended actions are AI-drafted and require your
        review.
      </div>

      <div className="mb-4 flex flex-wrap gap-2">
        {FILTERS.map((f) => (
          <button
            key={f.id}
            type="button"
            onClick={() => setFilter(f.id)}
            className={cn(
              'rounded-lg px-3 py-1.5 text-sm',
              filter === f.id ? 'bg-white/10 text-white' : 'text-slate-400 hover:bg-white/5',
            )}
          >
            {f.label} ({f.id === 'all' ? items.length : items.filter((i) => i.severity === f.id).length})
          </button>
        ))}
      </div>

      <div className="space-y-3">
        {filtered.map((item, idx) => (
          <GapItemRow
            key={item.id}
            item={item}
            sessionId={sessionId}
            defaultOpen={idx === 0}
            onUpdate={(updated) => {
              setItems((prev) => prev.map((i) => (i.id === updated.id ? updated : i)));
            }}
          />
        ))}
      </div>
    </AppShell>
  );
}

function SummaryCard({
  label,
  value,
  sub,
  tone,
}: {
  label: string;
  value: number;
  sub: string;
  tone: string;
}) {
  return (
    <div className="rounded-xl border border-white/10 bg-white/5 p-4">
      <p className="text-xs uppercase text-slate-500">{label}</p>
      <p className={`text-3xl font-bold ${tone}`}>{value}</p>
      <p className="text-xs text-slate-500">{sub}</p>
    </div>
  );
}
