'use client';

import { ReferenceComplianceCard } from '../../lib/ai-lab/reference-compliance-view';
import { parseReferenceComplianceBlock } from '../../lib/ai-lab/parse-reference-response';
import {
  agreementBadgeClass,
  type AgreementStatus,
  type DualVerifyAgreement,
} from '../../lib/landing-ai/dual-verify-merge';
import type { DualVerifyReportItem } from '../../lib/landing-ai/dual-verify-report';

type DualVerifyResultCardProps = {
  item: DualVerifyReportItem;
  variant?: 'light' | 'dark';
};

export function DualVerifyResultCard({
  item,
  variant = 'light',
}: DualVerifyResultCardProps) {
  const { pointId, pointTitle, agreement, landingMessage, llmMessage, errorMessage, status } =
    item;

  const shell =
    variant === 'dark'
      ? 'space-y-3 rounded-xl border border-white/10 bg-slate-900/50 p-4'
      : 'space-y-3 rounded-xl border border-slate-200 bg-white p-4 shadow-sm';

  const titleClass =
    variant === 'dark'
      ? 'text-sm font-bold text-slate-100'
      : 'text-sm font-bold text-slate-900';

  const summaryClass =
    variant === 'dark' ? 'text-xs text-slate-300' : 'text-xs text-slate-700';

  const passLabel =
    variant === 'dark'
      ? 'mb-2 text-xs font-semibold uppercase tracking-wide text-slate-400'
      : 'mb-2 text-xs font-semibold uppercase tracking-wide';

  const passPanel =
    variant === 'dark'
      ? 'rounded-lg border border-white/10 bg-white p-3 text-slate-900'
      : '';

  if (status === 'failed' || errorMessage) {
    return (
      <div
        className={
          variant === 'dark'
            ? 'rounded-xl border border-red-500/40 bg-red-500/10 p-4'
            : 'rounded-xl border border-red-200 bg-red-50/50 p-4'
        }
      >
        <p className={`text-sm font-bold ${variant === 'dark' ? 'text-red-200' : 'text-red-900'}`}>
          {pointId}
          {pointTitle ? ` — ${pointTitle}` : ''}
        </p>
        <p className={`mt-1 text-xs ${variant === 'dark' ? 'text-red-300' : 'text-red-800'}`}>
          {errorMessage ?? 'Analysis failed'}
        </p>
      </div>
    );
  }

  if (!landingMessage || !llmMessage || !agreement) {
    return (
      <div
        className={
          variant === 'dark'
            ? 'rounded-xl border border-white/10 bg-white/5 p-4 text-xs text-slate-400'
            : 'rounded-xl border border-slate-200 bg-slate-50 p-4 text-xs text-slate-600'
        }
      >
        {pointId} —{' '}
        {status === 'running'
          ? 'Running…'
          : status === 'queued'
            ? 'Queued…'
            : status}
      </div>
    );
  }

  return (
    <div className={shell}>
      <div className="flex flex-wrap items-center gap-2">
        <h3 className={titleClass}>
          {pointId}
          {pointTitle ? ` — ${pointTitle}` : ''}
        </h3>
        <span
          className={`rounded-full border px-2 py-0.5 text-[11px] font-semibold ${agreementBadgeClass(agreement.status as AgreementStatus)}`}
        >
          {agreement.label}
        </span>
      </div>
      <p className={summaryClass}>{agreement.summary}</p>
      <div className="grid gap-4 lg:grid-cols-2">
        <div>
          <p className={`${passLabel} text-teal-800`}>Pass 1 — Landing AI</p>
          <div className={passPanel}>
            <ReferenceComplianceCard
              block={parseReferenceComplianceBlock(landingMessage)}
            />
          </div>
        </div>
        <div>
          <p className={`${passLabel} text-indigo-800`}>Pass 2 — LLM verify</p>
          <div className={passPanel}>
            <ReferenceComplianceCard
              block={parseReferenceComplianceBlock(llmMessage)}
            />
          </div>
        </div>
      </div>
    </div>
  );
}
