export default function DashboardPage() {
  const bcpwebUrl = process.env.NEXT_PUBLIC_BCPWEB_URL ?? 'http://localhost:3001';

  return (
    <main className="mx-auto max-w-3xl p-8">
      <h1 className="mb-4 text-3xl font-bold text-slate-900">MIS Dashboard</h1>
      <p className="mb-6 rounded-lg border border-slate-200 bg-white p-8 text-slate-600">
        Placeholder — compliance metrics and charts (Stage 8).
      </p>
      <a
        href={bcpwebUrl}
        target="_blank"
        rel="noopener noreferrer"
        className="inline-flex items-center gap-2 rounded-lg bg-emerald-600 px-5 py-3 text-sm font-semibold text-white shadow-sm transition hover:bg-emerald-700"
      >
        Open BCP Web (Reguliq)
        <span className="text-emerald-200" aria-hidden>
          ↗
        </span>
      </a>
      <p className="mt-2 text-sm text-slate-500">
        Compliance workbench — gap analysis, regulation library, Excel export
      </p>
    </main>
  );
}
