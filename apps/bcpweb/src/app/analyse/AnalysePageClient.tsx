'use client';

import Link from 'next/link';
import { useSearchParams } from 'next/navigation';
import { useCallback, useEffect, useState } from 'react';
import { Check, Upload, X } from 'lucide-react';
import { AppShell } from '@/components/layout/AppShell';
import { SeverityBadge } from '@/components/ui/SeverityBadge';
import {
  createAnalysisSession,
  getRegulations,
  getSessionItems,
  pollSessionProgress,
} from '@/lib/api';
import type { BcpwebAnalysisSession, BcpwebComplianceItem, BcpwebRegulation } from '@/types';
import { cn } from '@/lib/utils';

const WIZARD_REGS: { id: string; match: string }[] = [
  { id: 'reg-tfs', match: 'TFS' },
  { id: 'reg-aml', match: 'AML' },
  { id: 'reg-fatf', match: 'FATF' },
  { id: 'reg-cab74', match: 'Cabinet' },
];

const STEPS = [
  'Parsing and chunking document',
  'Loading regulation clauses (48 found)',
  'Cross-referencing requirements',
  'Identifying gaps and risk levels',
  'Generating remediation actions',
];

/** New gap analysis wizard (client) */
export default function AnalysePageClient() {
  const searchParams = useSearchParams();
  const preReg = searchParams.get('regulation');

  const [regulations, setRegulations] = useState<BcpwebRegulation[]>([]);
  const [selectedReg, setSelectedReg] = useState<string | null>(preReg);
  const [internalFile, setInternalFile] = useState<File | null>(null);
  const [regulationFile, setRegulationFile] = useState<File | null>(null);
  const [session, setSession] = useState<BcpwebAnalysisSession | null>(null);
  const [topFindings, setTopFindings] = useState<BcpwebComplianceItem[]>([]);
  const [running, setRunning] = useState(false);

  useEffect(() => {
    getRegulations().then((r) => {
      const wizard = r.items.filter((item) =>
        WIZARD_REGS.some((w) => item.id === w.id || item.title.includes(w.match)),
      );
      setRegulations(wizard.length >= 4 ? wizard.slice(0, 4) : r.items.slice(0, 4));
    });
  }, []);

  const poll = useCallback(async (id: string) => {
    const interval = setInterval(async () => {
      try {
        const s = await pollSessionProgress(id);
        setSession(s);
        if (s.status === 'completed') {
          clearInterval(interval);
          setRunning(false);
          const items = await getSessionItems(id);
          setTopFindings(
            items.filter((i) => i.severity === 'critical' || i.severity === 'high').slice(0, 5),
          );
        }
      } catch {
        clearInterval(interval);
        setRunning(false);
      }
    }, 1000);
    return () => clearInterval(interval);
  }, []);

  const runAnalysis = async () => {
    if (!selectedReg || !internalFile || !regulationFile) return;
    setRunning(true);
    setSession(null);
    setTopFindings([]);
    try {
      const s = await createAnalysisSession({
        regulationId: selectedReg,
        internalFile,
        regulationFile,
      });
      setSession(s);
      void poll(s.id);
    } catch (e) {
      console.error(e);
      setRunning(false);
      alert(e instanceof Error ? e.message : 'Analysis failed');
    }
  };

  const canRun = selectedReg && internalFile && regulationFile && !running;

  return (
    <AppShell>
      <div className="mb-6">
        <h1 className="text-2xl font-bold">New Gap Analysis</h1>
        <p className="text-sm text-slate-400">
          Select a regulation, upload your compliance document, and run AI analysis.
        </p>
      </div>

      <div className="mb-6 flex flex-wrap items-center justify-between gap-3 rounded-xl border border-violet-500/30 bg-violet-500/10 px-4 py-3">
        <div>
          <p className="text-sm font-medium text-violet-200">Need Landing AI + Gemini dual verify?</p>
          <p className="text-xs text-slate-400">
            Async Kafka pipeline · combined report · PDF & Excel export · Supabase persistence
          </p>
        </div>
        <Link
          href="/dual-verify"
          className="rounded-lg bg-emerald-600 px-4 py-2 text-sm font-medium text-white hover:bg-emerald-500"
        >
          Open Dual Verify →
        </Link>
      </div>

      <div className="grid gap-6 lg:grid-cols-3">
        <div className="space-y-6 lg:col-span-2">
          <section>
            <h2 className="mb-3 text-sm font-medium text-slate-400">Step 1 — Select regulation</h2>
            <div className="grid gap-3 sm:grid-cols-2">
              {regulations.map((r) => (
                <button
                  key={r.id}
                  type="button"
                  onClick={() => setSelectedReg(r.id)}
                  className={cn(
                    'relative rounded-xl border p-4 text-left transition',
                    selectedReg === r.id
                      ? 'border-emerald-500 bg-emerald-500/10'
                      : 'border-white/10 bg-white/5 hover:border-white/20',
                  )}
                >
                  {selectedReg === r.id && (
                    <Check className="absolute right-3 top-3 h-4 w-4 text-emerald-400" />
                  )}
                  <p className="font-medium">{r.title}</p>
                  <p className="text-xs text-slate-500">
                    {r.issuingBody} · {r.version}
                  </p>
                  <p className="mt-2 text-xs text-slate-400">
                    Clauses: {r.clauseCount} · Type: {r.type}
                  </p>
                </button>
              ))}
            </div>
          </section>

          <UploadZone
            label="Step 2 — Upload your compliance document"
            sub="Internal compliance document — PDF or DOCX"
            file={internalFile}
            onFile={setInternalFile}
          />

          <UploadZone
            label="Step 3 — Upload the regulation to check against"
            sub="Regulation / guideline PDF — benchmark from library"
            file={regulationFile}
            onFile={setRegulationFile}
          />

          <button
            type="button"
            disabled={!canRun}
            onClick={() => void runAnalysis()}
            className="w-full rounded-xl bg-emerald-600 py-3 font-medium disabled:cursor-not-allowed disabled:opacity-40"
          >
            Run AI Gap Analysis
          </button>
        </div>

        <aside className="rounded-xl border border-white/10 bg-white/5 p-5">
          {!session && (
            <div className="flex h-full min-h-[320px] flex-col items-center justify-center text-center text-slate-500">
              <Upload className="mb-3 h-10 w-10 text-slate-600" />
              <p className="text-sm">Analysis results will appear here.</p>
              <p className="mt-1 text-xs">Select a regulation and upload a document to begin.</p>
            </div>
          )}

          {session && session.status !== 'completed' && (
            <>
              <p className="mb-4 text-sm font-medium">
                Analysing document against {session.regulationTitle}…
              </p>
              <ul className="mb-4 space-y-2 text-sm">
                {STEPS.map((step, i) => {
                  const pct = session.progressPct;
                  const done = pct >= (i + 1) * 20;
                  const active = !done && pct >= i * 20;
                  return (
                    <li key={step} className="flex items-center gap-2">
                      <span
                        className={cn(
                          'h-2 w-2 rounded-full',
                          done ? 'bg-emerald-400' : active ? 'bg-blue-400' : 'bg-slate-600',
                        )}
                      />
                      <span className={done ? 'text-slate-300' : 'text-slate-500'}>{step}</span>
                    </li>
                  );
                })}
              </ul>
              <div className="mb-4 h-2 overflow-hidden rounded-full bg-slate-800">
                <div
                  className="h-full bg-emerald-500 transition-all"
                  style={{ width: `${session.progressPct}%` }}
                />
              </div>
              {session.briefing && (
                <pre className="max-h-48 overflow-auto whitespace-pre-wrap text-xs text-slate-400">
                  {session.briefing}
                </pre>
              )}
            </>
          )}

          {session?.status === 'completed' && (
            <>
              <p className="mb-2 font-medium text-emerald-400">Analysis complete ✓</p>
              <Link
                href={`/analyse/report/${session.id}`}
                className="mb-4 inline-block rounded-lg bg-emerald-500 px-3 py-1.5 text-sm font-medium"
              >
                View full report →
              </Link>
              <ul className="space-y-2 text-sm">
                {topFindings.map((f) => (
                  <li
                    key={f.id}
                    className="flex items-start justify-between gap-2 rounded-lg border border-white/5 p-2"
                  >
                    <span className="text-slate-300">
                      {f.title} ({f.clauseNo})
                    </span>
                    <SeverityBadge severity={f.severity} />
                  </li>
                ))}
              </ul>
            </>
          )}
        </aside>
      </div>
    </AppShell>
  );
}

