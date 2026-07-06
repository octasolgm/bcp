import Link from 'next/link';

export default function HomePage() {
  const bcpwebUrl = process.env.NEXT_PUBLIC_BCPWEB_URL ?? 'http://localhost:3001';

  return (
    <main className="mx-auto flex min-h-screen max-w-3xl flex-col justify-center gap-6 p-8">
      <h1 className="text-4xl font-bold text-slate-900">Bank Compliance Platform</h1>
      <p className="text-lg text-slate-600">
        Compare regulatory requirements against internal bank policies.
      </p>
      <nav className="flex flex-wrap gap-4">
        <a
          href={bcpwebUrl}
          target="_blank"
          rel="noopener noreferrer"
          className="rounded-lg bg-emerald-600 px-4 py-2 font-semibold text-white hover:bg-emerald-700"
        >
          BCP Web (Reguliq) ↗
        </a>
        <Link
          href="/ai-lab"
          className="rounded-lg bg-violet-600 px-4 py-2 text-white hover:bg-violet-700"
        >
          AI Lab
        </Link>
        <Link
          href="/ai-lab-batch"
          className="rounded-lg bg-indigo-600 px-4 py-2 text-white hover:bg-indigo-700"
        >
          AI Lab Batch
        </Link>
        <Link
          href="/ai-lab-report"
          className="rounded-lg bg-amber-600 px-4 py-2 text-white hover:bg-amber-700"
        >
          Compliance Report
        </Link>
        <Link
          href="/ai-lab-extract"
          className="rounded-lg bg-emerald-600 px-4 py-2 text-white hover:bg-emerald-700"
        >
          Extract Points
        </Link>
        <Link
          href="/ai-lab-reference"
          className="rounded-lg bg-sky-600 px-4 py-2 text-white hover:bg-sky-700"
        >
          Reference Mapper
        </Link>
        <Link
          href="/landing-ai"
          className="rounded-lg bg-teal-600 px-4 py-2 text-white hover:bg-teal-700"
        >
          Workbench — Section (2.1, 2.2…)
        </Link>
        <Link
          href="/landing-ai/detail"
          className="rounded-lg bg-teal-700 px-4 py-2 text-white hover:bg-teal-800"
        >
          Workbench — Leaf (2.1.1, 2.1.2…)
        </Link>
        <Link
          href="/landing-ai/dual-verify"
          className="rounded-lg bg-indigo-600 px-4 py-2 text-white hover:bg-indigo-700"
        >
          Dual Verify — Section
        </Link>
        <Link
          href="/landing-ai/dual-verify/detail"
          className="rounded-lg bg-indigo-700 px-4 py-2 text-white hover:bg-indigo-800"
        >
          Dual Verify — Leaf
        </Link>
        <Link
          href="/landing-ai/kafka-dual-verify"
          className="rounded-lg bg-orange-600 px-4 py-2 text-white hover:bg-orange-700"
        >
          Dual Verify — Kafka (async)
        </Link>
        <Link
          href="/upload"
          className="rounded-lg bg-blue-600 px-4 py-2 text-white hover:bg-blue-700"
        >
          Upload
        </Link>
        <Link
          href="/compliance"
          className="rounded-lg border border-slate-300 px-4 py-2 hover:bg-slate-100"
        >
          Compliance
        </Link>
        <Link
          href="/dashboard"
          className="rounded-lg border border-slate-300 px-4 py-2 hover:bg-slate-100"
        >
          Dashboard
        </Link>
      </nav>
    </main>
  );
}
