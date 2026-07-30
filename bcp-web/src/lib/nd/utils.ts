import {
  extractNumericClauseRef,
  resolveGovPointDisplayNumber,
  resolveGovPointListTitle,
  type GovPoint,
  type GovPointWithNumber,
} from "../gov-point-filter";
import type { AnalysisPoint, PointSnapshot } from "./types";

export type RunPointDisplayMeta = {
  analysisPointId: string;
  govKey: string;
  clause: string;
  title: string;
  snapshot: PointSnapshot;
};

function clauseDepth(clause: string): number {
  return clause.split('.').filter(Boolean).length;
}

/** Match a saved analysis snapshot to the best gov/library row (title/text), for old runs. */
export function matchGovPointForSnapshot(
  snap: PointSnapshot,
  catalog: GovPointWithNumber[],
  regulationPointId?: string | null,
): GovPointWithNumber | undefined {
  const title = (snap.pointTitle ?? '').trim().toLowerCase();
  const text = (snap.pointContent ?? '').trim().toLowerCase();
  const textHead = text.slice(0, 96);

  if (regulationPointId) {
    const regMatches = catalog.filter(
      (g) => (g as { regulationPointId?: string }).regulationPointId === regulationPointId,
    );
    if (regMatches.length === 1) return regMatches[0];
    if (regMatches.length > 1) {
      if (title) {
        const byTitle = regMatches.filter((g) => {
          const gt = (g.title ?? '').trim().toLowerCase();
          return gt === title || gt.startsWith(title.slice(0, 32)) || title.startsWith(gt.slice(0, 32));
        });
        if (byTitle.length === 1) return byTitle[0];
      }
      if (textHead.length >= 20) {
        const byText = regMatches.filter((g) => {
          const gt = (g.text ?? '').trim().toLowerCase();
          const head = textHead.slice(0, 48);
          return gt.startsWith(head) || head.startsWith(gt.slice(0, 48));
        });
        if (byText.length === 1) return byText[0];
      }
      regMatches.sort((a, b) => {
        const da = clauseDepth(resolveGovPointDisplayNumber(a) || '');
        const db = clauseDepth(resolveGovPointDisplayNumber(b) || '');
        return db - da;
      });
      return regMatches[0];
    }
  }

  if (title) {
    const byTitle = catalog.filter((g) => (g.title ?? '').trim().toLowerCase() === title);
    if (byTitle.length === 1) return byTitle[0];
    if (byTitle.length > 1 && textHead) {
      const narrowed = byTitle.find((g) =>
        (g.text ?? '').trim().toLowerCase().startsWith(textHead.slice(0, 48)),
      );
      if (narrowed) return narrowed;
    }
    const byTitlePrefix = catalog.filter((g) => {
      const gt = (g.title ?? '').trim().toLowerCase();
      return (
        gt.startsWith(title.slice(0, Math.min(32, title.length))) ||
        title.startsWith(gt.slice(0, Math.min(32, gt.length)))
      );
    });
    if (byTitlePrefix.length === 1) return byTitlePrefix[0];
  }

  if (textHead.length >= 20) {
    const byText = catalog.filter((g) => {
      const gt = (g.text ?? '').trim().toLowerCase();
      if (!gt) return false;
      const head = textHead.slice(0, 48);
      return gt.startsWith(head) || head.startsWith(gt.slice(0, 48));
    });
    if (byText.length === 1) return byText[0];
    if (byText.length > 1) {
      let pool = byText;
      if (regulationPointId) {
        const reg = pool.filter(
          (g) => (g as { regulationPointId?: string }).regulationPointId === regulationPointId,
        );
        if (reg.length) pool = reg;
      }
      pool.sort((a, b) => {
        const da = clauseDepth(resolveGovPointDisplayNumber(a) || '');
        const db = clauseDepth(resolveGovPointDisplayNumber(b) || '');
        return db - da;
      });
      return pool[0];
    }
  }

  const snapClause = resolveSnapshotDisplayNumber(snap, regulationPointId);
  if (snapClause) {
    const byClause = catalog.find((g) => resolveGovPointDisplayNumber(g) === snapClause);
    if (byClause) return byClause;
  }

  return undefined;
}

/** Resolve clause number + title for a saved analysis row using the live gov catalog. */
export function resolveRunPointDisplayMeta(
  point: AnalysisPoint,
  catalog: GovPointWithNumber[],
): RunPointDisplayMeta {
  const raw = parsePointSnapshot(point.pointSnapshot);
  const gov = matchGovPointForSnapshot(raw, catalog, point.regulationPointId);
  const snapshot = hydratePointSnapshotFromGov(raw, gov ?? null, point.regulationPointId);
  const clause =
    (gov ? resolveGovPointDisplayNumber(gov) : '') ||
    resolveSnapshotDisplayNumber(snapshot, point.regulationPointId) ||
    extractNumericClauseRef(snapshot.pointTitle) ||
    extractNumericClauseRef(snapshot.pointContent) ||
    '';
  const safeClause = clause && !isUuidLike(clause) ? clause : '';
  const title =
    (gov?.title ?? snapshot.pointTitle ?? '').trim() ||
    (gov ? resolveGovPointListTitle(gov) : '') ||
    (snapshot.pointTitle ?? '').trim();
  const govKey =
    safeClause ||
    (gov?.point_id && !isUuidLike(gov.point_id) ? gov.point_id : '') ||
    point.id;
  return {
    analysisPointId: point.id,
    govKey,
    clause: safeClause,
    title,
    snapshot: safeClause ? { ...snapshot, pointNumber: safeClause } : snapshot,
  };
}

