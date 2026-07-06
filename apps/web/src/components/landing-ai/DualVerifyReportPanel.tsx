'use client';

import { useMemo } from 'react';
import {
  AttentionFocusCompact,
  ColorCodeLegend,
  MarkdownSummary,
  StatusTierBoxes,
} from '../../lib/ai-lab/compliance-report-view';
import { buildReportStats } from '../../lib/ai-lab/parse-compliance-results';
import { agreementBadgeClass } from '../../lib/landing-ai/dual-verify-merge';
import {
  buildDualVerifyExecutiveSummary,
  buildReportSummary,
  parsedResultsFromReport,
  type DualVerifyReportItem,
} from '../../lib/landing-ai/dual-verify-report';
import { DualVerifyResultCard } from './DualVerifyResultCard';

type DualVerifyReportPanelProps = {
  items: DualVerifyReportItem[];
  sessionLabel?: string;
  dbNote?: string;
  exporting?: boolean;
  onExportCombinedPdf: () => void;
  onExportBothPassesExcel: () => void;
  onExportSummaryPdf?: () => void;
  onExportDetailPdf?: () => void;
  onExportPass1Pdf?: () => void;
  onExportExcel?: () => void;
  onExportFormattedExcel?: () => void;
};

export function DualVerifyReportPanel({
  items,
  sessionLabel,
  dbNote,
  exporting,
  onExportCombinedPdf,
  onExportBothPassesExcel,
  onExportSummaryPdf,
  onExportDetailPdf,
  onExportPass1Pdf,
  onExportExcel,
  onExportFormattedExcel,
}: DualVerifyReportPanelProps) {
  const agreementSummary = useMemo(() => buildReportSummary(items), [items]);
  const pass2Results = useMemo(
    () => parsedResultsFromReport(items, 'llm'),
    [items],
  );
  const pass1Results = useMemo(
    () => parsedResultsFromReport(items, 'landing'),
    [items],
  );
  const pass2Stats = useMemo(
    () => buildReportStats(pass2Results),
    [pass2Results],
  );
  const executiveSummary = useMemo(
    () => buildDualVerifyExecutiveSummary(items, agreementSummary),
    [items, agreementSummary],
  );

  const reviewItems = items.filter(
    (i) => i.agreement && i.agreement.status !== 'aligned',
  );

  if (items.length === 0) return null;

  return (
    <section className="mt-10 space-y-6">
      <div className="flex flex-wrap items-center justify-between gap-4 rounded-xl border border-emerald-500/30 bg-emerald-500/10 p-4">
        <div>
          <h2 className="text-lg font-bold text-emerald-100">
            Session report ({agreementSummary.total} points)
          </h2>
          {sessionLabel && (
            <p className="mt-0.5 font-mono text-xs text-slate-400">{sessionLabel}</p>
          )}
          <p className="mt-1 text-sm text-slate-300">
            {agreementSummary.completed} completed · {agreementSummary.aligned}{' '}
            aligned · {agreementSummary.needsReview} need review ·{' '}
            {agreementSummary.failed} failed
            {agreementSummary.inProgress > 0 &&
              ` · ${agreementSummary.inProgress} in progress`}
          </p>
          {dbNote && (
            <p className="mt-2 text-xs font-medium text-emerald-300">{dbNote}</p>
          )}
        </div>
        <div className="flex flex-wrap gap-2">
          <button
            type="button"
            disabled={exporting || agreementSummary.completed === 0}
            onClick={onExportCombinedPdf}
            className="rounded-lg border border-amber-400/60 bg-amber-500 px-3 py-1.5 text-xs font-semibold text-white hover:bg-amber-400 disabled:opacity-40"
            title="Full PDF with Pass 1 + Pass 2 — status, confidence, output, fulfilled, CAP"
          >
            Both passes PDF
          </button>
          <button
            type="button"
            disabled={exporting || agreementSummary.completed === 0}
            onClick={onExportBothPassesExcel}
            className="rounded-lg border border-sky-400/60 bg-sky-600 px-3 py-1.5 text-xs font-semibold text-white hover:bg-sky-500 disabled:opacity-40"
            title="BCP formatted Excel with Pass 1 + Pass 2 columns"
          >
            Both passes Excel
          </button>
          {onExportSummaryPdf && (
            <button
              type="button"
              disabled={exporting || agreementSummary.completed === 0}
              onClick={onExportSummaryPdf}
              className="rounded-lg border border-white/20 bg-white/10 px-3 py-1.5 text-xs font-medium hover:bg-white/20 disabled:opacity-40"
            >
              Summary PDF
            </button>
          )}
          {onExportPass1Pdf && (
            <button
              type="button"
              disabled={exporting || agreementSummary.completed === 0}
              onClick={onExportPass1Pdf}
              className="rounded-lg border border-teal-500/50 bg-teal-700 px-3 py-1.5 text-xs font-medium text-white hover:bg-teal-600 disabled:opacity-40"
            >
              Pass 1 PDF
            </button>
          )}
          {onExportDetailPdf && (
            <button
              type="button"
              disabled={exporting || agreementSummary.completed === 0}
              onClick={onExportDetailPdf}
              className="rounded-lg border border-emerald-500/50 bg-emerald-600 px-3 py-1.5 text-xs font-medium text-white hover:bg-emerald-500 disabled:opacity-40"
            >
              Pass 2 PDF
            </button>
          )}
          {onExportExcel && (
            <button
              type="button"
              disabled={exporting || agreementSummary.completed === 0}
              onClick={onExportExcel}
              className="rounded-lg border border-violet-500/50 bg-violet-600 px-3 py-1.5 text-xs font-medium text-white hover:bg-violet-500 disabled:opacity-40"
            >
              Raw Excel
            </button>
          )}
          {onExportFormattedExcel && (
            <button
              type="button"
              disabled={exporting || agreementSummary.completed === 0}
              onClick={onExportFormattedExcel}
              className="rounded-lg border border-white/20 px-3 py-1.5 text-xs font-medium text-slate-300 hover:bg-white/5 disabled:opacity-40"
            >
              Pass 2 Excel only
            </button>
          )}
        </div>
      </div>

      <div className="space-y-6 rounded-xl border border-white/10 bg-slate-900/60 p-6">
        <div>
          <h3 className="text-lg font-bold text-slate-100">Executive summary</h3>
          <p className="text-xs text-slate-500">
            Generated {new Date().toLocaleString()} · Pass 2 drives compliance
            statistics (same as sync workbench)
          </p>
        </div>

        <div className="[&_.rounded-xl]:border-white/10 [&_.rounded-xl]:bg-slate-800/50">
          <ColorCodeLegend results={pass2Results} />
        </div>

        <section>
          <h4 className="mb-3 text-sm font-semibold uppercase tracking-wide text-violet-300">
            Pass 2 statistics (LLM verify)
          </h4>
          <p className="mb-3 text-sm text-slate-400">
            {pass2Stats.total} point{pass2Stats.total === 1 ? '' : 's'} ·{' '}
            {pass2Stats.compliant} compliant · {pass2Stats.partial} partial ·{' '}
            {pass2Stats.nonCompliant} non-compliant
          </p>
          <StatusTierBoxes stats={pass2Stats} />
        </section>

        <section>
          <h4 className="mb-3 text-sm font-semibold uppercase tracking-wide text-violet-300">
            Dual-verify agreement
          </h4>
          <div className="grid gap-3 sm:grid-cols-3">
            <div className="rounded-lg border border-emerald-500/30 bg-emerald-500/10 p-3 text-center">
              <p className="text-2xl font-bold text-emerald-300">
                {agreementSummary.aligned}
              </p>
              <p className="text-xs text-emerald-200/80">Aligned</p>
            </div>
            <div className="rounded-lg border border-amber-500/30 bg-amber-500/10 p-3 text-center">
              <p className="text-2xl font-bold text-amber-300">
                {agreementSummary.needsReview}
              </p>
              <p className="text-xs text-amber-200/80">Need review</p>
            </div>
            <div className="rounded-lg border border-red-500/30 bg-red-500/10 p-3 text-center">
              <p className="text-2xl font-bold text-red-300">
                {agreementSummary.failed}
              </p>
              <p className="text-xs text-red-200/80">Failed</p>
            </div>
          </div>
        </section>

        <section>
          <h4 className="mb-3 text-sm font-semibold uppercase tracking-wide text-violet-300">
            Narrative summary
          </h4>
          <div className="rounded-lg border border-white/10 bg-slate-800/50 p-4 text-slate-200">
            <MarkdownSummary text={executiveSummary} />
          </div>
        </section>

        {reviewItems.length > 0 && (
          <section>
            <h4 className="mb-3 text-sm font-semibold uppercase tracking-wide text-amber-300">
              Agreement mismatches ({reviewItems.length})
            </h4>
            <ul className="space-y-2">
              {reviewItems.map((item) => (
                <li
                  key={item.pointId}
                  className="rounded-lg border border-amber-500/20 bg-amber-500/5 px-3 py-2 text-sm"
                >
                  <span className="font-mono font-medium text-amber-200">
                    {item.pointId}
                  </span>
                  <span
                    className={`ml-2 rounded border px-1.5 py-0.5 text-[10px] ${agreementBadgeClass(item.agreement!.status)}`}
                  >
                    {item.agreement!.label}
                  </span>
                  <p className="mt-1 text-xs text-slate-400">
                    {item.agreement!.summary}
                  </p>
                </li>
              ))}
            </ul>
          </section>
        )}

        <section>
          <h4 className="mb-3 text-sm font-semibold uppercase tracking-wide text-amber-300">
            Attention focus — Pass 2 (&lt; 100% or Partial / Non-Compliant)
          </h4>
          <AttentionFocusCompact items={pass2Stats.attentionItems} />
        </section>

        {pass1Results.length > 0 && (
          <section>
            <h4 className="mb-2 text-xs font-semibold uppercase tracking-wide text-slate-500">
              Pass 1 (Landing AI) quick stats
            </h4>
            <p className="text-xs text-slate-500">
              {buildReportStats(pass1Results).compliant} compliant ·{' '}
              {buildReportStats(pass1Results).partial} partial ·{' '}
              {buildReportStats(pass1Results).nonCompliant} non-compliant
            </p>
          </section>
        )}
      </div>

      <section>
        <h3 className="mb-4 text-sm font-semibold uppercase tracking-wide text-violet-300">
          Detailed results — every point ({items.length})
        </h3>
        <p className="mb-4 text-xs text-slate-500">
          Full Pass 1 (Landing AI) and Pass 2 (LLM) cards per point — expand
          fulfilled clauses and corrective action plans inline.
        </p>
        <div className="space-y-6">
          {items.map((item) => (
            <DualVerifyResultCard key={item.pointId} item={item} variant="dark" />
          ))}
        </div>
      </section>
    </section>
  );
}
