'use client';

import { useCallback, useState } from 'react';
import { ChevronDown, ChevronUp } from 'lucide-react';
import { PdfViewerModal } from '@/components/report/PdfViewerModal';
import { SeverityBadge } from '@/components/ui/SeverityBadge';
import { signOffItem, updateComplianceItem } from '@/lib/api';
import type { BcpwebComplianceItem, BcpwebUpdateItemDto } from '@/types';
import {
  ASSIGNEE_OPTIONS,
  COMPLIANCE_STATUS_OPTIONS,
  DEPARTMENT_OPTIONS,
  EFFECTIVENESS_OPTIONS,
} from '@/types';

interface GapItemRowProps {
  item: BcpwebComplianceItem;
  sessionId: string;
  defaultOpen?: boolean;
  onUpdate: (item: BcpwebComplianceItem) => void;
}

/** Expandable gap analysis row with full form */
export function GapItemRow({
  item,
  sessionId,
  defaultOpen = false,
  onUpdate,
}: GapItemRowProps) {
  const [open, setOpen] = useState(defaultOpen);
  const [pdf, setPdf] = useState<{
    source: 'regulation' | 'policy';
    page: number;
  } | null>(null);
  const [local, setLocal] = useState(item);

  const save = useCallback(
    async (patch: BcpwebUpdateItemDto) => {
      const next = { ...local, ...patch } as BcpwebComplianceItem;
      setLocal(next);
      try {
        const updated = await updateComplianceItem(sessionId, item.id, patch);
        onUpdate(updated);
      } catch (e) {
        console.error(e);
      }
    },
    [local, item.id, sessionId, onUpdate],
  );

  const handleSignOff = async () => {
    const updated = await signOffItem(sessionId, item.id);
    setLocal(updated);
    onUpdate(updated);
  };

  return (
    <>
      <div className="rounded-xl border border-white/10 bg-white/5 overflow-hidden">
        <button
          type="button"
          onClick={() => setOpen(!open)}
          className="flex w-full items-center justify-between p-4 text-left hover:bg-white/5"
        >
          <div className="flex items-center gap-3">
            <span className="text-slate-500">{String(local.serialNo).padStart(2, '0')}</span>
            <span className="text-slate-400">{local.clauseNo}</span>
            <span className="font-medium">{local.title}</span>
          </div>
          <div className="flex items-center gap-2">
            <SeverityBadge severity={local.severity} />
            {local.signedOff && (
              <span className="rounded-full bg-blue-500/20 px-2 py-0.5 text-xs text-blue-400">
                Signed off
              </span>
            )}
            {open ? <ChevronUp className="h-4 w-4" /> : <ChevronDown className="h-4 w-4" />}
          </div>
        </button>

        {open && (
          <div className="border-t border-white/10 p-4 space-y-4">
            <div className="grid gap-4 md:grid-cols-2">
              <ExtractBox
                label="Regulatory Requirement"
                text={local.regulatoryText}
                page={local.regulatoryPdfPage}
                onViewPdf={() => setPdf({ source: 'regulation', page: local.regulatoryPdfPage })}
              />
              <ExtractBox
                label="Policy Extract"
                text={local.policyText}
                page={local.policyPdfPage}
                onViewPdf={() => setPdf({ source: 'policy', page: local.policyPdfPage })}
              />
            </div>

            <Field label="Gaps Identified" tag="AI DRAFT — REVIEW & EDIT">
              <textarea
                rows={4}
                value={local.gapsIdentified}
                onChange={(e) => setLocal({ ...local, gapsIdentified: e.target.value })}
                onBlur={() => void save({ gapsIdentified: local.gapsIdentified })}
                className="w-full rounded-lg border border-white/10 p-3 text-sm"
              />
            </Field>

            <Field label="Management Response" tag="FILL IN">
              <textarea
                rows={3}
                placeholder="Describe what management has done or documented in response to this requirement..."
                value={local.managementResponse}
                onChange={(e) => setLocal({ ...local, managementResponse: e.target.value })}
                onBlur={() => void save({ managementResponse: local.managementResponse })}
                className="w-full rounded-lg border border-white/10 p-3 text-sm"
              />
            </Field>

            <div className="grid gap-4 md:grid-cols-3">
              <SelectField
                label="Design Effectiveness"
                value={local.designEffectiveness}
                options={EFFECTIVENESS_OPTIONS}
                onChange={(v) => void save({ designEffectiveness: v })}
              />
              <SelectField
                label="Operating Effectiveness"
                value={local.operatingEffectiveness}
                options={EFFECTIVENESS_OPTIONS}
                onChange={(v) => void save({ operatingEffectiveness: v })}
              />
              <SelectField
                label="Overall Effectiveness"
                value={local.overallEffectiveness}
                options={EFFECTIVENESS_OPTIONS}
                onChange={(v) => void save({ overallEffectiveness: v })}
              />
            </div>

            <div className="grid gap-4 md:grid-cols-2">
              <Field label="Document Reference" tag="FILL IN">
                <input
                  placeholder="e.g. TFS Manual §7, page 19"
                  value={local.documentReference}
                  onChange={(e) => setLocal({ ...local, documentReference: e.target.value })}
                  onBlur={() => void save({ documentReference: local.documentReference })}
                  className="w-full rounded-lg border border-white/10 p-2 text-sm"
                />
              </Field>
              <Field label="Evidence Reference" tag="FILL IN">
                <input
                  placeholder="e.g. ref.#2.5, 4.3"
                  value={local.evidenceReference}
                  onChange={(e) => setLocal({ ...local, evidenceReference: e.target.value })}
                  onBlur={() => void save({ evidenceReference: local.evidenceReference })}
                  className="w-full rounded-lg border border-white/10 p-2 text-sm"
                />
              </Field>
            </div>

            <Field label="Evidence of Implementation" tag="FILL IN">
              <textarea
                rows={3}
                placeholder="List evidence: documentary ref.#, page numbers, section numbers, approval dates..."
                value={local.evidenceImplementation}
                onChange={(e) => setLocal({ ...local, evidenceImplementation: e.target.value })}
                onBlur={() => void save({ evidenceImplementation: local.evidenceImplementation })}
                className="w-full rounded-lg border border-white/10 p-3 text-sm"
              />
            </Field>

            <div className="grid gap-4 md:grid-cols-3">
              <SelectField
                label="Responsible Department"
                value={local.responsibleDepartment}
                options={DEPARTMENT_OPTIONS}
                onChange={(v) => void save({ responsibleDepartment: v })}
              />
              <SelectField
                label="Compliance Status"
                value={local.complianceStatus}
                options={COMPLIANCE_STATUS_OPTIONS}
                onChange={(v) => void save({ complianceStatus: v })}
              />
              <Field label="Target Date" tag="SET DATE">
                <input
                  type="date"
                  value={local.targetDate}
                  onChange={(e) => {
                    setLocal({ ...local, targetDate: e.target.value });
                    void save({ targetDate: e.target.value });
                  }}
                  className="w-full rounded-lg border border-white/10 p-2 text-sm"
                />
              </Field>
            </div>

            <Field label="Conclusion" tag="AI DRAFT — REVIEW & EDIT">
              <textarea
                rows={2}
                value={local.conclusion}
                onChange={(e) => setLocal({ ...local, conclusion: e.target.value })}
                onBlur={() => void save({ conclusion: local.conclusion })}
                className="w-full rounded-lg border border-white/10 p-3 text-sm"
              />
            </Field>

            <Field label="Observation" tag="AI DRAFT — REVIEW & EDIT">
              <textarea
                rows={2}
                value={local.observation}
                onChange={(e) => setLocal({ ...local, observation: e.target.value })}
                onBlur={() => void save({ observation: local.observation })}
                className="w-full rounded-lg border border-white/10 p-3 text-sm"
              />
            </Field>

            <Field label="Action Plan" tag="AI DRAFT — REVIEW & EDIT">
              <textarea
                rows={4}
                value={local.actionPlan}
                onChange={(e) => setLocal({ ...local, actionPlan: e.target.value })}
                onBlur={() => void save({ actionPlan: local.actionPlan })}
                className="w-full rounded-lg border border-white/10 p-3 text-sm"
              />
            </Field>

            <div className="flex items-center justify-between border-t border-white/10 pt-4">
              <select
                value={local.assignedTo}
                onChange={(e) => void save({ assignedTo: e.target.value })}
                className="rounded-lg border border-white/10 px-3 py-2 text-sm"
              >
                <option value="">— Unassigned —</option>
                {ASSIGNEE_OPTIONS.map((o) => (
                  <option key={o} value={o}>
                    {o}
                  </option>
                ))}
              </select>
              <button
                type="button"
                disabled={local.signedOff}
                onClick={() => void handleSignOff()}
                className="rounded-lg bg-slate-600 px-4 py-2 text-sm font-medium disabled:opacity-50"
              >
                Sign off
              </button>
            </div>
          </div>
        )}
      </div>

      {pdf && (
        <PdfViewerModal
          sessionId={sessionId}
          source={pdf.source}
          page={pdf.page}
          itemId={local.id}
          onClose={() => setPdf(null)}
        />
      )}
    </>
  );
}