function UploadZone({
  label,
  sub,
  file,
  onFile,
}: {
  label: string;
  sub: string;
  file: File | null;
  onFile: (f: File | null) => void;
}) {
  return (
    <section>
      <h2 className="mb-3 text-sm font-medium text-slate-400">{label}</h2>
      {!file ? (
        <label className="flex cursor-pointer flex-col items-center rounded-xl border border-dashed border-emerald-500/40 bg-emerald-500/5 p-8 hover:bg-emerald-500/10">
          <Upload className="mb-2 h-8 w-8 text-emerald-400" />
          <p className="font-medium">{sub.split(' — ')[0]}</p>
          <p className="text-xs text-slate-500">{sub.split(' — ')[1] ?? sub}</p>
          <input
            type="file"
            accept=".pdf,.doc,.docx"
            className="hidden"
            onChange={(e) => onFile(e.target.files?.[0] ?? null)}
          />
        </label>
      ) : (
        <div className="flex items-center justify-between rounded-xl border border-white/10 bg-white/5 p-3">
          <div className="flex items-center gap-2">
            <span className="rounded bg-red-500/20 px-2 py-1 text-xs text-red-400">PDF</span>
            <span className="text-sm">{file.name}</span>
            <span className="text-xs text-slate-500">
              {Math.round(file.size / 1024)} KB — Ready
            </span>
          </div>
          <button type="button" onClick={() => onFile(null)} className="text-slate-500 hover:text-white">
            <X className="h-4 w-4" />
          </button>
        </div>
      )}
    </section>
  );
}
