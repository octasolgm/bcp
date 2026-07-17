import { redirect } from "next/navigation";
import { Sidebar } from "@/components/shell/Sidebar";
import { getServerProfile } from "@/lib/auth/helpers";

export default async function DashboardLayout({ children }: { children: React.ReactNode }) {
  const profile = await getServerProfile();
  if (!profile) redirect("/login");
  if (!profile.isActive) redirect("/login?deactivated=1");

  return (
    <div className="flex min-h-screen">
      <Sidebar profile={profile} />
      <main className="flex flex-1 flex-col">{children}</main>
    </div>
  );
}
