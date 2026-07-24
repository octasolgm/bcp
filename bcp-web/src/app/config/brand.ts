export const BRAND = {
  name: 'Comply-Solution.com',
  shortName: 'Comply-Solution',
  tagline: 'Regulatory compliance platform',
  logoHeader: 'assets/brand/comply-solution-logo-header.png',
  logoPrimary: 'assets/brand/comply-solution-logo-primary.png',
  logoMark: 'assets/brand/comply-solution-logo-hex.png',
  themeColor: '#1a365d',
} as const;

export const brandPageTitle = (page: string): string => `${page} · ${BRAND.shortName}`;
