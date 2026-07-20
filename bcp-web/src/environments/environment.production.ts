import { AZURE_API_URL, resolveApiUrl } from './api-url';

/** Production build — deployed to Azure or built with `npm run build:prod` */
export const environment = {
  production: true,
  apiUrl: resolveApiUrl(),
  azureApiUrl: AZURE_API_URL,
  nestjsApiUrl: '',
  supabaseUrl: 'https://hxfbzhjlmkiqhbbeftfq.supabase.co',
  supabaseAnonKey:
    'eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJzdXBhYmFzZSIsInJlZiI6Imh4ZmJ6aGpsbWtpcWhiYmVmdGZxIiwicm9sZSI6ImFub24iLCJpYXQiOjE3ODIyMDMxODUsImV4cCI6MjA5Nzc3OTE4NX0.YFoIaS3i7NqUuRyaks92CYn1XLYOl5H1azlS0oyAXsk',
  ndApiUrl: resolveApiUrl(),
  appUrl: 'https://bcp-web-dev.azurewebsites.net',
};
