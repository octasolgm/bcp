/** Orders departments as their tree (department, then its units, then their teams) for pickers and dropdowns. */

export type NdTreeDepartment = { id: string; name: string; parentId?: string | null };

export type NdTreeRow<T extends NdTreeDepartment> = {
  dept: T;
  /** 0 = top level, 1 = unit, 2 = team. */
  level: number;
  /** Full name with parents, e.g. "Compliance › AML/CFT › Sanctions". */
  path: string;
};

export function ndDepartmentTreeRows<T extends NdTreeDepartment>(departments: T[]): NdTreeRow<T>[] {
  const ids = new Set(departments.map((d) => d.id));
  const children = new Map<string | null, T[]>();
  for (const d of departments) {
    // A parent that is not in the list (inactive or removed) puts the department at the top level.
    const parent = d.parentId && ids.has(d.parentId) ? d.parentId : null;
    const list = children.get(parent) ?? [];
    list.push(d);
    children.set(parent, list);
  }
  for (const list of children.values()) list.sort((a, b) => a.name.localeCompare(b.name));

  const rows: NdTreeRow<T>[] = [];
  const seen = new Set<string>();
  const walk = (parent: string | null, level: number, prefix: string) => {
    for (const d of children.get(parent) ?? []) {
      if (seen.has(d.id)) continue;
      seen.add(d.id);
      const path = prefix ? `${prefix} › ${d.name}` : d.name;
      rows.push({ dept: d, level, path });
      walk(d.id, level + 1, path);
    }
  };
  walk(null, 0, '');
  return rows;
}

/** Option text for a native select: indented by level so the tree is visible. */
export function ndIndentedName(row: { level: number; dept: { name: string } }): string {
  return `${'    '.repeat(row.level)}${row.level ? '└ ' : ''}${row.dept.name}`;
}
