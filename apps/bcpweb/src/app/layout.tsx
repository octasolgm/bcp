import type { Metadata } from 'next';
import './globals.css';

export const metadata: Metadata = {
  title: 'Reguliq — BCP Web',
  description: 'Bank compliance gap analysis workbench',
};

export default function RootLayout({
  children,
}: Readonly<{ children: React.ReactNode }>) {
  return (
    <html lang="en">
      <body>{children}</body>
    </html>
  );
}
