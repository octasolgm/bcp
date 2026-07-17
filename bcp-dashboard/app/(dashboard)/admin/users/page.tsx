"use client";

import { useEffect, useState } from "react";
import { TopNav } from "@/components/shell/TopNav";
import { RoleBadge } from "@/components/shell/RoleBadge";
import {
  activateUser,
  deactivateUser,
  getDepartments,
  getProfile,
  getUsers,
  inviteUser,
  updateUser,
} from "@/lib/api/bcp-api-client";
import type { UserProfile } from "@/lib/api/bcp-api-client";
import { getClientToken } from "@/lib/auth/client-token";
import type { Department } from "@/lib/types";

type AdminUser = {
  id: string;
  fullName: string;
  role: string;
  departmentId?: string | null;
  departmentName?: string | null;
  isActive: boolean;
  createdAt: string;
};

export default function AdminUsersPage() {
  const [profile, setProfile] = useState<UserProfile | null>(null);
  const [users, setUsers] = useState<AdminUser[]>([]);
  const [departments, setDepartments] = useState<Department[]>([]);
  const [inviteName, setInviteName] = useState("");
  const [inviteEmail, setInviteEmail] = useState("");
  const [inviteRole, setInviteRole] = useState("maker");
  const [inviteDept, setInviteDept] = useState("");
  const [loading, setLoading] = useState(true);
  const [inviting, setInviting] = useState(false);
  const [message, setMessage] = useState("");
  const [error, setError] = useState("");

  async function load() {
    const token = await getClientToken();
    if (!token) return;
    const [profRes, usersRes, deptRes] = await Promise.all([
      getProfile(token),
      getUsers(token),
      getDepartments(token),
    ]);
    if (profRes.success && profRes.data) setProfile(profRes.data);
    if (usersRes.success && usersRes.data) setUsers(usersRes.data as AdminUser[]);
    if (deptRes.success && deptRes.data) setDepartments(deptRes.data as Department[]);
    setLoading(false);
  }

  useEffect(() => {
    load();
  }, []);

  async function handleInvite(e: React.FormEvent) {
    e.preventDefault();
    setInviting(true);
    setError("");
    setMessage("");
    const token = await getClientToken();
    if (!token) return;
    const res = await inviteUser(token, {
      fullName: inviteName.trim(),
      email: inviteEmail.trim(),
      role: inviteRole,
      departmentId: inviteDept || undefined,
    });
    if (res.success) {
      setMessage("Invitation sent");
      setInviteName("");
      setInviteEmail("");
      await load();
    } else {
      setError(res.message ?? "Invite failed");
    }
    setInviting(false);
  }

  async function handleRoleChange(userId: string, role: string) {
    const token = await getClientToken();
    if (!token) return;
    await updateUser(token, userId, { role });
    await load();
  }

  async function handleDeptChange(userId: string, departmentId: string) {
    const token = await getClientToken();
    if (!token) return;
    await updateUser(token, userId, { departmentId: departmentId || null });
    await load();
  }

  async function toggleActive(user: AdminUser) {
    const token = await getClientToken();
    if (!token) return;
    if (user.isActive) await deactivateUser(token, user.id);
    else await activateUser(token, user.id);
    await load();
  }

  if (!profile) {
    return <p className="p-6 text-sm text-[var(--text-muted)]">Loading…</p>;
  }

  if (profile.role !== "super_admin") {
    return <p className="p-6 text-sm text-red-400">Access denied</p>;
  }

  return (
    <>
      <TopNav title="User Management" profile={profile} />
      <div className="p-6">
        <form onSubmit={handleInvite} className="card mb-6 space-y-4 p-4">
          <h2 className="font-medium">Invite user</h2>
          <div className="grid gap-4 md:grid-cols-2 lg:grid-cols-4">
            <label className="block text-sm">
              Full name
              <input
                className="input mt-1"
                value={inviteName}
                onChange={(e) => setInviteName(e.target.value)}
                required
              />
            </label>
            <label className="block text-sm">
              Email
              <input
                className="input mt-1"
                type="email"
                value={inviteEmail}
                onChange={(e) => setInviteEmail(e.target.value)}
                required
              />
            </label>
            <label className="block text-sm">
              Role
              <select
                className="input mt-1"
                value={inviteRole}
                onChange={(e) => setInviteRole(e.target.value)}
              >
                <option value="maker">Maker</option>
                <option value="checker">Checker</option>
                <option value="reviewer">Reviewer</option>
                <option value="super_admin">Super Admin</option>
              </select>
            </label>
            <label className="block text-sm">
              Department
              <select
                className="input mt-1"
                value={inviteDept}
                onChange={(e) => setInviteDept(e.target.value)}
              >
                <option value="">None</option>
                {departments.map((d) => (
                  <option key={d.id} value={d.id}>
                    {d.name}
                  </option>
                ))}
              </select>
            </label>
          </div>
          <button type="submit" className="btn-primary" disabled={inviting}>
            {inviting ? "Sending…" : "Send invite"}
          </button>
        </form>

        {message && <p className="mb-4 text-sm text-green-400">{message}</p>}
        {error && <p className="mb-4 text-sm text-red-400">{error}</p>}

        {loading ? (
          <p className="text-sm text-[var(--text-muted)]">Loading…</p>
        ) : (
          <div className="card overflow-x-auto">
            <table className="w-full text-sm">
              <thead>
                <tr className="border-b border-[var(--border)] text-left text-[var(--text-muted)]">
                  <th className="px-4 py-3">Name</th>
                  <th className="px-4 py-3">Role</th>
                  <th className="px-4 py-3">Department</th>
                  <th className="px-4 py-3">Status</th>
                  <th className="px-4 py-3">Actions</th>
                </tr>
              </thead>
              <tbody>
                {users.map((u) => (
                  <tr key={u.id} className="border-b border-[var(--border)]">
                    <td className="px-4 py-3 font-medium">{u.fullName}</td>
                    <td className="px-4 py-3">
                      <select
                        className="input"
                        value={u.role}
                        onChange={(e) => handleRoleChange(u.id, e.target.value)}
                      >
                        <option value="maker">Maker</option>
                        <option value="checker">Checker</option>
                        <option value="reviewer">Reviewer</option>
                        <option value="super_admin">Super Admin</option>
                      </select>
                    </td>
                    <td className="px-4 py-3">
                      <select
                        className="input"
                        value={u.departmentId ?? ""}
                        onChange={(e) => handleDeptChange(u.id, e.target.value)}
                      >
                        <option value="">None</option>
                        {departments.map((d) => (
                          <option key={d.id} value={d.id}>
                            {d.name}
                          </option>
                        ))}
                      </select>
                    </td>
                    <td className="px-4 py-3">
                      <RoleBadge role={u.role} />
                      {!u.isActive && (
                        <span className="ml-2 badge badge-red">Inactive</span>
                      )}
                    </td>
                    <td className="px-4 py-3">
                      <button
                        type="button"
                        className="btn-secondary text-xs"
                        onClick={() => toggleActive(u)}
                      >
                        {u.isActive ? "Deactivate" : "Activate"}
                      </button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>
    </>
  );
}
