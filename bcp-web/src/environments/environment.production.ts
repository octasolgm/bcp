import { AZURE_API_URL, resolveApiUrl } from './api-url';

/** Production build — deployed to Azure or built with `npm run build:prod` */
export const environment = {
  production: true,
  apiUrl: resolveApiUrl(),
  azureApiUrl: AZURE_API_URL,
  nestjsApiUrl: '',
};
