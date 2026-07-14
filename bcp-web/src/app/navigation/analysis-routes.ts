/** Shared gap-analysis page routes for shell nav and analyse design pickers. */
export const ANALYSIS_ROUTES = [
  { path: '/analyse', label: 'Original' },
  { path: '/analyse-v2', label: 'V2 · 3-Column' },
  { path: '/analyse-v3', label: 'V3 · Command Bar' },
  { path: '/analyse-v4', label: 'V4 · Doc Strip' },
  { path: '/analyse-v5', label: 'V5 · Left Rail' },
  { path: '/analyse-v6', label: 'V6 · Table View' },
  { path: '/analyse-v7', label: 'V7 · Split Screen' },
  { path: '/analyse-v8', label: 'V8 · Points on Top' },
] as const;
