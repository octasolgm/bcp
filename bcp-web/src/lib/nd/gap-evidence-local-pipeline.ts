import type { NdApiService, NdLocalExtractionResult } from '../../app/services/nd/nd-api.service';
import type { GapEvidencePrepStep } from './gap-evidence-activity';

/** Same engine as analyse-regul-full-v2 and /nd/internal-documents-azure-di. */
export const GAP_EVIDENCE_LOCAL_ENGINE = 'azure-di' as const;

const POLL_MS = 2500;
const MAX_WAIT_MS = 45 * 60_000;

function sleep(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

function isParsed(row: NdLocalExtractionResult | undefined): boolean {
  if (!row) return false;
  const st = (row.status ?? '').trim().toLowerCase();
  return st === 'parsed' || st === 'completed';
}

function isExtracted(row: NdLocalExtractionResult | undefined): boolean {
  if (!row) return false;
  const ex = (row.extractStatus ?? '').trim().toLowerCase();
  return ex === 'extracted' || ex === 'completed' || (row.sectionCount ?? 0) > 0;
}

function isIndexReady(row: NdLocalExtractionResult | undefined): boolean {
  if (!row) return true;
  const ix = (row.indexStatus ?? '').trim().toLowerCase();
  if (!ix) return true;
  if (ix === 'failed') return false;
  return ix === 'indexed' || ix === 'completed' || ix === 'skipped';
}

function isPrepActiveStatus(value: string | null | undefined): boolean {
  const s = (value ?? '').trim().toLowerCase();
  return s === 'processing' || s === 'pending' || s === 'running' || s === 'queued';
}

/** Uploaded file with no parse/extract/index work started yet. */
export function gapEvidenceLocalRowNeverStarted(row: NdLocalExtractionResult | undefined): boolean {
  if (!row) return true;
  if (statusFailed(row)) return false;
  if (isParsed(row) || isExtracted(row)) return false;
  const st = (row.status ?? '').trim().toLowerCase();
  const ex = (row.extractStatus ?? '').trim().toLowerCase();
  const ix = (row.indexStatus ?? '').trim().toLowerCase();
  if (isPrepActiveStatus(st) || isPrepActiveStatus(ex) || isPrepActiveStatus(ix)) return false;
  if (st === 'failed' || ex === 'failed' || ix === 'failed') return false;
  return !st && !ex && !ix && !(row.sectionCount ?? 0);
}

/** User-facing line when prepare has not run (or stopped before parse). */
export function gapEvidenceNotStartedDetail(row: NdLocalExtractionResult | undefined): string {
  if (!row || gapEvidenceLocalRowNeverStarted(row)) {
    return 'Not yet parsed or extracted';
  }
  if (!isParsed(row)) return 'Not yet parsed';
  if (!isExtracted(row)) return 'Not yet extracted';
  if (!isIndexReady(row)) return 'Not yet indexed for search';
  return 'Not yet parsed or extracted';
}

function statusFailed(row: NdLocalExtractionResult | undefined): string | null {
  if (!row) return null;
  const st = (row.status ?? '').trim().toLowerCase();
  if (st === 'failed') return row.error?.trim() || 'Parse failed';
  const ex = (row.extractStatus ?? '').trim().toLowerCase();
  if (ex === 'failed') return row.extractError?.trim() || 'Extract failed';
  const ix = (row.indexStatus ?? '').trim().toLowerCase();
  if (ix === 'failed') return row.indexError?.trim() || 'Index failed';
  return null;
}

async function fetchLite(
  ndApi: NdApiService,
  storedDocumentId: string,
): Promise<NdLocalExtractionResult | undefined> {
  const res = await ndApi.localExtractStatusBatch([storedDocumentId], GAP_EVIDENCE_LOCAL_ENGINE, {
    lite: true,
  });
  if (!res.success) return undefined;
  return res.data?.[storedDocumentId];
}

async function pollUntilReady(
  ndApi: NdApiService,
  storedDocumentId: string,
  phase: 'parse' | 'extract' | 'index',
  onProgress: (step: GapEvidencePrepStep, detail?: string) => void,
): Promise<{ ok: true; row: NdLocalExtractionResult } | { ok: false; message: string }> {
  const started = Date.now();
  while (Date.now() - started < MAX_WAIT_MS) {
    const row = await fetchLite(ndApi, storedDocumentId);
    const fail = statusFailed(row);
    if (fail) return { ok: false, message: fail };

    if (phase === 'parse') {
      onProgress('parsing', row?.status ?? 'processing');
      if (isParsed(row)) return { ok: true, row: row! };
    } else if (phase === 'extract') {
      onProgress('extracting', row?.extractStatus ?? 'processing');
      if (isExtracted(row)) return { ok: true, row: row! };
    } else {
      onProgress('indexing', row?.indexStatus ?? 'processing');
      if (isIndexReady(row) && isExtracted(row)) return { ok: true, row: row! };
    }

    await sleep(POLL_MS);
  }
  return { ok: false, message: 'Timed out waiting for document prepare — try again.' };
}

export type GapEvidencePipelineProgress = (step: GapEvidencePrepStep, detail?: string) => void;

/** Parse + structural extract through the ND local-documents API (Azure DI + local clause split). */
export async function runGapEvidenceLocalPipeline(
  ndApi: NdApiService,
  storedDocumentId: string,
  onProgress?: GapEvidencePipelineProgress,
): Promise<{ ok: true } | { ok: false; message: string }> {
  const progress = onProgress ?? (() => {});

  progress('parsing');
  const parseKick = await ndApi.localParseById(storedDocumentId, GAP_EVIDENCE_LOCAL_ENGINE);
  if (!parseKick.success) {
    return { ok: false, message: parseKick.message ?? 'Parse failed to start' };
  }
  if (parseKick.data?.status === 'failed') {
    return { ok: false, message: parseKick.data.error ?? 'Parse failed' };
  }

  if (!isParsed(parseKick.data)) {
    const parsed = await pollUntilReady(ndApi, storedDocumentId, 'parse', progress);
    if (!parsed.ok) return parsed;
  }

  progress('extracting');
  const extractKick = await ndApi.localExtractById(storedDocumentId, GAP_EVIDENCE_LOCAL_ENGINE);
  if (!extractKick.success) {
    return { ok: false, message: extractKick.message ?? 'Extract failed to start' };
  }
  if (extractKick.data?.extractStatus === 'failed') {
    return { ok: false, message: extractKick.data.extractError ?? 'Extract failed' };
  }

  if (!isExtracted(extractKick.data)) {
    const extracted = await pollUntilReady(ndApi, storedDocumentId, 'extract', progress);
    if (!extracted.ok) return extracted;
  }

  const lite = await fetchLite(ndApi, storedDocumentId);
  const ix = (lite?.indexStatus ?? '').trim().toLowerCase();
  if (ix === 'processing' || ix === 'pending') {
    const indexed = await pollUntilReady(ndApi, storedDocumentId, 'index', progress);
    if (!indexed.ok) return indexed;
  } else {
    progress('indexing');
  }

  progress('ready');
  return { ok: true };
}

/** Map local-doc status row to the step shown on gap evidence attachment rows. */
export function gapEvidencePrepStepFromLocalRow(
  row: NdLocalExtractionResult | undefined,
): GapEvidencePrepStep {
  if (gapEvidenceLocalRowNeverStarted(row)) return 'not_started';
  const fail = statusFailed(row);
  if (fail) return 'failed';
  if (!isParsed(row)) return 'parsing';
  if (!isExtracted(row)) return 'extracting';
  if (!isIndexReady(row)) return 'indexing';
  return 'ready';
}

export function gapEvidencePrepDetailFromLocalRow(
  row: NdLocalExtractionResult | undefined,
  step: GapEvidencePrepStep,
): string | undefined {
  if (step === 'parsing') return row?.status ?? undefined;
  if (step === 'extracting') return row?.extractStatus ?? undefined;
  if (step === 'indexing') return row?.indexStatus ?? undefined;
  if (step === 'failed') return statusFailed(row) ?? undefined;
  if (step === 'ready' && row) {
    const parts: string[] = [];
    if (row.totalPages) parts.push(`${row.totalPages} page${row.totalPages === 1 ? '' : 's'}`);
    if (row.sectionCount) parts.push(`${row.sectionCount} section${row.sectionCount === 1 ? '' : 's'}`);
    return parts.length ? parts.join(' · ') : undefined;
  }
  return undefined;
}

export function gapEvidencePrepInProgress(step: GapEvidencePrepStep): boolean {
  return step !== 'ready' && step !== 'failed' && step !== 'not_started';
}

export function gapEvidenceNeedsManualPrepare(step: GapEvidencePrepStep): boolean {
  return step === 'not_started' || step === 'failed';
}
