'use client';

import Link from 'next/link';
import { usePathname } from 'next/navigation';
import {
  BookOpen,
  FileText,
  LayoutDashboard,
  LayoutGrid,
  PlusCircle,
  Zap,
} from 'lucide-react';
import { cn } from '@/lib/utils';

const NAV = [
  { href: '/dashboard', label: 'Dashboard' },
  { href: '/analyse', label: 'Analyse' },
  { href: '/dual-verify', label: 'Dual Verify' },
  { href: '/reg-library', label: 'Reg Library' },
  { href: '/documents', label: 'Documents' },
];

interface AppShellProps {
  children: React.ReactNode;
  documentCount?: number;
}

/** Reguliq app shell — top nav + sidebar */
export function AppShell({ children, documentCount = 6 }: AppShellProps) {
  const pathname = usePathname();

  return (
    <div className="flex min-h-screen flex-col">
      <header className="flex h-14 items-center justify-between border-b border-white/10 bg-[#0b111b] px-4">
        <div className="flex items-center gap-8">
          <Link href="/dashboard" className="flex items-center gap-2 font-semibold">
            <span className="flex h-7 w-7 items-center justify-center rounded bg-emerald-500 text-xs font-bold text-white">
              R
            </span>
            Reguliq
          </Link>
          <nav className="hidden items-center gap-1 md:flex">
            {NAV.map((item) => (
              <Link
                key={item.href}
                href={item.href}
                className={cn(
                  'rounded-lg px-3 py-1.5 text-sm transition',
                  pathname === item.href || pathname.startsWith(item.href + '/')
                    ? 'bg-[#1e293b] text-white'
                    : 'text-slate-400 hover:text-white',
                )}
              >
                {item.label}
              </Link>
            ))}
          </nav>
        </div>
        <div className="flex items-center gap-3">
          <button
            type="button"
            className="rounded-lg border border-white/10 px-3 py-1.5 text-sm text-slate-300"
          >
            SNB UAE / DIFC ▾
          </button>
          <Link
            href="/analyse"
            className="hidden rounded-lg border border-white/10 px-3 py-1.5 text-sm text-slate-300 sm:inline-block"
          >
            Sync
          </Link>
          <Link
            href="/dual-verify"
            className="rounded-lg bg-emerald-500 px-3 py-1.5 text-sm font-medium text-white hover:bg-emerald-400"
          >
            + Dual Verify
          </Link>
        </div>
      </header>

      <div className="flex flex-1">
        <aside className="hidden w-52 shrink-0 border-r border-white/10 bg-[#0b111b] p-4 md:block">
          <p className="mb-2 text-[10px] font-semibold uppercase tracking-wider text-slate-500">
            Workspace
          </p>
          <SidebarLink href="/dashboard" icon={<LayoutGrid className="h-4 w-4" />} label="Overview" />
          <SidebarLink href="/analyse" icon={<PlusCircle className="h-4 w-4" />} label="Sync Analyse" />
          <SidebarLink href="/dual-verify" icon={<Zap className="h-4 w-4" />} label="Dual Verify" />
          <SidebarLink
            href="/documents"
            icon={<FileText className="h-4 w-4" />}
            label="Documents"
            badge={documentCount}
          />
          <p className="mb-2 mt-6 text-[10px] font-semibold uppercase tracking-wider text-slate-500">
            Regulations
          </p>
          <SidebarLink href="/reg-library" icon={<BookOpen className="h-4 w-4" />} label="Library" />
        </aside>
        <main className="flex-1 overflow-auto p-6">{children}</main>
      </div>
    </div>
  );
}

function SidebarLink({
  href,
  icon,
  label,
  badge,
}: {
  href: string;
  icon: React.ReactNode;
  label: string;
  badge?: number;
}) {
  const pathname = usePathname();
  const active = pathname === href || pathname.startsWith(href + '/');

  return (
    <Link
      href={href}
      className={cn(
        'mb-1 flex items-center gap-2 rounded-lg px-2 py-2 text-sm transition',
        active ? 'bg-white/10 text-white' : 'text-slate-400 hover:bg-white/5 hover:text-white',
      )}
    >
      {icon}
      <span className="flex-1">{label}</span>
      {badge != null && (
        <span className="rounded-full bg-emerald-500 px-1.5 text-[10px] font-bold text-white">
          {badge}
        </span>
      )}
    </Link>
  );
}
