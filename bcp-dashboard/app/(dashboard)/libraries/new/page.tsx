"use client";

import { useEffect, useState } from "react";
import { useRouter } from "next/navigation";
import { TopNav } from "@/components/shell/TopNav";
import { LibraryBuilder } from "@/components/library/LibraryBuilder";
import { createLibrary, getProfile } from "@/lib/api/bcp-api-client";
import type { UserProfile } from "@/lib/api/bcp-api-client";
import { getClientToken } from "@/lib/auth/client-token";
import type { LibraryPointInput } from "@/lib/types";

export default function NewLibraryPage() {
  const router = useRouter();
  const [profile, setProfile] = useState<UserProfile | null>(null);
  const [name, setName] = useState("");
  const [description, setDescription] = useState("");
  const [points, setPoints] = useState<LibraryPointInput[]>([]);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState("");

  useEffect(() => {
    (async () => {
      const token = await getClientToken();
      if (!token) return;
      const res = await getProfile(token);
      if (res.success && res.data) setProfile(res.data);
    })();
  }, []);

  async function handleSave(e: React.FormEvent) {
    e.preventDefault();
    if (!name.trim() || points.length === 0) {
      setError("Name and at least one point are required");
      return;
    }
    setSaving(true);
    const token = await getClientToken();
    if (!token) return;
    const res = await createLibrary(token, {
      name: name.trim(),
      description: description.trim() || null,
      departmentId: profile?.departmentId ?? null,
      points,
    });
    if (res.success && res.data?.id) {
      router.push(`/libraries/${res.data.id}`);
    } else {
      setError(res.message ?? "Failed to create library");
    }
    setSaving(false);
  }

  if (!profile) {
    return <p className="p-6 text-sm text-[var(--text-muted)]">Loading…</p>;
  }

  return (
    <>
      <TopNav title="New Library" profile={profile} />
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
        <LibraryBuilder departmentId={profile.departmentId} onPointsChange={setPoints} />
        {error && <p className="px-6 text-sm text-red-400">{error}</p>}
        <div className="border-t border-[var(--border)] p-6">
          <button type="submit" className="btn-primary" disabled={saving}>
            {saving ? "Saving…" : "Create library"}
          </button>
        </div>
      </form>
    </>
  );
}
