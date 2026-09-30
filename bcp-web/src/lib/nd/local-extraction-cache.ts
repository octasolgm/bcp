import type { NdLocalExtractionResult } from '../../app/services/nd/nd-api.service';

/** Status-only poll payloads must not wipe parsed markdown / section text already in memory. */
export function mergeLiteLocalExtractionStatus(
  previous: NdLocalExtractionResult | undefined,
  lite: NdLocalExtractionResult,
): NdLocalExtractionResult {
  if (!previous || previous.lite === true) return lite;
  return {
    ...previous,
    status: lite.status,
    totalPages: lite.totalPages ?? previous.totalPages,
    ocrPageCount: lite.ocrPageCount ?? previous.ocrPageCount,
    error: lite.error ?? previous.error,
    parsedAt: lite.parsedAt ?? previous.parsedAt,
    extractStatus: lite.extractStatus,
    sectionCount: lite.sectionCount ?? previous.sectionCount,
    extractError: lite.extractError ?? previous.extractError,
    extractedAt: lite.extractedAt ?? previous.extractedAt,
    semanticExtractStatus: lite.semanticExtractStatus,
    semanticSectionCount: lite.semanticSectionCount ?? previous.semanticSectionCount,
    semanticExtractError: lite.semanticExtractError ?? previous.semanticExtractError,
    semanticExtractedAt: lite.semanticExtractedAt ?? previous.semanticExtractedAt,
    indexStatus: lite.indexStatus,
    indexError: lite.indexError ?? previous.indexError,
    indexedAt: lite.indexedAt ?? previous.indexedAt,
    structuralCoverageRatio: lite.structuralCoverageRatio ?? previous.structuralCoverageRatio,
    structuralCoverageOrphanSnippet:
      lite.structuralCoverageOrphanSnippet ?? previous.structuralCoverageOrphanSnippet,
    structuralCoverageLow: lite.structuralCoverageLow ?? previous.structuralCoverageLow,
  };
}

export function localExtractionHasSectionText(result: NdLocalExtractionResult | null | undefined): boolean {
  if (!result || result.lite === true) return false;
  return (result.sections?.length ?? 0) > 0 || (result.semanticSections?.length ?? 0) > 0;
}
