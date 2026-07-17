"use client";

import { useEffect, useState } from "react";
import { useRouter } from "next/navigation";
import {
  createAnalysisRun,
  getInternalDocuments,
  getLibraries,
  getLibrary,
  startAnalysisRun,
} from "@/lib/api/bcp-api-client";
import { getClientToken } from "@/lib/auth/client-token";
import { LibraryBuilder } from "@/components/library/LibraryBuilder";
import type { InternalDocument, LibraryPointInput, LibrarySummary } from "@/lib/types";

type Mode = "library" | "manual";

export function RunAnalysisForm({ departmentId }: { departmentId?: string | null }) {
  const router = useRouter();
  const [step, setStep] = useState(1);
  const [mode, setMode] = useState<Mode>("library");
  const [libraries, setLibraries] = useState<LibrarySummary[]>([]);
  const [selectedLibraryId, setSelectedLibraryId] = useState("");
  const [manualPoints, setManualPoints] = useState<LibraryPointInput[]>([]);
  const [internalDocs, setInternalDocs] = useState<InternalDocument[]>([]);
  const [selectedDocIds, setSelectedDocIds] = useState<string[]>([]);
  const [name, setName] = useState("");
  const [description, setDescription] = useState("");
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState("");

  useEffect(() => {
    (async () => {
      const token = await getClientToken();
      if (!token) return;
      const [libRes, docRes] = await Promise.all([
        getLibraries(token, departmentId ?? undefined),
        getInternalDocuments(token),
      ]);
      if (libRes.success && libRes.data) setLibraries(libRes.data as LibrarySummary[]);
      if (docRes.success && docRes.data) setInternalDocs(docRes.data as InternalDocument[]);
    })();
  }, [departmentId]);

  const pointsSnapshot =
    mode === "library" && selectedLibraryId
      ? null // loaded on submit
      : manualPoints.map((p) => ({
          regulationPointId: p.regulationPointId,
          regulationDocumentId: p.regulationDocumentId,
          ...p.pointSnapshot,
        }));

  const regDocIds =
    mode === "manual"
      ? [...new Set(manualPoints.map((p) => p.regulationDocumentId))]
      : [];

  async function handleSubmit() {
    setError("");
    setLoading(true);
    const token = await getClientToken();
    if (!token) {
      setError("Not authenticated");
      setLoading(false);
      return;
    }

    let points: unknown[] = pointsSnapshot ?? [];
    let libraryId: string | undefined;
    let regulationDocIds = regDocIds;

    if (mode === "library") {
      if (!selectedLibraryId) {
        setError("Select a library");
        setLoading(false);
        return;
      }
      const libRes = await getLibrary(token, selectedLibraryId);
      if (!libRes.success || !libRes.data) {
        setError(libRes.message ?? "Failed to load library");
        setLoading(false);
        return;
      }
      const data = libRes.data as {
        library: { id: string };
        points: { regulationPointId: string; regulationDocumentId: string; pointSnapshot?: string }[];
      };
      libraryId = data.library.id;
      points = data.points.map((p) => {
        const snap = p.pointSnapshot ? JSON.parse(p.pointSnapshot) : {};
        return {
          regulationPointId: p.regulationPointId,
          regulationDocumentId: p.regulationDocumentId,
          ...snap,
        };
      });
      regulationDocIds = [...new Set(data.points.map((p) => p.regulationDocumentId))];
    }

    if (points.length === 0) {
      setError("No regulation points selected");
      setLoading(false);
      return;
    }
    if (!name.trim()) {
      setError("Run name is required");
      setLoading(false);
      return;
    }

    const createRes = await createAnalysisRun(token, {
      name: name.trim(),
      description: description.trim() || null,
      libraryId: libraryId ?? null,
      departmentId: departmentId ?? null,
      selectedPointsSnapshot: points,
      selectedInternalDocIds: selectedDocIds,
      selectedRegulationDocIds: regulationDocIds,
    });

    if (!createRes.success || !createRes.data?.id) {
      setError(createRes.message ?? "Failed to create run");
      setLoading(false);
      return;
    }

    const runId = createRes.data.id;
    await startAnalysisRun(token, runId);
    router.push(`/run-analysis/${runId}`);
  }

  function toggleDoc(id: string) {
    setSelectedDocIds((prev) =>
      prev.includes(id) ? prev.filter((x) => x !== id) : [...prev, id],
    );
  }

  const canNextStep1 =
    mode === "library" ? !!selectedLibraryId : manualPoints.length > 0;

  return (
    <div className="flex flex-1 flex-col">
      <div className="flex gap-2 border-b border-[var(--border)] px-6 py-4">
        {[1, 2, 3].map((s) => (
          <button
            key={s}
            type="button"
            className={`rounded-lg px-4 py-2 text-sm ${
              step === s ? "bg-[var(--accent)] text-[var(--accent-text)]" : "btn-secondary"
            }`}
            onClick={() => s < step && setStep(s)}
          >
            {s === 1 ? "Points" : s === 2 ? "Internal docs" : "Review"}
          </button>
        ))}
      </div>

      {step === 1 && (
        <div className="flex flex-1 flex-col p-6">
          <div className="mb-4 flex gap-4">
            <label className="flex items-center gap-2 text-sm">
              <input
                type="radio"
                checked={mode === "library"}
                onChange={() => setMode("library")}
              />
              Use library
            </label>
            <label className="flex items-center gap-2 text-sm">
              <input
                type="radio"
                checked={mode === "manual"}
                onChange={() => setMode("manual")}
              />
              Manual point selection
            </label>
          </div>

          {mode === "library" ? (
            <div className="card max-w-lg p-4">
              <label className="block text-sm">
                Library
                <select
                  className="input mt-1"
                  value={selectedLibraryId}
                  onChange={(e) => setSelectedLibraryId(e.target.value)}
                >
                  <option value="">Select library…</option>
                  {libraries.map((l) => (
                    <option key={l.id} value={l.id}>
                      {l.name} ({l.pointCount} points)
                    </option>
                  ))}
                </select>
              </label>
            </div>
          ) : (
            <LibraryBuilder
              departmentId={departmentId}
              onPointsChange={setManualPoints}
            />
          )}

          <div className="mt-6 flex justify-end">
            <button
              type="button"
              className="btn-primary"
              disabled={!canNextStep1}
              onClick={() => setStep(2)}
            >
              Next
            </button>
          </div>
        </div>
      )}

      {step === 2 && (
        <div className="flex flex-1 flex-col p-6">
          <p className="mb-4 text-sm text-[var(--text-muted)]">
            Select internal documents to include in the compliance analysis.
          </p>
          <div className="grid gap-3 md:grid-cols-2">
            {internalDocs.map((d) => (
              <label
                key={d.id}
                className={`card flex cursor-pointer items-center gap-3 p-4 text-sm ${
                  selectedDocIds.includes(d.id) ? "ring-1 ring-[var(--accent)]" : ""
                }`}
              >
                <input
                  type="checkbox"
                  checked={selectedDocIds.includes(d.id)}
                  onChange={() => toggleDoc(d.id)}
                />
                <div>
                  <div className="font-medium">{d.title}</div>
                  <div className="text-xs text-[var(--text-muted)]">{d.originalFileName}</div>
                </div>
              </label>
            ))}
          </div>
          {internalDocs.length === 0 && (
            <p className="text-sm text-[var(--text-muted)]">No internal documents uploaded yet.</p>
          )}
          <div className="mt-6 flex justify-between">
            <button type="button" className="btn-secondary" onClick={() => setStep(1)}>
              Back
            </button>
            <button type="button" className="btn-primary" onClick={() => setStep(3)}>
              Next
            </button>
          </div>
        </div>
      )}

      {step === 3 && (
        <div className="flex flex-1 flex-col p-6">
          <div className="card max-w-lg space-y-4 p-6">
            <label className="block text-sm">
              Run name
              <input
                className="input mt-1"
                value={name}
                onChange={(e) => setName(e.target.value)}
                placeholder="Q1 compliance review"
              />
            </label>
            <label className="block text-sm">
              Description (optional)
              <textarea
                className="input mt-1 min-h-20"
                value={description}
                onChange={(e) => setDescription(e.target.value)}
              />
            </label>
            <dl className="space-y-2 text-sm">
              <div className="flex justify-between">
                <dt className="text-[var(--text-muted)]">Point source</dt>
                <dd>{mode === "library" ? "Library" : "Manual"}</dd>
              </div>
              <div className="flex justify-between">
                <dt className="text-[var(--text-muted)]">Internal documents</dt>
                <dd>{selectedDocIds.length}</dd>
              </div>
            </dl>
          </div>
          {error && <p className="mt-4 text-sm text-red-400">{error}</p>}
          <div className="mt-6 flex justify-between">
            <button type="button" className="btn-secondary" onClick={() => setStep(2)}>
              Back
            </button>
            <button type="button" className="btn-primary" disabled={loading} onClick={handleSubmit}>
              {loading ? "Starting…" : "Create & start analysis"}
            </button>
          </div>
        </div>
      )}
    </div>
  );
}
