import { Suspense } from 'react';
import AnalysePage from './AnalysePageClient';

export default function AnalyseRoute() {
  return (
    <Suspense fallback={<p className="p-6 text-slate-400">Loading…</p>}>
      <AnalysePage />
    </Suspense>
  );
}
