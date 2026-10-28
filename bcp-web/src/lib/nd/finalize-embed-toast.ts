import type { FinalizeEmbedSummary } from '../../app/services/nd/nd-api.service';

/** Short suffix for finalize/regenerate toasts so reviewers see whether embed targets were found. */
export function finalizeEmbedToastNote(embed?: FinalizeEmbedSummary | null): string {
  if (!embed) return '';
  const { resolvedActionPlans, embedTargets } = embed;
  if (resolvedActionPlans === 0) {
    return ' No resolved actions to embed — mark action plans resolved first.';
  }
  if (embedTargets === 0) {
    return ` ${resolvedActionPlans} resolved action(s) but none mapped to an internal document for embedding.`;
  }
  const docCount = embed.documents?.filter((d) => d.targetCount > 0).length ?? 0;
  return ` Embedded ${embedTargets} action note(s) across ${docCount || embed.documents?.length || 0} document(s). Search PDFs for POLICY UPDATE.`;
}
