"use client";

import { useState } from "react";
import { useRouter } from "next/navigation";
import Link from "next/link";
import { createClient } from "@/lib/supabase/client";
import { getProfile, upsertProfile } from "@/lib/api/bcp-api-client";

export default function LoginPage() {
  const router = useRouter();
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [error, setError] = useState("");
  const [loading, setLoading] = useState(false);

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    setError("");
    setLoading(true);
    const supabase = createClient();
    const { data, error: authError } = await supabase.auth.signInWithPassword({
      email: email.trim(),
      password,
    });
    if (authError) {
      setError(authError.message);
      setLoading(false);
      return;
    }
    const token = data.session?.access_token;
    if (!token) {
      setError("No session returned.");
      setLoading(false);
      return;
    }
    let profileRes = await getProfile(token);
    if (!profileRes.success || !profileRes.data) {
      profileRes = await upsertProfile(token, { fullName: email.split("@")[0] });
    }
    if (profileRes.success && profileRes.data && !profileRes.data.isActive) {
      setError("Account deactivated");
      await supabase.auth.signOut();
      setLoading(false);
      return;
    }
    router.push("/dashboard");
    router.refresh();
  }

  return (
    <div className="flex min-h-screen items-center justify-center p-4">
      <div className="card w-full max-w-md p-8">
        <h1 className="mb-1 text-xl font-semibold">Reguliq</h1>
        <p className="mb-6 text-sm text-[var(--text-muted)]">Sign in to your workspace</p>
        <form onSubmit={submit} className="space-y-4">
          <label className="block text-sm">
            Email
            <input
              className="input mt-1"
              type="email"
              value={email}
              onChange={(e) => setEmail(e.target.value)}
              required
              autoComplete="email"
            />
          </label>
          <label className="block text-sm">
            Password
            <input
              className="input mt-1"
              type="password"
              value={password}
              onChange={(e) => setPassword(e.target.value)}
              required
              autoComplete="current-password"
            />
          </label>
          {error && (
            <p className="text-sm text-red-400" role="alert">
              {error}
            </p>
          )}
          <button type="submit" className="btn-primary w-full" disabled={loading}>
            {loading ? "Signing in…" : "Sign in"}
          </button>
        </form>
        <p className="mt-4 text-center text-sm">
          <Link href="/forgot-password" className="text-[var(--accent)]">
            Forgot password?
          </Link>
        </p>
      </div>
    </div>
  );
}
