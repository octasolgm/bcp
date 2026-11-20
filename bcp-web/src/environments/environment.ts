import { LOCAL_API_URL, resolveApiUrl } from './api-url';

/** Local development — `ng serve` on http://localhost:3002 */
export const environment = {
  production: false,
  /** true on client installs (app-config.json "edition": "client"): only the live analysis and document pages. */
  clientEdition: false,
  apiUrl: resolveApiUrl(),
  /** Override when testing local web against Azure API only */
  localApiUrl: LOCAL_API_URL,
  nestjsApiUrl: '',
  ndApiUrl: resolveApiUrl(),
  appUrl: 'http://localhost:3002',
};
