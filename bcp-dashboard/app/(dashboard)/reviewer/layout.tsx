import { redirect } from "next/navigation";
import { getServerProfile } from "@/lib/auth/helpers";

export default async function ReviewerLayout({ children }: { children: React.ReactNode }) {
  const profile = await getServerProfile();
  if (!profile) redirect("/login");
  if (profile.role !== "reviewer" && profile.role !== "super_admin") {
    redirect("/dashboard");
  }
  return children;
}
