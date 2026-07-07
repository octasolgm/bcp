/** Live Azure API (bcp-api-dev App Service). */
export const AZURE_API_URL = 'https://bcp-api-dev.azurewebsites.net';

/** Local bcp-api (`dotnet run` on port 5100). */
export const LOCAL_API_URL = 'http://localhost:5100';

/**
 * Pick API base URL from where the web app is opened:
 * - localhost:3002 (ng serve) → local API
 * - bcp-web-dev.azurewebsites.net → Azure API
 */
export function resolveApiUrl(): string {
  if (typeof window === 'undefined') {
    return LOCAL_API_URL;
  }

  const host = window.location.hostname.toLowerCase();
  if (host === 'localhost' || host === '127.0.0.1') {
    return LOCAL_API_URL;
  }

  return AZURE_API_URL;
}
