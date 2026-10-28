import { AZURE_API_URL } from './api-url';

/** Production build — deployed to Azure or built with `npm run build:prod` */
export const environment = {
  production: true,
  apiUrl: AZURE_API_URL,
  azureApiUrl: AZURE_API_URL,
  nestjsApiUrl: '',
  supabaseUrl: 'https://prxmkrmwqxlltwjnazay.supabase.co',
  supabaseAnonKey:
    'eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJzdXBhYmFzZSIsInJlZiI6InByeG1rcm13cXhsbHR3am5hemF5Iiwicm9sZSI6ImFub24iLCJpYXQiOjE3ODY5NzAwMTIsImV4cCI6MjEwMjU0NjAxMn0.nHcayH4ul9rcluW8yqzvWTEXgh-jHC6hU4WL2YauVAw',
  ndApiUrl: AZURE_API_URL,
  appUrl: 'https://bcp-web-dev.azurewebsites.net',
};