export function formatDate(iso?: string | null): string {
  if (!iso) return "—";
  return new Date(iso).toLocaleString();
}

export function formatBytes(bytes?: number): string {
  if (!bytes) return "—";
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

/** Regulation point label for exports — numeric id (e.g. 3.1.1), never a UUID. */
export function resolveAnalysisPointDisplayNumber(
  point: AnalysisPoint,
  snap?: PointSnapshot,
): string {
  const s = snap ?? parsePointSnapshot(point.pointSnapshot);
  return resolveSnapshotDisplayNumber(s, point.regulationPointId || point.id);
}

export function resolveSnapshotDisplayNumber(
  snap: PointSnapshot,
  regulationPointId?: string | null,
): string {
  const pointNumber =
    snap.pointNumber?.trim() && !isUuidLike(snap.pointNumber) ? snap.pointNumber.trim() : undefined;
  const resolved = resolveGovPointDisplayNumber({
    point_id: isUuidLike(snap.regulationPointId ?? "")
      ? ""
      : snap.regulationPointId || "",
    pointNumber,
    title: snap.pointTitle ?? "",
    text: snap.pointContent ?? "",
    section: snap.pageReference ?? "",
  });
  const safeResolved = safeClauseNumber(resolved);
  if (safeResolved) return safeResolved;
  return (
    safeClauseNumber(extractNumericClauseRef(snap.pointTitle)) ||
    safeClauseNumber(extractNumericClauseRef(snap.pointContent)) ||
    safeClauseNumber(extractNumericClauseRef(snap.pageReference)) ||
    ""
  );
}

/** Replace UUID / junk pointNumber in a stored snapshot with the resolved clause id. */
export function normalizePointSnapshotLabels(
  snap: PointSnapshot,
  regulationPointId?: string | null,
): PointSnapshot {
  const resolved = resolveSnapshotDisplayNumber(snap, regulationPointId);
  if (!resolved || snap.pointNumber === resolved) return snap;
  return { ...snap, pointNumber: resolved };
}

/** Prefer full regulation text from the live gov point over a short frozen snapshot. */
export function hydratePointSnapshotFromGov(
  snap: PointSnapshot,
  gov?: (Pick<GovPoint, "title" | "text" | "section"> & { pointNumber?: string }) | null,
  regulationPointId?: string | null,
): PointSnapshot {
  let next = normalizePointSnapshotLabels(snap, regulationPointId);
  if (!gov?.text?.trim()) return next;

  const govText = gov.text.trim();
  const snapText = (next.pointContent ?? "").trim();
  if (govText.length > snapText.length) {
    next = {
      ...next,
      pointContent: govText,
      pointTitle: next.pointTitle?.trim() || gov.title?.trim() || next.pointTitle,
      pageReference: next.pageReference?.trim() || gov.section?.trim() || next.pageReference,
    };
  }

  const resolvedNum = resolveGovPointDisplayNumber({
    point_id: next.regulationPointId || regulationPointId || "",
    pointNumber: gov.pointNumber ?? next.pointNumber,
    title: gov.title ?? next.pointTitle ?? "",
    text: govText,
    section: gov.section ?? next.pageReference ?? "",
  });
  if (resolvedNum) next = { ...next, pointNumber: resolvedNum };
  return next;
}

export function regulatoryRequirementText(
  snap: PointSnapshot | null | undefined,
  gov?: (Pick<GovPoint, "title" | "text">) | null,
): string {
  const fromSnap = snap?.pointContent?.trim() ?? "";
  const fromGov = gov?.text?.trim() ?? "";
  if (fromGov.length > fromSnap.length) return fromGov;
  if (fromSnap) return fromSnap;
  return snap?.pointTitle?.trim() || gov?.title?.trim() || "";
}

export function isUuidLike(value: string): boolean {
  const v = value.trim();
  return (
    /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(v) ||
    /^[0-9a-f]{32}$/i.test(v)
  );
}

function safeClauseNumber(value: string | null | undefined): string {
  const v = (value ?? '').trim();
  return v && !isUuidLike(v) ? v : '';
}

export function parsePointSnapshot(raw?: string | null): PointSnapshot {
  if (!raw) return {};
  try {
    const parsed = JSON.parse(raw) as PointSnapshot & { pdfPage?: number | string | null };
    if (typeof parsed.pdfPage === 'string') {
      const n = Number.parseInt(parsed.pdfPage, 10);
      parsed.pdfPage = Number.isFinite(n) && n > 0 ? n : null;
    }
    return parsed;
  } catch {
    return {};
  }
}

export function statusBadgeClass(status: string): string {
  const map: Record<string, string> = {
    completed: "badge-green",
    compliant: "badge-green",
    checker_approved: "badge-green",
    reviewer_approved: "badge-green",
    pending: "badge-gray",
    draft: "badge-gray",
    processing: "badge-blue",
    running: "badge-blue",
    submitted_for_review: "badge-blue",
    partial_compliant: "badge-amber",
    dual_verify_failed: "badge-amber",
    failed: "badge-red",
    non_compliant: "badge-red",
    pulled_back: "badge-red",
    error: "badge-red",
  };
  return map[status] ?? "badge-gray";
}

export function formatStatus(status: string): string {
  return status.replace(/_/g, " ");
}
