'use client';

import Link from 'next/link';
import { useCallback, useEffect, useMemo, useState, type ReactNode } from 'react';
import { DualVerifyReportPanel } from './DualVerifyReportPanel';
import {
  AI_MODELS,
  API_BASE as DEFAULT_API_BASE,
  DEFAULT_PDF_NAME,
} from '../../lib/ai-lab/constants';
import { loadDefaultPdfFile } from '../../lib/ai-lab/pdf';
import {
  downloadDualVerifyBothPassesFormattedExcel,
  downloadDualVerifyCombinedPdf,
  downloadDualVerifyDetailPdf,
  downloadDualVerifyExcel,
  downloadDualVerifyFormattedExcel,
  downloadDualVerifyPass1DetailPdf,
  downloadDualVerifySummaryPdf,
} from '../../lib/landing-ai/export-dual-verify-report';
import {
  filterComparableGovLeafPoints,
  filterComparableGovPoints,
  groupGovPointsByChapter,
  pointMatchesPrefix,
  type GovPoint as FilterGovPoint,
} from '../../lib/landing-ai/gov-point-filter';
import {
  agreementBadgeClass,
  type DualVerifyAgreement,
} from '../../lib/landing-ai/dual-verify-merge';
import {
  buildReportSummary,
  mergeReportItems,
  progressPointToReportItem,
  reportItemsToSortedArray,
  savedResultToReportItem,
  type DualVerifyReportItem,
} from '../../lib/landing-ai/dual-verify-report';
import {
  isKafkaSessionId,
  pushRecentKafkaSession,
  readRecentKafkaSessions,
  type KafkaRecentVariant,
} from '../../lib/landing-ai/kafka-recent-sessions';

export type KafkaDualVerifyWorkbenchProps = {
  /** UI skin — Reguliq uses dashboard links instead of landing-ai nav */
  variant?: KafkaRecentVariant;
  /** Optional API base (defaults to NEXT_PUBLIC_API_URL) */
  apiBase?: string;
  /** Wrap inner content (e.g. Reguliq AppShell) */
  wrapContent?: (children: ReactNode) => ReactNode;
  /** Pre-fill session ID from URL query */
  initialSessionId?: string;
  /** Hide outer min-h-screen when embedded in app shell */
  embedded?: boolean;
};

type GovPoint = {
  point_id: string;
  title?: string;
  text: string;
  section?: string;
};

type SessionProgress = {
  session: {
    id: string;
    status: string;
    totalPoints: number;
    completedPoints: number;
    failedPoints: number;
    runningPoints: number;
    queuedPoints: number;
    transport: 'kafka' | 'local';
    phase2Model: string;
  };
  points: Array<{
    id: string;
    pointId: string;
    pointTitle?: string;
    status: string;
    landingMessage?: string;
    llmMessage?: string;
    agreementJson?: DualVerifyAgreement;
    errorMessage?: string;
  }>;
};

type HealthData = {
  transport: 'kafka' | 'local';
  kafkaConfigured: boolean;
  topics: { jobs: string; retry: string; dlq: string; results: string };
  persistence?: {
    dualVerifyTablesReady: boolean;
    complianceSessionsTableReady: boolean;
    fileFallbackReady: boolean;
    fileDataDir: string;
    mode: 'supabase' | 'file' | 'memory';
    hint?: string;
  };
};

type CompareGranularity = 'section' | 'leaf';

type SavedAnalysisOption = {
  id: string;
  source: 'compliance' | 'kafka';
  label: string;
  comparedPoints?: number;
};

function filterGovByGranularity(
  points: FilterGovPoint[],
  granularity: CompareGranularity,
): GovPoint[] {
  const { comparable } =
    granularity === 'leaf'
      ? filterComparableGovLeafPoints(points)
      : filterComparableGovPoints(points);
  return comparable;
}

function sessionGranularityFor(granularity: CompareGranularity): string {
  return granularity === 'leaf' ? 'dual-leaf' : 'dual-section';
}

const GOV_FILE_HASH =
  'c84713f9aacd18415680356aeae47bcacff9c17458b5595b575400b12fe8f2ff';
const INTERNAL_FILE_HASH =
  '6a0a0bd13c7a32ea10c43c9a8391347a7e0caceaa0b17dd6443e9ee622111717';

