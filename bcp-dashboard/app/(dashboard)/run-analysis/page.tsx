import { redirect } from "next/navigation";
import { TopNav } from "@/components/shell/TopNav";
import { RunAnalysisForm } from "@/components/analysis/RunAnalysisForm";
import { getServerProfile } from "@/lib/auth/helpers";

export default async function RunAnalysisPage() {
  const profile = await getServerProfile();
  if (!profile) redirect("/login");

  return (
    <>
      <TopNav title="Run Analysis" profile={profile} />
      <RunAnalysisForm departmentId={profile.departmentId} />
    </>
  );
}