function ExtractBox({
  label,
  text,
  page,
  onViewPdf,
}: {
  label: string;
  text: string;
  page: number;
  onViewPdf: () => void;
}) {
  return (
    <div className="rounded-lg border border-white/10 bg-black/20 p-3">
      <p className="mb-2 text-[10px] font-semibold uppercase tracking-wide text-slate-500">
        {label} <span className="text-cyan-500">(AI EXTRACTED)</span>
      </p>
      <p className="text-sm text-slate-300">{text}</p>
      <button
        type="button"
        onClick={onViewPdf}
        className="mt-2 text-xs text-emerald-400 hover:underline"
      >
        View PDF · p.{page}
      </button>
    </div>
  );
}

function Field({
  label,
  tag,
  children,
}: {
  label: string;
  tag: string;
  children: React.ReactNode;
}) {
  return (
    <div>
      <p className="mb-1 text-[10px] font-semibold uppercase tracking-wide text-yellow-500/90">
        {label}{' '}
        <span className="text-yellow-600/80">({tag})</span>
      </p>
      {children}
    </div>
  );
}

function SelectField({
  label,
  value,
  options,
  onChange,
}: {
  label: string;
  value: string;
  options: readonly string[];
  onChange: (v: string) => void;
}) {
  return (
    <Field label={label} tag="SELECT">
      <select
        value={value}
        onChange={(e) => onChange(e.target.value)}
        className="w-full rounded-lg border border-white/10 p-2 text-sm"
      >
        <option value="">— Select —</option>
        {options.map((o) => (
          <option key={o} value={o}>
            {o}
          </option>
        ))}
      </select>
    </Field>
  );
}
