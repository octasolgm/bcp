import {
  COMPLIANCE_SEVERITY_LABELS,
  complianceSeverityLabel,
  type ComplianceSeverity,
} from '../../lib/nd/point-compliance-status';

/** Shared local persistence for Reguliq gap-analysis working document. */

/** Gap analysis uses three compliance outcomes only. */
export type GapSeverity = ComplianceSeverity;

export function gapSeverityLabel(severity: GapSeverity | string): string {
  const normalized = normalizeGapSeverity(String(severity));
  return complianceSeverityLabel(normalized);
}

export function gapSeverityShortLabel(severity: GapSeverity | string): string {
  return gapSeverityLabel(severity);
}

export { COMPLIANCE_SEVERITY_LABELS };

/** Map legacy 5-tier severities and free-text to the 3 compliance statuses. */
export function normalizeGapSeverity(value: string): GapSeverity {
  const v = value.trim().toLowerCase().replace(/\s+/g, '_');
  if (v === 'compliant' || v === 'compliance') return 'compliant';
  if (v === 'partial_compliant' || v === 'partial' || v === 'partial_compliance') return 'partial_compliant';
  if (v === 'non_compliant' || v === 'non_compliance' || v === 'non-compliant') return 'non_compliant';
  if (v === 'critical' || v === 'high') return 'non_compliant';
  if (v === 'medium' || v === 'low') return 'partial_compliant';
  return 'partial_compliant';
}

export type GapItemData = {
  id: string;
  section: string;
  title: string;
  severity: GapSeverity;
  signedOff?: boolean;
  expanded?: boolean;
  regulatoryText: string;
  policyText: string;
  regPage: string;
  policyPage: string;
  gaps: string;
  managementResponse: string;
  designEffectiveness: string;
  operatingEffectiveness: string;
  overallEffectiveness: string;
  documentReference: string;
  evidence: string;
  /** Parsed corrective-action gap items for this point (0 = none). */
  gapCount?: number;
};

const STORAGE_KEY = 'reguliq-gap-report';
const DRAFT_KEY = 'reguliq-gap-drafts';
const DOCUMENTS_KEY = 'reguliq-documents';

/** No demo gaps — load from dual-verify / Analyse runs instead. */
export const DEFAULT_GAP_ITEMS: GapItemData[] = [];

export type GapDraftOverlay = {
  gaps?: string;
  managementResponse?: string;
  designEffectiveness?: string;
  operatingEffectiveness?: string;
  overallEffectiveness?: string;
  documentReference?: string;
  evidence?: string;
  signedOff?: boolean;
  expanded?: boolean;
};

export type UploadedDocument = {
  id: string;
  title: string;
  category: string;
  pages: number;
  uploaded: string;
  version: string;
  status: 'gaps' | 'reviewed' | 'compliant' | 'review-due';
  gapCount?: number;
  filter: string;
  fileType: 'PDF' | 'DOC' | 'XLS';
};

/** @deprecated Legacy full-item cache — prefer session + draft overlays. */
export function loadGapItems(): GapItemData[] {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    if (!raw) return [];
    const parsed = JSON.parse(raw) as GapItemData[];
    if (!Array.isArray(parsed)) return [];
    return parsed;
  } catch {
    return [];
  }
}

export function saveGapItems(items: GapItemData[]): void {
  localStorage.setItem(STORAGE_KEY, JSON.stringify(items));
}

export function clearGapItems(): void {
  localStorage.removeItem(STORAGE_KEY);
}

/** Per-session human edits keyed by point id. */
export function loadGapDrafts(sessionKey: string): Record<string, GapDraftOverlay> {
  try {
    const raw = localStorage.getItem(DRAFT_KEY);
    if (!raw) return {};
    const all = JSON.parse(raw) as Record<string, Record<string, GapDraftOverlay>>;
    return all[sessionKey] ?? {};
  } catch {
    return {};
  }
}

export function saveGapDrafts(
  sessionKey: string,
  overlays: Record<string, GapDraftOverlay>,
): void {
  try {
    const raw = localStorage.getItem(DRAFT_KEY);
    const all = raw
      ? (JSON.parse(raw) as Record<string, Record<string, GapDraftOverlay>>)
      : {};
    all[sessionKey] = overlays;
    localStorage.setItem(DRAFT_KEY, JSON.stringify(all));
  } catch {
    /* ignore quota */
  }
}

export function clearGapDrafts(sessionKey: string): void {
  try {
    const raw = localStorage.getItem(DRAFT_KEY);
    if (!raw) return;
    const all = JSON.parse(raw) as Record<string, Record<string, GapDraftOverlay>>;
    delete all[sessionKey];
    localStorage.setItem(DRAFT_KEY, JSON.stringify(all));
  } catch {
    /* ignore */
  }
}

export function loadExtraDocuments(): UploadedDocument[] {
  try {
    const raw = localStorage.getItem(DOCUMENTS_KEY);
    if (!raw) return [];
    return JSON.parse(raw) as UploadedDocument[];
  } catch {
    return [];
  }
}

export function saveExtraDocuments(docs: UploadedDocument[]): void {
  localStorage.setItem(DOCUMENTS_KEY, JSON.stringify(docs));
}
