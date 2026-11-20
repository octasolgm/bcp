import { AZURE_API_URL } from './api-url';

/** Production build — deployed to Azure or built with `npm run build:prod` */
export const environment = {
  production: true,
  /** true on client installs (app-config.json "edition": "client"): only the live analysis and document pages. */
  clientEdition: false,
  apiUrl: AZURE_API_URL,
  azureApiUrl: AZURE_API_URL,
  nestjsApiUrl: '',
  ndApiUrl: AZURE_API_URL,
  appUrl: 'https://bcp-web-dev.azurewebsites.net',
};
