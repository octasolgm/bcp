const ROLE_LABELS: Record<string, string> = {
  super_admin: "Super Admin",
  maker: "Maker",
  checker: "Checker",
  reviewer: "Reviewer",
};

export function RoleBadge({ role }: { role: string }) {
  const label = ROLE_LABELS[role] ?? role;
  const cls =
    role === "super_admin"
      ? "badge badge-blue"
      : role === "checker"
        ? "badge badge-amber"
        : role === "reviewer"
          ? "badge badge-green"
          : "badge badge-gray";
  return <span className={cls}>{label}</span>;
}
