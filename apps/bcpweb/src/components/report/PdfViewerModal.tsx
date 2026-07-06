'use client';

import { useEffect, useState } from 'react';
import { X } from 'lucide-react';
import { getPdfPage } from '@/lib/api';

interface PdfViewerModalProps {
  sessionId: string;
  source: 'regulation' | 'policy';
  page: number;
  itemId?: string;
  onClose: () => void;
}

/** PDF viewer modal with extracted text panel */
export function PdfViewerModal({
  sessionId,
  source,
  page,
  itemId,
  onClose,
}: PdfViewerModalProps) {
  const [data, setData] = useState<{
    extractedText: string;
    totalPages: number;
    title: string;
  } | null>(null);

  useEffect(() => {
    getPdfPage(sessionId, source, page, itemId).then(setData).catch(console.error);
  }, [sessionId, source, page, itemId]);

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/70 p-4">
      <div className="flex max-h-[90vh] w-full max-w-5xl flex-col overflow-hidden rounded-xl border border-white/10 bg-[#0f1729] shadow-2xl">
        <div className="flex items-center justify-between border-b border-white/10 px-4 py-3">
          <div className="flex items-center gap-3">
            <span className="rounded bg-red-500/20 px-2 py-1 text-xs text-red-400">PDF</span>
            <div>
              <p className="font-medium">{data?.title ?? 'Regulation'}</p>
              <p className="text-xs text-slate-500">Regulation / Guideline</p>
            </div>
          </div>
          <div className="flex items-center gap-4">
            <span className="text-sm text-slate-400">
              Page {page} of {data?.totalPages ?? '—'}
            </span>
            <button type="button" onClick={onClose} className="text-slate-400 hover:text-white">
              <X className="h-5 w-5" />
            </button>
          </div>
        </div>
        <div className="grid flex-1 overflow-hidden md:grid-cols-2">
          <div className="overflow-auto border-r border-white/10 bg-white p-6 text-sm text-slate-900">
            <p className="mb-4 text-xs font-semibold text-slate-500">CBUAE Classification: Public</p>
            <h3 className="mb-2 font-bold">
              {source === 'regulation' ? '2. SANCTIONS COMPLIANCE PROGRAM' : 'Policy Section'}
            </h3>
            <p className="mb-2 rounded bg-blue-100 p-2 text-slate-800">
              {data?.extractedText?.slice(0, 280) ?? 'Loading document page…'}
            </p>
            <p className="text-slate-700">
              Senior management commitment and SCP oversight requirements as documented in the
              regulatory framework.
            </p>
          </div>
          <div className="overflow-auto p-4">
            <p className="mb-2 text-xs font-semibold uppercase tracking-wide text-cyan-400">
              Extracted Text
            </p>
            <p className="whitespace-pre-wrap text-sm italic text-slate-300">
              {data?.extractedText ?? 'Loading…'}
            </p>
            <p className="mt-4 text-xs text-slate-600">
              Manually verify this matches what you see on the page
            </p>
          </div>
        </div>
      </div>
    </div>
  );
}
