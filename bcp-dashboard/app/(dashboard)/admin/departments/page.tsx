"use client";

import { useEffect, useState } from "react";
import { TopNav } from "@/components/shell/TopNav";
import { StatusBadge } from "@/components/ui/StatusBadge";
import {
  createDepartment,
  deleteDepartment,
  getDepartments,
  getProfile,
  updateDepartment,
} from "@/lib/api/bcp-api-client";
import type { UserProfile } from "@/lib/api/bcp-api-client";
import { getClientToken } from "@/lib/auth/client-token";
import type { Department } from "@/lib/types";

export default function AdminDepartmentsPage() {
  const [profile, setProfile] = useState<UserProfile | null>(null);
  const [departments, setDepartments] = useState<Department[]>([]);
  const [name, setName] = useState("");
  const [description, setDescription] = useState("");
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");

  async function load() {
    const token = await getClientToken();
    if (!token) return;
    const [profRes, deptRes] = await Promise.all([
      getProfile(token),
      getDepartments(token),
    ]);
    if (profRes.success && profRes.data) setProfile(profRes.data);
    if (deptRes.success && deptRes.data) setDepartments(deptRes.data as Department[]);
    setLoading(false);
  }

  useEffect(() => {
    load();
  }, []);

  async function handleCreate(e: React.FormEvent) {
    e.preventDefault();
    const token = await getClientToken();
    if (!token) return;
    const res = await createDepartment(token, { name: name.trim(), description: description.trim() });
    if (res.success) {
      setName("");
      setDescription("");
      await load();
    } else {
      setError(res.message ?? "Failed to create");
    }
  }

  async function toggleActive(dept: Department) {
    const token = await getClientToken();
    if (!token) return;
    await updateDepartment(token, dept.id, {
      name: dept.name,
      description: dept.description ?? undefined,
      isActive: !dept.isActive,
    });
    await load();
  }

  async function handleDelete(id: string) {
    if (!confirm("Delete department?")) return;
    const token = await getClientToken();
    if (!token) return;
    const res = await deleteDepartment(token, id);
    if (!res.success) setError(res.message ?? "Failed to delete");
    else await load();
  }

  if (!profile) {
    return <p className="p-6 text-sm text-[var(--text-muted)]">Loading…</p>;
  }

  if (profile.role !== "super_admin") {
    return <p className="p-6 text-sm text-red-400">Access denied</p>;
  }

  return (
    <>
      <TopNav title="Department Management" profile={profile} />
      <div className="p-6">
        <form onSubmit={handleCreate} className="card mb-6 grid gap-4 p-4 md:grid-cols-3">
          <label className="block text-sm">
            Name
            <input className="input mt-1" value={name} onChange={(e) => setName(e.target.value)} required />
          </label>
          <label className="block text-sm">
            Description
            <input
              className="input mt-1"
              value={description}
              onChange={(e) => setDescription(e.target.value)}
            />
          </label>
          <div className="flex items-end">
            <button type="submit" className="btn-primary">
              Add department
            </button>
          </div>
        </form>

        {error && <p className="mb-4 text-sm text-red-400">{error}</p>}

        {loading ? (
          <p className="text-sm text-[var(--text-muted)]">Loading…</p>
        ) : (
          <div className="card divide-y divide-[var(--border)]">
            {departments.map((d) => (
              <div key={d.id} className="flex flex-wrap items-center justify-between gap-4 px-4 py-3">
                <div>
                  <div className="font-medium">{d.name}</div>
                  {d.description && (
                    <p className="text-sm text-[var(--text-muted)]">{d.description}</p>
                  )}
                  <div className="text-xs text-[var(--text-muted)]">
                    {d.userCount ?? 0} users · {d.documentCount ?? 0} docs · {d.libraryCount ?? 0} libraries
                  </div>
                </div>
                <div className="flex items-center gap-3">
                  <StatusBadge status={d.isActive ? "completed" : "failed"} />
                  <button
                    type="button"
                    className="btn-secondary text-sm"
                    onClick={() => toggleActive(d)}
                  >
                    {d.isActive ? "Deactivate" : "Activate"}
                  </button>
                  <button
                    type="button"
                    className="btn-secondary text-sm text-red-400"
                    onClick={() => handleDelete(d.id)}
                  >
                    Delete
                  </button>
                </div>
              </div>
            ))}
          </div>
        )}
      </div>
    </>
  );
}
