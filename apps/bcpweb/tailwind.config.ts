import type { Config } from 'tailwindcss';

const config: Config = {
  content: ['./src/**/*.{js,ts,jsx,tsx,mdx}'],
  theme: {
    extend: {
      colors: {
        bcp: {
          bg: '#0b111b',
          panel: '#0f1729',
          card: 'rgba(255,255,255,0.05)',
          accent: '#10b981',
          accentHover: '#22d3a0',
        },
      },
    },
  },
  plugins: [],
};

export default config;
