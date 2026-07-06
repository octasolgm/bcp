import {
  parseReferenceComplianceBlock,
  type ReferenceComplianceBlock,
} from '../ai-lab/parse-reference-response';

export type AgreementStatus =
  | 'aligned'
  | 'status_mismatch'
  | 'confidence_gap'
  | 'both_non_compliant'
  | 'landing_error'
  | 'llm_error';

export type DualVerifyAgreement = {
  status: AgreementStatus;
  label: string;
  landingStatus: string;
  llmStatus: string;
  landingConfidence: number | null;
  llmConfidence: number | null;
  confidenceDelta: number | null;
  summary: string;
};

function parseConfidence(block: ReferenceComplianceBlock): number | null {
  const m = block.confidence.match(/(\d+)/);
  return m ? Number(m[1]) : null;
}

function normalizeStatus(status: string): string {
  const s = status.trim();
  if (/^compliant$/i.test(s) && !/partial/i.test(s)) return 'Compliant';
  if (/partial/i.test(s)) return 'Partial Compliant';
  if (/non[- ]?compliant|^no$/i.test(s)) return 'Non-Compliant';
  return s || 'Unknown';
}

export function blockFromMessage(message: string): ReferenceComplianceBlock {
  return parseReferenceComplianceBlock(message.trim());
}

export function compareDualVerifyResults(
  landingMessage: string,
  llmMessage: string,
): DualVerifyAgreement {
  const landing = blockFromMessage(landingMessage);
  const llm = blockFromMessage(llmMessage);

  const landingStatus = normalizeStatus(landing.status);
  const llmStatus = normalizeStatus(llm.status);
  const landingConfidence = parseConfidence(landing);
  const llmConfidence = parseConfidence(llm);
  const confidenceDelta =
    landingConfidence != null && llmConfidence != null
      ? Math.abs(landingConfidence - llmConfidence)
      : null;

  if (landingStatus === llmStatus) {
    if (confidenceDelta != null && confidenceDelta > 15) {
      return {
        status: 'confidence_gap',
        label: 'Status match · confidence differs',
        landingStatus,
        llmStatus,
        landingConfidence,
        llmConfidence,
        confidenceDelta,
        summary: `Both report ${landingStatus}, but confidence differs by ${confidenceDelta} points (Landing ${landingConfidence}% vs LLM ${llmConfidence}%).`,
      };
    }
    return {
      status: 'aligned',
      label: 'Aligned',
      landingStatus,
      llmStatus,
      landingConfidence,
      llmConfidence,
      confidenceDelta,
      summary: `Both passes agree: ${landingStatus}${landingConfidence != null ? ` (${landingConfidence}%)` : ''}.`,
    };
  }

  if (
    landingStatus !== 'Compliant' &&
    llmStatus !== 'Compliant'
  ) {
    return {
      status: 'both_non_compliant',
      label: 'Both flag gaps (different severity)',
      landingStatus,
      llmStatus,
      landingConfidence,
      llmConfidence,
      confidenceDelta,
      summary: `Landing: ${landingStatus}; LLM: ${llmStatus}. Review both CAP notes.`,
    };
  }

  return {
    status: 'status_mismatch',
    label: 'Status mismatch',
    landingStatus,
    llmStatus,
    landingConfidence,
    llmConfidence,
    confidenceDelta,
    summary: `Landing AI: ${landingStatus}; Second pass: ${llmStatus}. Manual review required.`,
  };
}

export function agreementBadgeClass(status: AgreementStatus): string {
  switch (status) {
    case 'aligned':
      return 'bg-emerald-100 text-emerald-900 border-emerald-300';
    case 'confidence_gap':
      return 'bg-amber-100 text-amber-900 border-amber-300';
    case 'status_mismatch':
      return 'bg-red-100 text-red-900 border-red-300';
    case 'both_non_compliant':
      return 'bg-orange-100 text-orange-900 border-orange-300';
    default:
      return 'bg-slate-100 text-slate-800 border-slate-300';
  }
}
