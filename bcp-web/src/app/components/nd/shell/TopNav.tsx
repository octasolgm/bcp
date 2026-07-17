"use client";

import { useRouter } from "next/navigation";
import { createClient } from "@/lib/supabase/client";
import type { UserProfile } from "@/lib/api/bcp-api-client";

export function TopNav({
  title,
  profile,
}: {
  title: string;
  profile: UserProfile;
}) {
  const router = useRouter();

  async function signOut() {
    const supabase = createClient();
    await supabase.auth.signOut();
    router.push("/login");
    router.refresh();
  }

  return (
    <header className="flex items-center justify-between border-b border-[var(--border)] bg-[var(--bg-elevated)] px-6 py-4">
      <h1 className="text-lg font-semibold">{title}</h1>
      <div className="flex items-center gap-4 text-sm">
        <span className="text-[var(--text-muted)]">{profile.fullName}</span>
        <button type="button" className="btn-secondary text-sm" onClick={signOut}>
          Sign out
        </button>
      </div>
    </header>
  );
}
