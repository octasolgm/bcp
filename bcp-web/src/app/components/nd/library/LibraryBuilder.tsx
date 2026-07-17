"use client";

import { useCallback, useEffect, useState } from "react";
import {
  getDocumentPoints,
  getRegulationDocuments,
} from "@/lib/api/bcp-api-client";
import { getClientToken } from "@/lib/auth/client-token";
import type { LibraryPointInput, RegulationDocument, RegulationPoint } from "@/lib/types";

type SelectedPoint = LibraryPointInput & {
  label: string;
};

export function LibraryBuilder({
  initialPoints = [],
  departmentId,
  onPointsChange,
}: {
  initialPoints?: LibraryPointInput[];
  departmentId?: string | null;
  onPointsChange: (points: LibraryPointInput[]) => void;
}) {
  const [docs, setDocs] = useState<RegulationDocument[]>([]);
  const [selectedDocId, setSelectedDocId] = useState<string | null>(null);
  const [docPoints, setDocPoints] = useState<RegulationPoint[]>([]);
  const [selected, setSelected] = useState<SelectedPoint[]>([]);
  const [loading, setLoading] = useState(true);
  const [pointsLoading, setPointsLoading] = useState(false);
  const [error, setError] = useState("");

  const syncParent = useCallback(
    (pts: SelectedPoint[]) => {
      onPointsChange(
        pts.map((p, i) => ({
          regulationPointId: p.regulationPointId,
          regulationDocumentId: p.regulationDocumentId,
          displayOrder: i + 1,
          pointSnapshot: p.pointSnapshot,
        })),
      );
    },
    [onPointsChange],
  );

  useEffect(() => {
    if (initialPoints.length === 0) return;
    const mapped: SelectedPoint[] = initialPoints.map((p) => {
      const snap = (p.pointSnapshot ?? {}) as Record<string, unknown>;
      const num = String(snap.pointNumber ?? "");
      const title = String(snap.pointTitle ?? "");
      return {
        ...p,
        label: `${num}${title ? ` — ${title}` : ""}`,
      };
    });
    setSelected(mapped);
  }, [initialPoints]);

  useEffect(() => {
    (async () => {
      const token = await getClientToken();
      if (!token) return;
      const res = await getRegulationDocuments(token, {
        departmentId: departmentId ?? undefined,
        status: "completed",
      });
      if (res.success && res.data) {
        setDocs(res.data as RegulationDocument[]);
      } else {
        setError(res.message ?? "Failed to load documents");
      }
      setLoading(false);
    })();
  }, [departmentId]);

  useEffect(() => {
    if (!selectedDocId) {
      setDocPoints([]);
      return;
    }
    (async () => {
      setPointsLoading(true);
      const token = await getClientToken();
      if (!token) return;
      const res = await getDocumentPoints(token, selectedDocId);
      if (res.success && res.data) {
        setDocPoints(res.data as RegulationPoint[]);
      }
      setPointsLoading(false);
    })();
  }, [selectedDocId]);

  function isSelected(pointId: string) {
    return selected.some((p) => p.regulationPointId === pointId);
  }

  function addPoint(pt: RegulationPoint) {
    if (!selectedDocId || isSelected(pt.id)) return;
    const snap = {
      pointNumber: pt.pointNumber,
      pointTitle: pt.pointTitle,
      pointContent: pt.pointContent,
      pageReference: pt.pageReference,
      regulationDocumentId: selectedDocId,
      regulationPointId: pt.id,
    };
    const next: SelectedPoint[] = [
      ...selected,
      {
        regulationPointId: pt.id,
        regulationDocumentId: selectedDocId,
        displayOrder: selected.length + 1,
        pointSnapshot: snap,
        label: `${pt.pointNumber}${pt.pointTitle ? ` — ${pt.pointTitle}` : ""}`,
      },
    ];
    setSelected(next);
    syncParent(next);
  }

  function removePoint(pointId: string) {
    const next = selected.filter((p) => p.regulationPointId !== pointId);
    setSelected(next);
    syncParent(next);
  }

  function movePoint(index: number, dir: -1 | 1) {
    const target = index + dir;
    if (target < 0 || target >= selected.length) return;
    const next = [...selected];
    [next[index], next[target]] = [next[target], next[index]];
    setSelected(next);
    syncParent(next);
  }

  if (loading) {
    return <p className="p-6 text-sm text-[var(--text-muted)]">Loading documents…</p>;
  }

  return (
    <div className="grid flex-1 grid-cols-1 gap-4 p-6 lg:grid-cols-3">
      <div className="card flex flex-col overflow-hidden">
        <div className="border-b border-[var(--border)] px-4 py-3 text-sm font-medium">
          Regulation documents
        </div>
        <div className="max-h-[28rem] flex-1 overflow-y-auto p-2">
          {docs.length === 0 ? (
            <p className="p-2 text-sm text-[var(--text-muted)]">No extracted documents</p>
          ) : (
            docs.map((d) => (
              <button
                key={d.id}
                type="button"
                onClick={() => setSelectedDocId(d.id)}
                className={`mb-1 w-full rounded-lg px-3 py-2 text-left text-sm ${
                  selectedDocId === d.id
                    ? "bg-[var(--accent)] text-[var(--accent-text)]"
                    : "hover:bg-[var(--bg-input)]"
                }`}
              >
                <div className="font-medium">{d.name}</div>
                <div className="text-xs opacity-80">{d.pointCount} points</div>
              </button>
            ))
          )}
        </div>
      </div>

      <div className="card flex flex-col overflow-hidden">
        <div className="border-b border-[var(--border)] px-4 py-3 text-sm font-medium">
          Points {selectedDocId ? `(${docPoints.length})` : ""}
        </div>
        <div className="max-h-[28rem] flex-1 overflow-y-auto p-2">
          {!selectedDocId ? (
            <p className="p-2 text-sm text-[var(--text-muted)]">Select a document</p>
          ) : pointsLoading ? (
            <p className="p-2 text-sm text-[var(--text-muted)]">Loading points…</p>
          ) : (
            docPoints.map((pt) => (
              <div
                key={pt.id}
                className="mb-2 rounded-lg border border-[var(--border)] p-3 text-sm"
              >
                <div className="flex items-start justify-between gap-2">
                  <div>
                    <div className="font-medium">
                      {pt.pointNumber}
                      {pt.pointTitle ? ` — ${pt.pointTitle}` : ""}
                    </div>
                    <p className="mt-1 line-clamp-2 text-xs text-[var(--text-muted)]">
                      {pt.pointContent}
                    </p>
                  </div>
                  <button
                    type="button"
                    className="btn-primary shrink-0 text-xs"
                    disabled={isSelected(pt.id)}
                    onClick={() => addPoint(pt)}
                  >
                    {isSelected(pt.id) ? "Added" : "Add"}
                  </button>
                </div>
              </div>
            ))
          )}
        </div>
      </div>

      <div className="card flex flex-col overflow-hidden">
        <div className="border-b border-[var(--border)] px-4 py-3 text-sm font-medium">
          Library selection ({selected.length})
        </div>
        <div className="max-h-[28rem] flex-1 overflow-y-auto p-2">
          {selected.length === 0 ? (
            <p className="p-2 text-sm text-[var(--text-muted)]">No points selected</p>
          ) : (
            selected.map((pt, i) => (
              <div
                key={pt.regulationPointId}
                className="mb-2 flex items-center gap-2 rounded-lg border border-[var(--border)] p-2 text-sm"
              >
                <span className="flex-1">{pt.label}</span>
                <button
                  type="button"
                  className="btn-secondary px-2 py-1 text-xs"
                  onClick={() => movePoint(i, -1)}
                  disabled={i === 0}
                >
                  ↑
                </button>
                <button
                  type="button"
                  className="btn-secondary px-2 py-1 text-xs"
                  onClick={() => movePoint(i, 1)}
                  disabled={i === selected.length - 1}
                >
                  ↓
                </button>
                <button
                  type="button"
                  className="btn-secondary px-2 py-1 text-xs text-red-400"
                  onClick={() => removePoint(pt.regulationPointId)}
                >
                  Remove
                </button>
              </div>
            ))
          )}
        </div>
      </div>
      {error && <p className="col-span-full text-sm text-red-400">{error}</p>}
    </div>
  );
}
