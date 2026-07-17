import { redirect } from "next/navigation";
import { TopNav } from "@/components/shell/TopNav";
import { ResultsView } from "@/components/results/ResultsView";
import { getServerProfile } from "@/lib/auth/helpers";

export default async function ResultsPage({
  params,
}: {
  params: Promise<{ runId: string }>;
}) {
  const { runId } = await params;
  const profile = await getServerProfile();
  if (!profile) redirect("/login");

  const readOnly = profile.role === "checker" || profile.role === "reviewer";

  return (
    <>
      <TopNav title="Analysis Results" profile={profile} />
      <ResultsView runId={runId} profile={profile} readOnly={readOnly} />
    </>
  );
}
