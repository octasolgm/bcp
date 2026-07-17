"use client";

import { useEffect, useState } from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { TopNav } from "@/components/shell/TopNav";
import { deleteLibrary, getLibraries, getProfile } from "@/lib/api/bcp-api-client";
import type { UserProfile } from "@/lib/api/bcp-api-client";
import { getClientToken } from "@/lib/auth/client-token";
import type { LibrarySummary } from "@/lib/types";
import { formatDate } from "@/lib/utils";

export default function LibrariesPage() {
  const router = useRouter();
  const [profile, setProfile] = useState<UserProfile | null>(null);
  const [libraries, setLibraries] = useState<LibrarySummary[]>([]);
  const [loading, setLoading] = useState(true);

  async function load() {
    const token = await getClientToken();
    if (!token) return;
    const profRes = await getProfile(token);
    if (!profRes.success || !profRes.data) {
      setLoading(false);
      return;
    }
    setProfile(profRes.data);
    const libRes = await getLibraries(token, profRes.data.departmentId ?? undefined);
    if (libRes.success && libRes.data) setLibraries(libRes.data as LibrarySummary[]);
    setLoading(false);
  }

  useEffect(() => {
    load();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  async function handleDelete(id: string) {
    if (!confirm("Delete this library?")) return;
    const token = await getClientToken();
    if (!token) return;
    await deleteLibrary(token, id);
    await load();
  }

  const canEdit = profile?.role === "maker" || profile?.role === "super_admin";

  if (!profile) {
    return <p className="p-6 text-sm text-[var(--text-muted)]">Loading…</p>;
  }

  return (
    <>
      <TopNav title="Libraries" profile={profile} />
      <div className="p-6">
        {canEdit && (
          <div className="mb-6">
            <Link href="/libraries/new" className="btn-primary inline-block">
              New library
            </Link>
          </div>
        )}

        {loading ? (
          <p className="text-sm text-[var(--text-muted)]">Loading…</p>
        ) : (
          <div className="card divide-y divide-[var(--border)]">
            {libraries.map((lib) => (
              <div key={lib.id} className="flex flex-wrap items-center justify-between gap-4 px-4 py-3">
                <div>
                  <Link href={`/libraries/${lib.id}`} className="font-medium hover:text-[var(--accent)]">
                    {lib.name}
                  </Link>
                  {lib.description && (
                    <p className="text-sm text-[var(--text-muted)]">{lib.description}</p>
                  )}
                  <div className="text-xs text-[var(--text-muted)]">
                    {lib.pointCount} points · {lib.documentCount} docs · {formatDate(lib.createdAt)}
                  </div>
                </div>
                {canEdit && (
                  <div className="flex gap-2">
                    <button
                      type="button"
                      className="btn-secondary text-sm"
                      onClick={() => router.push(`/libraries/${lib.id}`)}
                    >
                      Edit
                    </button>
                    <button
                      type="button"
                      className="btn-secondary text-sm text-red-400"
                      onClick={() => handleDelete(lib.id)}
                    >
                      Delete
                    </button>
                  </div>
                )}
              </div>
            ))}
            {libraries.length === 0 && (
              <p className="px-4 py-6 text-sm text-[var(--text-muted)]">No libraries yet.</p>
            )}
          </div>
        )}
      </div>
    </>
  );
}
