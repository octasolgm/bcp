"use client";

import { use, useEffect, useState } from "react";
import Link from "next/link";
import { TopNav } from "@/components/shell/TopNav";
import { LibraryBuilder } from "@/components/library/LibraryBuilder";
import { getLibrary, getProfile, updateLibrary } from "@/lib/api/bcp-api-client";
import type { UserProfile } from "@/lib/api/bcp-api-client";
import { getClientToken } from "@/lib/auth/client-token";
import type { LibraryPointInput } from "@/lib/types";

export default function EditLibraryPage({
  params,
}: {
  params: Promise<{ libraryId: string }>;
}) {
  const { libraryId } = use(params);
  const [profile, setProfile] = useState<UserProfile | null>(null);
  const [name, setName] = useState("");
  const [description, setDescription] = useState("");
  const [initialPoints, setInitialPoints] = useState<LibraryPointInput[]>([]);
  const [points, setPoints] = useState<LibraryPointInput[]>([]);
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState("");

  useEffect(() => {
    (async () => {
      const token = await getClientToken();
      if (!token) return;
      const [profRes, libRes] = await Promise.all([
        getProfile(token),
        getLibrary(token, libraryId),
      ]);
      if (profRes.success && profRes.data) setProfile(profRes.data);
      if (libRes.success && libRes.data) {
        const data = libRes.data as {
          library: { name: string; description?: string };
          points: {
            regulationPointId: string;
            regulationDocumentId: string;
            displayOrder: number;
            pointSnapshot?: string;
          }[];
        };
        setName(data.library.name);
        setDescription(data.library.description ?? "");
        const mapped = data.points.map((p) => ({
          regulationPointId: p.regulationPointId,
          regulationDocumentId: p.regulationDocumentId,
          displayOrder: p.displayOrder,
          pointSnapshot: p.pointSnapshot ? JSON.parse(p.pointSnapshot) : undefined,
        }));
        setInitialPoints(mapped);
        setPoints(mapped);
      }
      setLoading(false);
    })();
  }, [libraryId]);

  async function handleSave(e: React.FormEvent) {
    e.preventDefault();
    setSaving(true);
    const token = await getClientToken();
    if (!token) return;
    const res = await updateLibrary(token, libraryId, {
      name: name.trim(),
      description: description.trim() || null,
      points,
    });
    if (!res.success) setError(res.message ?? "Failed to save");
    setSaving(false);
  }

  if (!profile || loading) {
    return <p className="p-6 text-sm text-[var(--text-muted)]">Loading…</p>;
  }

  const canEdit = profile.role === "maker" || profile.role === "super_admin";

  return (
    <>
      <TopNav title={name || "Edit Library"} profile={profile} />
      <div className="border-b border-[var(--border)] px-6 py-2">
        <Link href="/libraries" className="text-sm text-[var(--accent)]">
          ← Back to libraries
        </Link>
      </div>
      {canEdit ? (
        <form onSubmit={handleSave} className="flex flex-1 flex-col">
          <div className="grid gap-4 border-b border-[var(--border)] p-6 md:grid-cols-2">
            <label className="block text-sm">
              Name
              <input className="input mt-1" value={name} onChange={(e) => setName(e.target.value)} required />
            </label>
            <label className="block text-sm">
              Description
              <input
                className="input mt-1"
                value={description}
                onChange={(e) => setDescription(e.target.value)}
              />
            </label>
          </div>
          <LibraryBuilder
            initialPoints={initialPoints}
            departmentId={profile.departmentId}
            onPointsChange={setPoints}
          />
          {error && <p className="px-6 text-sm text-red-400">{error}</p>}
          <div className="border-t border-[var(--border)] p-6">
            <button type="submit" className="btn-primary" disabled={saving}>
              {saving ? "Saving…" : "Save changes"}
            </button>
          </div>
        </form>
      ) : (
        <p className="p-6 text-sm text-[var(--text-muted)]">View only</p>
      )}
    </>
  );
}
