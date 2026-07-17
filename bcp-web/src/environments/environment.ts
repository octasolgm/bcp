import { LOCAL_API_URL, resolveApiUrl } from './api-url';

/** Local development — `ng serve` on http://localhost:3002 */
export const environment = {
  production: false,
  apiUrl: resolveApiUrl(),
  /** Override when testing local web against Azure API only */
  localApiUrl: LOCAL_API_URL,
  nestjsApiUrl: '',
  supabaseUrl: 'https://hxfbzhjlmkiqhbbeftfq.supabase.co',
  supabaseAnonKey: 'eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJzdXBhYmFzZSIsInJlZiI6Imh4ZmJ6aGpsbWtpcWhiYmVmdGZxIiwicm9sZSI6ImFub24iLCJpYXQiOjE3ODIyMDMxODUsImV4cCI6MjA5Nzc3OTE4NX0.YFoIaS3i7NqUuRyaks92CYn1XLYOl5H1azlS0oyAXsk',
  ndApiUrl: resolveApiUrl(),
  appUrl: 'http://localhost:3002',
};
