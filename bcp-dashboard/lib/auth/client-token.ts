import { createClient } from "@/lib/supabase/client";

export async function getClientToken(): Promise<string | null> {
  const supabase = createClient();
  const { data } = await supabase.auth.getSession();
  return data.session?.access_token ?? null;
}
