'use client';

import Link from 'next/link';
import { useEffect, useState } from 'react';
import { AppShell } from '@/components/layout/AppShell';
import { getDocuments } from '@/lib/api';
import type { BcpwebDocument } from '@/types';
import { cn } from '@/lib/utils';

const TABS = ['All documents', 'AML/CFT', 'Sanctions', 'KYC/CDD'];

const TONE: Record<string, string> = {
  red: 'bg-red-500/20 text-red-400',
  orange: 'bg-orange-500/20 text-orange-400',
  blue: 'bg-blue-500/20 text-blue-400',
  green: 'bg-green-500/20 text-green-400',
  yellow: 'bg-yellow-500/20 text-yellow-400',
};

const FORMAT_COLOR: Record<string, string> = {
  PDF: 'text-red-400 bg-red-500/20',
  DOC: 'text-blue-400 bg-blue-500/20',
  XLS: 'text-green-400 bg-green-500/20',
};

/** Document library page */
export default function DocumentsPage() {
  const [tab, setTab] = useState('All documents');
  const [docs, setDocs] = useState<BcpwebDocument[]>([]);

  useEffect(() => {
    const cat = tab === 'All documents' ? undefined : tab;
    getDocuments(cat).then(setDocs);
  }, [tab]);

  return (
    <AppShell>
      <div className="mb-6 flex items-start justify-between">
        <div>
          <h1 className="text-2xl font-bold">Document Library</h1>
          <p className="text-sm text-slate-400">
            Version-controlled compliance documents — synced from your OneDrive
          </p>
        </div>
        <div className="flex gap-2">
          <button
            type="button"
            className="rounded-lg border border-white/10 px-3 py-2 text-sm"
            onClick={() => alert('OneDrive sync coming soon')}
          >
            OneDrive
          </button>
          <button type="button" className="rounded-lg bg-emerald-500 px-3 py-2 text-sm font-medium">
            + Upload
          </button>
        </div>
      </div>

      <div className="mb-4 flex flex-wrap gap-2">
        {TABS.map((t) => (
          <button
            key={t}
            type="button"
            onClick={() => setTab(t)}
            className={cn(
              'rounded-lg px-3 py-1.5 text-sm',
              tab === t ? 'bg-white/10 text-white' : 'text-slate-400 hover:bg-white/5',
            )}
          >
            {t}
          </button>
        ))}
      </div>

      <div className="space-y-2">
        {docs.map((d) => (
          <div
            key={d.id}
            className="flex items-center justify-between rounded-xl border border-white/10 bg-white/5 p-4"
          >
            <div className="flex items-center gap-4">
              <span
                className={cn(
                  'rounded px-2 py-1 text-xs font-bold',
                  FORMAT_COLOR[d.format],
                )}
              >
                {d.format}
              </span>
              <div>
                <p className="font-medium">{d.title}</p>
                <p className="text-xs text-slate-500">
                  {d.category} · {d.pageCount} pages · Uploaded {d.uploadedAt}
                </p>
              </div>
            </div>
            <div className="flex items-center gap-3">
              <span className="text-xs text-slate-500">{d.version}</span>
              <span className={cn('rounded-full px-2 py-0.5 text-xs', TONE[d.statusTone])}>
                {d.status}
              </span>
              {d.sessionId && (
                <Link
                  href={`/analyse/report/${d.sessionId}`}
                  className="text-sm text-emerald-400 hover:underline"
                >
                  View analysis
                </Link>
              )}
            </div>
          </div>
        ))}
      </div>
    </AppShell>
  );
}
