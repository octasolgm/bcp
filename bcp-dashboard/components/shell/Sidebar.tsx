"use client";

import Link from "next/link";
import { usePathname } from "next/navigation";
import { RoleBadge } from "./RoleBadge";
import type { UserProfile } from "@/lib/api/bcp-api-client";

type NavItem = { href: string; label: string };

function navForRole(role: string): NavItem[] {
  const common: NavItem[] = [{ href: "/dashboard", label: "Overview" }];
  switch (role) {
    case "super_admin":
      return [
        ...common,
        { href: "/admin/users", label: "User Management" },
        { href: "/admin/departments", label: "Department Management" },
        { href: "/regulation-documents", label: "Regulation Documents" },
        { href: "/internal-documents", label: "Internal Documents" },
        { href: "/libraries", label: "Libraries" },
        { href: "/run-analysis", label: "All Analysis Runs" },
      ];
    case "maker":
      return [
        ...common,
        { href: "/regulation-documents", label: "Regulation Documents" },
        { href: "/internal-documents", label: "Internal Documents" },
        { href: "/libraries", label: "Libraries" },
        { href: "/run-analysis", label: "Run Analysis" },
        { href: "/run-analysis?mine=1", label: "My Analysis Runs" },
      ];
    case "checker":
      return [
        ...common,
        { href: "/checker", label: "Review Queue" },
        { href: "/checker?history=1", label: "Review History" },
      ];
    case "reviewer":
      return [
        ...common,
        { href: "/reviewer", label: "Final Review Queue" },
        { href: "/reviewer?history=1", label: "Final Review History" },
      ];
    default:
      return common;
  }
}

export function Sidebar({ profile }: { profile: UserProfile }) {
  const pathname = usePathname();
  const items = navForRole(profile.role);

  return (
    <aside
      className="flex w-60 shrink-0 flex-col border-r border-[var(--border)] bg-[var(--bg-sidebar)]"
      style={{ minHeight: "100vh" }}
    >
      <div className="border-b border-[var(--border)] p-4">
        <div className="font-semibold text-[var(--accent)]">Reguliq</div>
        <div className="text-xs text-[var(--text-muted)]">Compliance workspace</div>
      </div>
      <nav className="flex-1 space-y-1 p-3">
        {items.map((item) => {
          const active =
            pathname === item.href ||
            (item.href !== "/dashboard" && pathname.startsWith(item.href.split("?")[0]));
          return (
            <Link
              key={item.href}
              href={item.href}
              className={`block rounded-lg px-3 py-2 text-sm ${
                active
                  ? "bg-[var(--bg-card)] text-[var(--accent)]"
                  : "text-[var(--text-muted)] hover:bg-[var(--bg-card)] hover:text-[var(--text-primary)]"
              }`}
            >
              {item.label}
            </Link>
          );
        })}
      </nav>
      <div className="border-t border-[var(--border)] p-4 text-sm">
        <div className="font-medium">{profile.fullName || "User"}</div>
        <RoleBadge role={profile.role} />
      </div>
    </aside>
  );
}
