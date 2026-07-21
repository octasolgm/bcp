/**
 * Azure / hosted build — `ng build --configuration production`
 * Update apiUrl to your deployed .NET App Service URL before building.
 */
export const environment = {
  production: true,
  apiUrl: 'https://reguliq-api.azurewebsites.net',
  /** Optional — only if you still run NestJS separately in production */
  nestjsApiUrl: '',
};
