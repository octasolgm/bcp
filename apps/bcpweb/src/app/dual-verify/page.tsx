'use client';

import { Suspense } from 'react';
import { useSearchParams } from 'next/navigation';
import { AppShell } from '@/components/layout/AppShell';
import { KafkaDualVerifyWorkbench } from '@web/components/landing-ai/KafkaDualVerifyWorkbench';

function DualVerifyInner() {
  const params = useSearchParams();
  const sessionId = params.get('session') ?? undefined;

  return (
    <KafkaDualVerifyWorkbench
      variant="reguliq"
      embedded
      initialSessionId={sessionId}
      wrapContent={(children) => <AppShell>{children}</AppShell>}
    />
  );
}

/** Reguliq — full Kafka dual verify (Landing AI + Gemini) */
export default function DualVerifyPage() {
  return (
    <Suspense
      fallback={
        <AppShell>
          <p className="text-slate-400">Loading dual verify…</p>
        </AppShell>
      }
    >
      <DualVerifyInner />
    </Suspense>
  );
}
