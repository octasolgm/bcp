export const BRAND = {
  name: 'Comply Solution',
  shortName: 'Comply Solution',
  tagline: 'Regulatory compliance platform',
  /** Square-cropped mark (icon only) — use in header, login, favicon */
  logo: 'assets/brand/comply-solution-mark-192.png',
  logoMark: 'assets/brand/comply-solution-mark-64.png',
  favicon32: 'assets/brand/favicon-32.png',
  favicon192: 'assets/brand/comply-solution-mark-192.png',
  favicon512: 'assets/brand/comply-solution-mark-512.png',
  themeColor: '#1d7ee6',
} as const;

export const brandPageTitle = (page: string): string => `${page} · ${BRAND.shortName}`;
