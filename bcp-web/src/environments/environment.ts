import { LOCAL_API_URL, resolveApiUrl } from './api-url';

/** Local development — `ng serve` on http://localhost:3002 */
export const environment = {
  production: false,
  apiUrl: resolveApiUrl(),
  /** Override when testing local web against Azure API only */
  localApiUrl: LOCAL_API_URL,
  nestjsApiUrl: '',
  supabaseUrl: 'https://prxmkrmwqxlltwjnazay.supabase.co',
  supabaseAnonKey: 'eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJzdXBhYmFzZSIsInJlZiI6InByeG1rcm13cXhsbHR3am5hemF5Iiwicm9sZSI6ImFub24iLCJpYXQiOjE3ODY5NzAwMTIsImV4cCI6MjEwMjU0NjAxMn0.nHcayH4ul9rcluW8yqzvWTEXgh-jHC6hU4WL2YauVAw',
  ndApiUrl: resolveApiUrl(),
  appUrl: 'http://localhost:3002',
};
