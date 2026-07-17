import { createClient, type SupabaseClient } from '@supabase/supabase-js';
import { environment } from '../../../environments/environment';
import { supabaseConfigured } from '../../../environments/runtime-config';

let client: SupabaseClient | null = null;

export function getNdSupabaseClient(): SupabaseClient {
  if (!client) {
    const url = environment.supabaseUrl;
    const key = environment.supabaseAnonKey;
    if (!supabaseConfigured()) {
      console.error(
        'Supabase URL and anon key are not configured. Set Azure App Settings (supabaseUrl, supabaseAnonKey) and use startup command: node server.js — or rebuild with environment.production.ts.',
      );
      throw new Error('Supabase URL and anon key must be configured');
    }
    client = createClient(url, key, {
      auth: {
        detectSessionInUrl: true,
        // Implicit flow: email reset/invite links carry tokens in the URL hash and do not
        // require a PKCE code verifier from the browser that requested the email.
        flowType: 'implicit',
        persistSession: true,
      },
    });
  }
  return client;
}

export async function getNdAccessToken(): Promise<string | null> {
  const { data } = await getNdSupabaseClient().auth.getSession();
  return data.session?.access_token ?? null;
}
