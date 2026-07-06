'use client';

import Link from 'next/link';
import { useEffect, useState } from 'react';
import { AppShell } from '@/components/layout/AppShell';
import { getRegulations } from '@/lib/api';
import type { BcpwebRegulation } from '@/types';
import { cn } from '@/lib/utils';

const TABS = [
  { id: 'all', label: 'All' },
  { id: 'CBUAE', label: 'CBUAE' },
  { id: 'FATF', label: 'FATF' },
  { id: 'UAE Gov', label: 'UAE Gov' },
  { id: 'DIFC/DFSA', label: 'DIFC/DFSA' },
  { id: 'International', label: 'International' },
];

/** Regulation library page */
export default function RegLibraryPage() {
  const [tab, setTab] = useState('all');
  const [items, setItems] = useState<BcpwebRegulation[]>([]);
  const [counts, setCounts] = useState<Record<string, number>>({});

  useEffect(() => {
    getRegulations(tab === 'all' ? undefined : tab).then((res) => {
      setItems(res.items);
      setCounts(res.counts);
    });
  }, [tab]);

  return (
    <AppShell>
      <div className="mb-6 flex items-start justify-between">
        <div>
          <h1 className="text-2xl font-bold">Regulation Library</h1>
          <p className="text-sm text-slate-400">
            MENA region regulatory database — auto-updated from official sources
          </p>
        </div>
        <div className="flex gap-2">
          <button type="button" className="rounded-lg border border-white/10 px-3 py-2 text-sm">
            Sync now
          </button>
          <button type="button" className="rounded-lg bg-emerald-500 px-3 py-2 text-sm font-medium">
            + Add regulation
          </button>
        </div>
      </div>

      <div className="mb-4 flex flex-wrap gap-2">
        {TABS.map((t) => (
          <button
            key={t.id}
            type="button"
            onClick={() => setTab(t.id)}
            className={cn(
              'rounded-lg px-3 py-1.5 text-sm',
              tab === t.id ? 'bg-white/10 text-white' : 'text-slate-400 hover:bg-white/5',
            )}
          >
            {t.label} ({counts[t.id] ?? counts.all ?? 0})
          </button>
        ))}
      </div>

      <div className="overflow-hidden rounded-xl border border-white/10">
        <table className="w-full text-sm">
          <thead className="bg-white/5 text-left text-slate-500">
            <tr>
              <th className="p-3">Regulation</th>
              <th className="p-3">Issuing Body</th>
              <th className="p-3">Type</th>
              <th className="p-3">Version</th>
              <th className="p-3">Last Updated</th>
              <th className="p-3">Status</th>
              <th className="p-3">Action</th>
            </tr>
          </thead>
          <tbody>
            {items.map((r) => (
              <tr key={r.id} className="border-t border-white/5 hover:bg-white/5">
                <td className="p-3">
                  <p className="font-medium">{r.title}</p>
                  {r.subtitle && <p className="text-xs text-slate-500">{r.subtitle}</p>}
                </td>
                <td className="p-3 text-slate-400">{r.issuingBody}</td>
                <td className="p-3 text-slate-400">{r.type}</td>
                <td className="p-3 text-slate-400">{r.version}</td>
                <td className="p-3 text-slate-400">{r.lastUpdated}</td>
                <td className="p-3">
                  <span
                    className={cn(
                      'rounded-full px-2 py-0.5 text-xs',
                      r.status === 'Active'
                        ? 'bg-green-500/20 text-green-400'
                        : 'bg-yellow-500/20 text-yellow-400',
                    )}
                  >
                    {r.status}
                  </span>
                </td>
                <td className="p-3">
                  <Link
                    href={`/analyse?regulation=${r.id}`}
                    className="text-emerald-400 hover:underline"
                  >
                    Use
                  </Link>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </AppShell>
  );
}
