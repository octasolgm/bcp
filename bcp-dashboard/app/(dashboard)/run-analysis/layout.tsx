import { redirect } from "next/navigation";
import { getServerProfile } from "@/lib/auth/helpers";

export default async function RunAnalysisLayout({ children }: { children: React.ReactNode }) {
  const profile = await getServerProfile();
  if (!profile) redirect("/login");
  if (profile.role !== "maker" && profile.role !== "super_admin") {
    redirect("/dashboard");
  }
  return children;
}
