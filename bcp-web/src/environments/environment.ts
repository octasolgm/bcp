/** Local development — `ng serve` uses this file */
export const environment = {
  production: false,
  /** Reguliq .NET API (primary) */
  apiUrl: 'http://localhost:5100',
  /**
   * Optional NestJS API — only for legacy Kafka sessions started from Next.js.
   * Leave empty to stop all :4000 requests.
   */
  nestjsApiUrl: '',
};
