export interface TempPointReviewComment {
  id: string;
  analysisPointId: string;
  comment: string;
  commentedBy?: string | null;
  commentedByName?: string | null;
  createdAt: string;
}

export type TempReviewCommentsChangeEvent = {
  analysisPointId: string;
  comments: TempPointReviewComment[];
};

export function tempCommentsForPoint(
  comments: TempPointReviewComment[],
  analysisPointId: string,
): TempPointReviewComment[] {
  return comments.filter((c) => c.analysisPointId === analysisPointId);
}

export function parseTempPointReviewComment(raw: unknown): TempPointReviewComment | null {
  if (!raw || typeof raw !== 'object') return null;
  const row = raw as Record<string, unknown>;
  const id = String(row['id'] ?? '').trim();
  const analysisPointId = String(row['analysisPointId'] ?? '').trim();
  const comment = String(row['comment'] ?? '').trim();
  if (!id || !analysisPointId || !comment) return null;
  return {
    id,
    analysisPointId,
    comment,
    commentedBy: row['commentedBy'] != null ? String(row['commentedBy']) : null,
    commentedByName: row['commentedByName'] != null ? String(row['commentedByName']) : null,
    createdAt: String(row['createdAt'] ?? new Date().toISOString()),
  };
}
