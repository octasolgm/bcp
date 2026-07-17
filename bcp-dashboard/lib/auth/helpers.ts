import { createClient } from "@/lib/supabase/server";
import { getProfile, type UserProfile } from "@/lib/api/bcp-api-client";

export async function getSessionToken(): Promise<string | null> {
  const supabase = await createClient();
  const { data } = await supabase.auth.getSession();
  return data.session?.access_token ?? null;
}

export async function getServerProfile(): Promise<UserProfile | null> {
  const token = await getSessionToken();
  if (!token) return null;
  const res = await getProfile(token);
  if (!res.success || !res.data) return null;
  return res.data;
}

export function roleCanAccess(role: string, path: string): boolean {
  if (role === "super_admin") return true;
  if (path.startsWith("/admin")) return false;
  if (path.startsWith("/checker")) return role === "checker";
  if (path.startsWith("/reviewer")) return role === "reviewer";
  if (path.startsWith("/run-analysis")) return role === "maker" || role === "super_admin";
  return true;
}