/** Kafka-backed dual verify — async pipeline with combined report bag */
export function KafkaDualVerifyWorkbench({
  variant = 'landing-ai',
  apiBase = DEFAULT_API_BASE,
  wrapContent,
  initialSessionId,
  embedded = false,
}: KafkaDualVerifyWorkbenchProps) {
  const [health, setHealth] = useState<HealthData | null>(null);
  const [rawGovPoints, setRawGovPoints] = useState<FilterGovPoint[]>([]);
  const [granularity, setGranularity] = useState<CompareGranularity>('leaf');
  const [govPoints, setGovPoints] = useState<GovPoint[]>([]);
  const [selectedIds, setSelectedIds] = useState<Set<string>>(new Set());
  const [aiModel, setAiModel] = useState('gemini-2.5-flash-lite');
  const [internalFile, setInternalFile] = useState<File | null>(null);
  const [sessionId, setSessionId] = useState<string | null>(null);
  const [progress, setProgress] = useState<SessionProgress | null>(null);
  const [running, setRunning] = useState(false);
  const [error, setError] = useState('');
  const [loadingPoints, setLoadingPoints] = useState(false);
  const [forceRefresh, setForceRefresh] = useState(false);
  const [seeding, setSeeding] = useState(false);
  const [reportBag, setReportBag] = useState<Map<string, DualVerifyReportItem>>(
    new Map(),
  );
  const [savedAnalyses, setSavedAnalyses] = useState<SavedAnalysisOption[]>([]);
  const [selectedAnalysisId, setSelectedAnalysisId] = useState('');
  const [loadingAnalysis, setLoadingAnalysis] = useState(false);
  const [reportNote, setReportNote] = useState('');
  const [exporting, setExporting] = useState(false);
  const [manualSessionId, setManualSessionId] = useState(initialSessionId ?? '');
  const [savedAnalysisHint, setSavedAnalysisHint] = useState('');
  const [dbNote, setDbNote] = useState('');

  const sessionGranularity = sessionGranularityFor(granularity);

  useEffect(() => {
    void fetch(`${apiBase}/dual-verify-kafka/health`)
      .then((r) => r.json())
      .then((j) => setHealth(j.data as HealthData))
      .catch(() => setHealth(null));

    void loadDefaultPdfFile().then((f) => {
      if (f) setInternalFile(f);
    });
  }, [apiBase]);

  const applyGranularity = useCallback(
    (points: FilterGovPoint[], mode: CompareGranularity) => {
      const comparable = filterGovByGranularity(points, mode);
      setGovPoints(comparable);
      setSelectedIds(new Set());
    },
    [],
  );

  const loadGovPoints = useCallback(async () => {
    setLoadingPoints(true);
    setError('');
    try {
      const res = await fetch(
        `${apiBase}/landing-ai/stored-points?docId=gov-tfs-guidelines`,
      );
      const data = await res.json();
      if (!res.ok) throw new Error(data.message ?? 'Failed to load gov points');
      const points = (data.points ?? []) as FilterGovPoint[];
      setRawGovPoints(points);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Load failed');
    } finally {
      setLoadingPoints(false);
    }
  }, [apiBase]);

  const fetchSavedAnalyses = useCallback(async () => {
    try {
      const [dualLeafRes, dualSectionRes, kafkaRes] = await Promise.all([
        fetch(
          `${apiBase}/landing-ai/compliance-sessions?limit=30&granularity=dual-leaf`,
        ),
        fetch(
          `${apiBase}/landing-ai/compliance-sessions?limit=30&granularity=dual-section`,
        ),
        fetch(`${apiBase}/dual-verify-kafka/sessions`),
      ]);

      const options: SavedAnalysisOption[] = [];
      const seen = new Set<string>();

      if (dualLeafRes.ok) {
        const data = await dualLeafRes.json();
        if (typeof data.diagnostics?.hint === 'string') {
          setSavedAnalysisHint(data.diagnostics.hint);
        }
        for (const s of (data.sessions ?? []) as Array<{
          id: string;
          label: string;
          comparedPoints: number;
          source?: string;
        }>) {
          if (s.source === 'compare_cache') continue;
          const key = `compliance:${s.id}`;
          if (seen.has(key)) continue;
          seen.add(key);
          options.push({
            id: key,
            source: 'compliance',
            label: `[DB] ${s.label}`,
            comparedPoints: s.comparedPoints,
          });
        }
      }

      if (dualSectionRes.ok) {
        const data = await dualSectionRes.json();
        for (const s of (data.sessions ?? []) as Array<{
          id: string;
          label: string;
          comparedPoints: number;
          source?: string;
        }>) {
          if (s.source === 'compare_cache') continue;
          const key = `compliance:${s.id}`;
          if (seen.has(key)) continue;
          seen.add(key);
          options.push({
            id: key,
            source: 'compliance',
            label: `[DB] ${s.label}`,
            comparedPoints: s.comparedPoints,
          });
        }
      }

      if (kafkaRes.ok) {
        const json = await kafkaRes.json();
        for (const s of (json.data ?? []) as Array<{
          id: string;
          label: string;
          completedPoints: number;
        }>) {
          const key = `kafka:${s.id}`;
          if (seen.has(key)) continue;
          seen.add(key);
          options.push({
            id: key,
            source: 'kafka',
            label: `[Kafka] ${s.label}`,
            comparedPoints: s.completedPoints,
          });
        }
      }

      for (const s of readRecentKafkaSessions(variant)) {
        const key = `kafka:${s.id}`;
        if (seen.has(key)) continue;
        seen.add(key);
        options.unshift({
          id: key,
          source: 'kafka',
          label: `[Recent] ${s.label}`,
          comparedPoints: s.completedPoints,
        });
      }

      setSavedAnalyses(options);
      setSelectedAnalysisId((prev) => prev || options[0]?.id || '');
    } catch {
      /* optional */
    }
  }, [variant, apiBase]);

  useEffect(() => {
    void loadGovPoints();
  }, [loadGovPoints]);

  useEffect(() => {
    if (rawGovPoints.length > 0) {
      applyGranularity(rawGovPoints, granularity);
    }
  }, [granularity, rawGovPoints, applyGranularity]);

  useEffect(() => {
    void fetchSavedAnalyses();
  }, [fetchSavedAnalyses]);

  const recordRecentSession = useCallback((data: SessionProgress) => {
    pushRecentKafkaSession(
      {
        id: data.session.id,
        label: `${data.session.completedPoints}/${data.session.totalPoints} done · ${data.session.id.slice(0, 8)}…`,
        completedPoints: data.session.completedPoints,
        totalPoints: data.session.totalPoints,
      },
      variant,
    );
  }, [variant]);

  const loadKafkaSessionById = useCallback(
    async (id: string): Promise<DualVerifyReportItem[]> => {
      const res = await fetch(`${apiBase}/dual-verify-kafka/jobs/${id}`);
      const json = await res.json();
      if (!res.ok) {
        throw new Error(
          json.message ??
            'Session not found. If the API restarted, paste a session ID from a recent run in this browser, or run again.',
        );
      }
      const data = json.data as SessionProgress;
      recordRecentSession(data);
      return data.points
        .filter((p) => p.status === 'completed')
        .map((p) => progressPointToReportItem(p));
    },
    [recordRecentSession, apiBase],
  );

  const mergeProgressIntoReport = useCallback(
    (data: SessionProgress) => {
      const govById = new Map(govPoints.map((g) => [g.point_id, g]));
      const incoming = data.points.map((pt) => {
        const item = progressPointToReportItem(pt);
        const gov = govById.get(pt.pointId);
        if (gov) {
          item.govText = gov.text;
          item.pointTitle = item.pointTitle ?? gov.title;
        }
        return item;
      });
      setReportBag((prev) => mergeReportItems(prev, incoming));
    },
    [govPoints],
  );

  const persistReportToDb = useCallback(
    async (
      items: DualVerifyReportItem[],
      sessionData: SessionProgress['session'],
    ) => {
      const done = items.filter((i) => i.landingMessage && i.llmMessage);
      if (!done.length) return;

      try {
        const res = await fetch(`${apiBase}/landing-ai/compliance-sessions`, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({
            govFileHash: GOV_FILE_HASH,
            internalFileHash: INTERNAL_FILE_HASH,
            govFileName: 'TFS Guidelines.pdf',
            internalFileName: internalFile?.name ?? DEFAULT_PDF_NAME,
            totalGovPoints: govPoints.length,
            comparedPoints: done.length,
            skippedPoints: 0,
            compareGranularity: sessionGranularity,
            resultsJson: done.map((i) => ({
              point_id: i.pointId,
              title: i.pointTitle,
              text: i.govText,
              message: i.landingMessage,
              landingMessage: i.landingMessage,
              llmMessage: i.llmMessage,
              agreementJson: i.agreement,
            })),
            summaryJson: {
              pipeline: 'kafka-dual-verify',
              sessionId: sessionData.id,
              phase2Model: sessionData.phase2Model,
              transport: sessionData.transport,
            },
          }),
        });
        const data = (await res.json()) as {
          message?: string;
          comparedPoints?: number;
        };
        if (!res.ok) {
          throw new Error(data.message ?? 'DB save failed');
        }
        setDbNote(
          `Saved ${data.comparedPoints ?? done.length} points to Supabase — load from saved list anytime (0 credits)`,
        );
        void fetchSavedAnalyses();
      } catch (e) {
        setDbNote(
          `DB save: ${e instanceof Error ? e.message : 'failed'} — Kafka/disk tables may still have this session`,
        );
      }
    },
    [govPoints.length, internalFile, sessionGranularity, fetchSavedAnalyses],
  );

  const poll = useCallback(
    async (id: string) => {
      const interval = setInterval(async () => {
        try {
          const res = await fetch(`${apiBase}/dual-verify-kafka/jobs/${id}`);
          const json = await res.json();
          if (!res.ok) throw new Error(json.message ?? 'Poll failed');
          const data = json.data as SessionProgress;
          setProgress(data);
          mergeProgressIntoReport(data);
          if (
            data.session.status === 'completed' ||
            data.session.status === 'failed'
          ) {
            clearInterval(interval);
            setRunning(false);
            setReportNote(
              `Run finished — ${data.session.completedPoints} completed, ${data.session.failedPoints} failed. Combined report updated.`,
            );
            recordRecentSession(data);
            const govById = new Map(govPoints.map((g) => [g.point_id, g]));
            const mergedItems = data.points.map((pt) => {
              const item = progressPointToReportItem(pt);
              const gov = govById.get(pt.pointId);
              if (gov) {
                item.govText = gov.text;
                item.pointTitle = item.pointTitle ?? gov.title;
              }
              return item;
            });
            void persistReportToDb(mergedItems, data.session);
            void fetchSavedAnalyses();
          }
        } catch {
          clearInterval(interval);
          setRunning(false);
        }
      }, 2500);
      return () => clearInterval(interval);
    },
    [mergeProgressIntoReport, fetchSavedAnalyses, recordRecentSession, persistReportToDb, govPoints],
  );

  const seedBuiltin = async () => {
    setSeeding(true);
    setError('');
    try {
      const res = await fetch(`${apiBase}/landing-ai/seed/builtin`, {
        method: 'POST',
      });
      const data = await res.json();
      if (!res.ok) throw new Error(data.message ?? 'Seed failed');
      await loadGovPoints();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Seed failed');
    } finally {
      setSeeding(false);
    }
  };

  const persistenceOk =
    health?.persistence?.mode === 'supabase' ||
    health?.persistence?.mode === 'file';

  const startPipeline = async () => {
    if (!persistenceOk) {
      setError(
        'Cannot run — results would not be saved. Restart API after update, or apply Supabase migrations (002 + 003).',
      );
      return;
    }
    const ids = [...selectedIds];
    if (!ids.length) {
      setError('Select at least one gov point.');
      return;
    }
    if (!internalFile) {
      setError('Attach internal PDF for Phase 2 (Gemini).');
      return;
    }

    setRunning(true);
    setError('');
    setProgress(null);

    try {
      const seedRes = await fetch(`${apiBase}/landing-ai/seed/builtin`, {
        method: 'POST',
      });
      if (!seedRes.ok) {
        const seedErr = await seedRes.json().catch(() => ({}));
        console.warn('Seed skipped:', seedErr);
      }

      const form = new FormData();
      form.append('pointIds', JSON.stringify(ids));
      form.append('granularity', granularity);
      form.append('govDocId', 'gov-tfs-guidelines');
      form.append('internalDocId', 'internal-imptfs');
      form.append('phase2Model', aiModel);
      form.append('forceRefresh', String(forceRefresh));
      form.append('internalFile', internalFile);

      const res = await fetch(`${apiBase}/dual-verify-kafka/jobs`, {
        method: 'POST',
        body: form,
      });
      const json = await res.json();
      if (!res.ok) throw new Error(json.message ?? JSON.stringify(json));

      const sid = json.data.id as string;
      setSessionId(sid);
      pushRecentKafkaSession(
        {
          id: sid,
          label: `Started · ${ids.length} points · ${sid.slice(0, 8)}…`,
          completedPoints: 0,
          totalPoints: ids.length,
        },
        variant,
      );
      void fetchSavedAnalyses();
      setReportNote(
        `Started ${ids.length} point(s). Results merge into combined report (${reportBag.size} loaded).`,
      );
      void poll(sid);
    } catch (e) {
      setRunning(false);
      setError(e instanceof Error ? e.message : 'Start failed');
    }
  };

  const retryFailed = async () => {
    if (!sessionId) return;
    await fetch(`${apiBase}/dual-verify-kafka/jobs/${sessionId}/retry-failed`, {
      method: 'POST',
    });
    setRunning(true);
    void poll(sessionId);
  };

  const loadSelectedIntoReport = async () => {
    const manualId = manualSessionId.trim();
    const useManual = isKafkaSessionId(manualId);
    if (!useManual && !selectedAnalysisId) {
      setError('Select a saved session or paste a Kafka session ID.');
      return;
    }
    setLoadingAnalysis(true);
    setError('');
    try {
      let loaded: DualVerifyReportItem[] = [];

      if (useManual) {
        loaded = await loadKafkaSessionById(manualId);
      } else {
        const [source, id] = selectedAnalysisId.split(':');

        if (source === 'compliance') {
          const res = await fetch(
            `${apiBase}/landing-ai/compliance-sessions/${encodeURIComponent(id)}?granularity=${sessionGranularity}`,
          );
          const data = await res.json();
          if (!res.ok) throw new Error(data.message ?? 'Load failed');
          const results = (data.results ?? []) as Array<{
            point_id: string;
            title?: string;
            text?: string;
            message?: string;
            landingMessage?: string;
            llmMessage?: string;
            agreementJson?: DualVerifyAgreement;
          }>;
          loaded = results
            .map(savedResultToReportItem)
            .filter((r): r is DualVerifyReportItem => r != null);
        } else if (source === 'kafka') {
          loaded = await loadKafkaSessionById(id);
        }
      }

      if (!loaded.length) {
        throw new Error(
          'No completed dual verify results in this session. Wait for the run to finish, or check the session ID.',
        );
      }

      setReportBag((prev) => mergeReportItems(prev, loaded));
      setReportNote(
        `Loaded ${loaded.length} point(s) into combined report — run more points to accumulate (e.g. 6 + 50 = 56).`,
      );
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Load failed');
    } finally {
      setLoadingAnalysis(false);
    }
  };

  const canLoadSaved =
    Boolean(selectedAnalysisId) || isKafkaSessionId(manualSessionId.trim());

  const clearReportBag = () => {
    setReportBag(new Map());
    setReportNote('Combined report cleared.');
  };

  const exportFormattedExcel = async () => {
    setExporting(true);
    try {
      const items = reportItemsToSortedArray(reportBag);
      await downloadDualVerifyFormattedExcel(
        items,
        `dual-verify-formatted-${items.length}-points.xlsx`,
      );
    } finally {
      setExporting(false);
    }
  };

  const exportCombinedPdf = async () => {
    setExporting(true);
    try {
      const items = reportItemsToSortedArray(reportBag);
      const summary = buildReportSummary(items);
      await downloadDualVerifyCombinedPdf(
        items,
        summary,
        `dual-verify-both-passes-${items.length}-points.pdf`,
      );
    } finally {
      setExporting(false);
    }
  };

  const exportBothPassesExcel = async () => {
    setExporting(true);
    try {
      const items = reportItemsToSortedArray(reportBag);
      await downloadDualVerifyBothPassesFormattedExcel(
        items,
        `dual-verify-both-passes-${items.length}-points.xlsx`,
      );
    } finally {
      setExporting(false);
    }
  };

  const exportPass1Pdf = async () => {
    setExporting(true);
    try {
      const items = reportItemsToSortedArray(reportBag);
      const summary = buildReportSummary(items);
      await downloadDualVerifyPass1DetailPdf(
        items,
        summary,
        `dual-verify-pass1-${items.length}-points.pdf`,
      );
    } finally {
      setExporting(false);
    }
  };

  const exportExcel = async () => {
    setExporting(true);
    try {
      const items = reportItemsToSortedArray(reportBag);
      await downloadDualVerifyExcel(
        items,
        `dual-verify-kafka-${items.length}-points.xlsx`,
      );
    } finally {
      setExporting(false);
    }
  };

  const exportDetailPdf = async () => {
    setExporting(true);
    try {
      const items = reportItemsToSortedArray(reportBag);
      const summary = buildReportSummary(items);
      await downloadDualVerifyDetailPdf(
        items,
        summary,
        `dual-verify-detail-${items.length}-points.pdf`,
      );
    } finally {
      setExporting(false);
    }
  };

  const exportSummaryPdf = async () => {
    setExporting(true);
    try {
      const items = reportItemsToSortedArray(reportBag);
      const summary = buildReportSummary(items);
      await downloadDualVerifySummaryPdf(
        items,
        summary,
        `dual-verify-summary-${items.length}-points.pdf`,
      );
    } finally {
      setExporting(false);
    }
  };

  const togglePoint = (id: string) => {
    setSelectedIds((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  };

  const togglePointsByPrefix = (prefix: string) => {
    const matching = govPoints.filter((p) =>
      pointMatchesPrefix(p.point_id, prefix, p.section),
    );
    const allSelected = matching.every((p) => selectedIds.has(p.point_id));
    setSelectedIds((prev) => {
      const next = new Set(prev);
      for (const p of matching) {
        if (allSelected) next.delete(p.point_id);
        else next.add(p.point_id);
      }
      return next;
    });
  };

  const govByChapter = useMemo(
    () => groupGovPointsByChapter(govPoints),
    [govPoints],
  );

  const reportItems = useMemo(
    () => reportItemsToSortedArray(reportBag),
    [reportBag],
  );

  const pct =
    progress && progress.session.totalPoints > 0
      ? Math.round(
          ((progress.session.completedPoints + progress.session.failedPoints) /
            progress.session.totalPoints) *
            100,
        )
      : 0;

  const isReguliq = variant === 'reguliq';

  const inner = (
    <div className={embedded ? 'w-full' : 'mx-auto max-w-6xl px-4 py-8'}>
        <header className="mb-8 flex flex-wrap items-center justify-between gap-4">
          <div>
            <p className="text-xs uppercase tracking-wider text-emerald-400">
              {isReguliq ? 'Reguliq · Kafka Pipeline' : 'Kafka Pipeline'}
            </p>
            <h1 className="text-2xl font-bold">
              {isReguliq ? 'Dual Verify Analysis' : 'Dual Verify — Async (Kafka)'}
            </h1>
            <p className="mt-1 text-sm text-slate-400">
              Phase 1 Landing AI → Phase 2 Gemini — load saved points, run more,
              combined report with full detail + export.
            </p>
          </div>
          <div className="flex gap-2">
            {isReguliq ? (
              <>
                <Link
                  href="/analyse"
                  className="rounded-lg border border-white/10 px-3 py-2 text-sm hover:bg-white/5"
                >
                  ← Sync analyse
                </Link>
                <Link
                  href="/dashboard"
                  className="rounded-lg border border-white/10 px-3 py-2 text-sm hover:bg-white/5"
                >
                  Dashboard
                </Link>
              </>
            ) : (
              <>
                <Link
                  href="/landing-ai/dual-verify"
                  className="rounded-lg border border-white/10 px-3 py-2 text-sm hover:bg-white/5"
                >
                  ← Sync dual verify
                </Link>
                <Link
                  href="/"
                  className="rounded-lg border border-white/10 px-3 py-2 text-sm hover:bg-white/5"
                >
                  Home
                </Link>
              </>
            )}
          </div>
        </header>

        {health && (
          <div className="mb-6 space-y-2 rounded-xl border border-white/10 bg-white/5 p-4 text-sm">
            <p>
              Transport:{' '}
              <span className="font-medium text-emerald-400">{health.transport}</span>
              {health.persistence && (
                <span className="ml-3">
                  Save:{' '}
                  <span
                    className={
                      health.persistence.mode === 'memory'
                        ? 'font-medium text-red-400'
                        : health.persistence.mode === 'supabase'
                          ? 'font-medium text-emerald-400'
                          : 'font-medium text-sky-400'
                    }
                  >
                    {health.persistence.mode === 'supabase'
                      ? 'Supabase'
                      : health.persistence.mode === 'file'
                        ? 'disk'
                        : 'NONE — do not run'}
                  </span>
                </span>
              )}
            </p>
            <p className="text-slate-500">
              Topics: {health.topics.jobs} · {health.topics.retry} · {health.topics.dlq}
            </p>
            {health.persistence?.fileDataDir && health.persistence.mode === 'file' && (
              <p className="text-xs text-slate-500">
                Disk backup: {health.persistence.fileDataDir}
              </p>
            )}
            {health.persistence?.hint && (
              <p
                className={`rounded-lg border px-3 py-2 text-xs ${
                  health.persistence.mode === 'memory'
                    ? 'border-red-500/40 bg-red-500/10 text-red-200'
                    : 'border-amber-500/40 bg-amber-500/10 text-amber-200'
                }`}
              >
                {health.persistence.hint}
              </p>
            )}
          </div>
        )}

        {govPoints.length === 0 && !loadingPoints && (
          <div className="mb-4 rounded-lg border border-amber-500/30 bg-amber-500/10 p-4 text-sm">
            <p className="mb-2">Gov points not in Supabase yet.</p>
            <button
              type="button"
              disabled={seeding}
              onClick={() => void seedBuiltin()}
              className="rounded-lg bg-amber-600 px-3 py-1.5 text-sm font-medium"
            >
              {seeding ? 'Seeding…' : 'Seed builtin docs (free)'}
            </button>
          </div>
        )}

        {error && (
          <div className="mb-4 rounded-lg border border-red-500/40 bg-red-500/10 px-4 py-3 text-sm text-red-300">
            {error}
          </div>
        )}

        <section className="mb-6 rounded-xl border border-violet-500/30 bg-violet-500/10 p-4">
          <h2 className="font-medium text-violet-200">
            1. Load saved results (optional — combine before new run)
          </h2>
          <p className="mt-1 text-xs text-slate-400">
            Optional for a <strong className="text-slate-300">second batch</strong>:
            load a previous session, then run more points (6 + 50 = 56). On your
            first run, skip this — results appear in the combined report
            automatically as jobs finish.
          </p>
          {reportNote && (
            <p className="mt-2 text-xs font-medium text-emerald-300">{reportNote}</p>
          )}
          {savedAnalysisHint && (
            <p className="mt-2 text-xs text-amber-300/90">{savedAnalysisHint}</p>
          )}
          {savedAnalyses.length > 0 ? (
            <select
              value={selectedAnalysisId}
              onChange={(e) => setSelectedAnalysisId(e.target.value)}
              className="mt-3 w-full rounded-lg border border-white/10 bg-slate-900 px-3 py-2 text-sm"
            >
              {savedAnalyses.map((s) => (
                <option key={s.id} value={s.id}>
                  {s.label}
                </option>
              ))}
            </select>
          ) : (
            <p className="mt-2 text-xs text-slate-500">
              No DB/Kafka sessions listed yet. Paste a session ID below, or run a
              job — this browser remembers recent session IDs automatically.
            </p>
          )}
          <label className="mt-3 block text-xs text-slate-400">
            Or paste Kafka session ID
            <input
              type="text"
              value={manualSessionId}
              onChange={(e) => setManualSessionId(e.target.value)}
              placeholder="e.g. 77157b23-xxxx-xxxx-xxxx-xxxxxxxxxxxx"
              className="mt-1 w-full rounded-lg border border-white/10 bg-slate-900 px-3 py-2 font-mono text-sm text-emerald-300 placeholder:text-slate-600"
            />
          </label>
          {sessionId && (
            <button
              type="button"
              onClick={() => setManualSessionId(sessionId)}
              className="mt-2 text-xs text-violet-400 hover:underline"
            >
              Use current run session ({sessionId.slice(0, 8)}…)
            </button>
          )}
          <div className="mt-3 flex flex-wrap gap-2">
            <button
              type="button"
              disabled={!canLoadSaved || loadingAnalysis}
              onClick={() => void loadSelectedIntoReport()}
              className="rounded-lg border border-emerald-500/50 bg-emerald-600/20 px-3 py-1.5 text-sm font-medium text-emerald-200 hover:bg-emerald-600/30 disabled:opacity-40"
            >
              {loadingAnalysis ? 'Loading…' : 'Load into combined report'}
            </button>
            <button
              type="button"
              onClick={() => void fetchSavedAnalyses()}
              className="rounded-lg border border-white/10 px-3 py-1.5 text-sm text-slate-400 hover:bg-white/5"
            >
              Refresh list
            </button>
            <button
              type="button"
              onClick={clearReportBag}
              disabled={reportBag.size === 0}
              className="rounded-lg border border-white/10 px-3 py-1.5 text-sm text-slate-400 hover:bg-white/5 disabled:opacity-40"
            >
              Clear report ({reportBag.size})
            </button>
          </div>
        </section>

        <div className="grid gap-6 lg:grid-cols-3">
          <section className="space-y-4 lg:col-span-2">
            <div className="rounded-xl border border-white/10 bg-white/5 p-4">
              <div className="mb-3 flex flex-wrap items-center justify-between gap-2">
                <div>
                  <h2 className="font-medium">
                    2. Gov points ({granularity === 'leaf' ? 'leaf' : 'section'})
                  </h2>
                  <p className="text-xs text-slate-500">
                    {granularity === 'leaf'
                      ? 'Sub-clauses like 2.1.1, 2.1.2 — one Kafka job per leaf'
                      : 'Merged sections like 2.1, 2.2 — sub-points rolled up'}
                  </p>
                </div>
                <div className="flex items-center gap-2">
                  <div className="flex rounded-lg border border-white/10 p-0.5 text-xs">
                    <button
                      type="button"
                      onClick={() => setGranularity('section')}
                      className={`rounded-md px-2 py-1 ${
                        granularity === 'section'
                          ? 'bg-emerald-600 text-white'
                          : 'text-slate-400 hover:text-slate-200'
                      }`}
                    >
                      Section 2.1
                    </button>
                    <button
                      type="button"
                      onClick={() => setGranularity('leaf')}
                      className={`rounded-md px-2 py-1 ${
                        granularity === 'leaf'
                          ? 'bg-emerald-600 text-white'
                          : 'text-slate-400 hover:text-slate-200'
                      }`}
                    >
                      Leaf 2.1.1
                    </button>
                  </div>
                  <button
                    type="button"
                    onClick={() => void loadGovPoints()}
                    className="text-xs text-emerald-400 hover:underline"
                  >
                    Reload
                  </button>
                </div>
              </div>
              {loadingPoints ? (
                <p className="text-sm text-slate-500">Loading…</p>
              ) : (
                <>
                  <div className="mb-2 flex gap-3 text-xs">
                    <button
                      type="button"
                      onClick={() =>
                        setSelectedIds(new Set(govPoints.map((p) => p.point_id)))
                      }
                      className="text-emerald-400 hover:underline"
                    >
                      Select all ({govPoints.length})
                    </button>
                    <button
                      type="button"
                      onClick={() => setSelectedIds(new Set())}
                      className="text-slate-400 hover:underline"
                    >
                      Clear
                    </button>
                    <span className="text-slate-500">
                      {selectedIds.size} selected · report has {reportBag.size}
                    </span>
                  </div>
                  <ul className="max-h-[28rem] space-y-2 overflow-auto text-sm">
                    {govByChapter.map(({ chapter, points: chapterPoints, sections }) => {
                      const chapterAllSelected =
                        chapterPoints.length > 0 &&
                        chapterPoints.every((p) => selectedIds.has(p.point_id));

                      return (
                        <li
                          key={`chapter-${chapter}`}
                          className="rounded-lg border border-white/10 bg-black/20"
                        >
                          <div className="flex flex-wrap items-center justify-between gap-2 border-b border-white/5 px-3 py-2">
                            <span className="text-xs font-semibold uppercase tracking-wide text-slate-400">
                              §{chapter} · {chapterPoints.length} point
                              {chapterPoints.length === 1 ? '' : 's'}
                            </span>
                            <button
                              type="button"
                              onClick={() => togglePointsByPrefix(chapter)}
                              className="text-xs text-emerald-400 hover:underline"
                            >
                              {chapterAllSelected
                                ? `Deselect §${chapter}`
                                : `Select §${chapter}`}
                            </button>
                          </div>
                          <ul className="space-y-2 p-2">
                            {sections.map(({ key, points: sectionPoints }) => {
                              const showSectionBar =
                                sections.length > 1 || key !== chapter;
                              const sectionAllSelected =
                                sectionPoints.length > 0 &&
                                sectionPoints.every((p) =>
                                  selectedIds.has(p.point_id),
                                );
                              const inReport = sectionPoints.filter((p) =>
                                reportBag.has(p.point_id),
                              ).length;

                              return (
                                <li key={`section-${key}`}>
                                  {showSectionBar && (
                                    <div className="mb-1 flex items-center justify-between px-1">
                                      <span className="text-[11px] font-semibold text-slate-500">
                                        §{key}
                                        {inReport > 0 && (
                                          <span className="ml-1 text-emerald-500">
                                            ({inReport} in report)
                                          </span>
                                        )}
                                      </span>
                                      <button
                                        type="button"
                                        onClick={() => togglePointsByPrefix(key)}
                                        className="text-[11px] text-emerald-400 hover:underline"
                                      >
                                        {sectionAllSelected
                                          ? `Deselect §${key}`
                                          : `Select §${key}`}
                                      </button>
                                    </div>
                                  )}
                                  <ul className="space-y-1">
                                    {sectionPoints.map((p) => (
                                      <li key={p.point_id}>
                                        <label className="flex cursor-pointer items-start gap-2 rounded p-2 hover:bg-white/5">
                                          <input
                                            type="checkbox"
                                            checked={selectedIds.has(p.point_id)}
                                            onChange={() => togglePoint(p.point_id)}
                                            className="mt-1"
                                          />
                                          <span>
                                            <span className="font-mono text-emerald-400">
                                              {p.point_id}
                                            </span>
                                            {reportBag.has(p.point_id) && (
                                              <span className="ml-1 text-[10px] text-violet-400">
                                                ✓ report
                                              </span>
                                            )}
                                            {p.title && ` — ${p.title}`}
                                          </span>
                                        </label>
                                      </li>
                                    ))}
                                  </ul>
                                </li>
                              );
                            })}
                          </ul>
                        </li>
                      );
                    })}
                  </ul>
                </>
              )}
            </div>

            <div className="rounded-xl border border-white/10 bg-white/5 p-4">
              <h2 className="mb-3 font-medium">3. Phase 2 model & PDF</h2>
              <select
                value={aiModel}
                onChange={(e) => setAiModel(e.target.value)}
                className="w-full rounded-lg border border-white/10 bg-slate-900 px-3 py-2 text-sm"
              >
                {AI_MODELS.map((m) => (
                  <option key={m} value={m}>
                    {m}
                    {m === 'gemini-2.5-flash-lite'
                      ? ' (recommended · low cost)'
                      : ''}
                  </option>
                ))}
              </select>
              <label className="mt-3 flex cursor-pointer items-center gap-2 text-sm text-slate-400">
                <input
                  type="checkbox"
                  checked={forceRefresh}
                  onChange={(e) => setForceRefresh(e.target.checked)}
                />
                Force fresh Phase 1 (uses Landing AI credits)
              </label>
              <div className="mt-3">
                <input
                  type="file"
                  accept=".pdf"
                  onChange={(e) => setInternalFile(e.target.files?.[0] ?? null)}
                  className="text-sm"
                />
                <p className="mt-1 text-xs text-slate-500">
                  {internalFile?.name ?? DEFAULT_PDF_NAME}
                </p>
              </div>
            </div>

            <button
              type="button"
              disabled={running || !persistenceOk}
              onClick={() => void startPipeline()}
              className="w-full rounded-xl bg-emerald-600 py-3 font-medium disabled:opacity-40"
              title={
                !persistenceOk
                  ? 'Persistence not ready — results would be lost'
                  : undefined
              }
            >
              {running
                ? 'Pipeline running…'
                : `Run Kafka dual verify (${selectedIds.size} points → merges into ${reportBag.size} report)`}
            </button>
          </section>

          <aside className="rounded-xl border border-white/10 bg-white/5 p-4">
            <h2 className="mb-4 font-medium">Live progress</h2>
            {!progress && !sessionId && (
              <p className="text-sm text-slate-500">Start a job to see live progress.</p>
            )}
            {progress && (
              <>
                <p className="mb-2 text-sm">
                  Session{' '}
                  <span className="font-mono text-xs text-slate-400">
                    {progress.session.id.slice(0, 8)}…
                  </span>
                </p>
                <p className="mb-2 capitalize text-emerald-400">
                  {progress.session.status}
                </p>
                <div className="mb-3 h-2 overflow-hidden rounded-full bg-slate-800">
                  <div
                    className="h-full bg-emerald-500 transition-all"
                    style={{ width: `${pct}%` }}
                  />
                </div>
                <ul className="mb-4 space-y-1 text-xs text-slate-400">
                  <li>Completed: {progress.session.completedPoints}</li>
                  <li>Failed: {progress.session.failedPoints}</li>
                  <li>Running: {progress.session.runningPoints}</li>
                  <li>Queued: {progress.session.queuedPoints}</li>
                </ul>
                {progress.session.failedPoints > 0 && !running && (
                  <button
                    type="button"
                    onClick={() => void retryFailed()}
                    className="mb-4 w-full rounded-lg border border-amber-500/40 py-2 text-sm text-amber-300"
                  >
                    Retry failed points
                  </button>
                )}
                <ul className="max-h-64 space-y-1 overflow-auto text-xs">
                  {progress.points.map((pt) => (
                    <li
                      key={pt.id}
                      className="flex items-center justify-between gap-2 rounded border border-white/5 px-2 py-1"
                    >
                      <span className="font-mono text-emerald-400">{pt.pointId}</span>
                      <span className="capitalize text-slate-500">{pt.status}</span>
                      {pt.agreementJson && (
                        <span
                          className={`rounded border px-1 text-[9px] ${agreementBadgeClass(pt.agreementJson.status)}`}
                        >
                          {pt.agreementJson.label}
                        </span>
                      )}
                    </li>
                  ))}
                </ul>
              </>
            )}
          </aside>
        </div>

        {reportItems.length > 0 && (
          <DualVerifyReportPanel
            items={reportItems}
            sessionLabel={sessionId ?? undefined}
            dbNote={dbNote}
            exporting={exporting}
            onExportCombinedPdf={() => void exportCombinedPdf()}
            onExportBothPassesExcel={() => void exportBothPassesExcel()}
            onExportSummaryPdf={() => void exportSummaryPdf()}
            onExportDetailPdf={() => void exportDetailPdf()}
            onExportPass1Pdf={() => void exportPass1Pdf()}
            onExportExcel={() => void exportExcel()}
            onExportFormattedExcel={() => void exportFormattedExcel()}
          />
        )}
      </div>
  );

  const shellClass = embedded
    ? 'text-slate-100'
    : 'min-h-screen bg-slate-950 text-slate-100';

  if (wrapContent) {
    return <>{wrapContent(inner)}</>;
  }

  return <main className={shellClass}>{inner}</main>;
}
