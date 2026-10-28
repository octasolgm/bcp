/** Multi-select state for a document catalog table (row checkboxes + select all). */
export class CatalogSelection {
  private readonly ids = new Set<string>();

  get size(): number {
    return this.ids.size;
  }

  has(id: string): boolean {
    return this.ids.has(id);
  }

  toggle(id: string): void {
    if (this.ids.has(id)) this.ids.delete(id);
    else this.ids.add(id);
  }

  clear(): void {
    this.ids.clear();
  }

  allSelected(ids: readonly string[]): boolean {
    return ids.length > 0 && ids.every((id) => this.ids.has(id));
  }

  someSelected(ids: readonly string[]): boolean {
    return ids.some((id) => this.ids.has(id)) && !this.allSelected(ids);
  }

  /** Select every given id, or deselect them all when they are all selected already. */
  toggleAll(ids: readonly string[]): void {
    if (this.allSelected(ids)) ids.forEach((id) => this.ids.delete(id));
    else ids.forEach((id) => this.ids.add(id));
  }

  /** Selected items among `items`, in their display order. */
  pick<T extends { id: string }>(items: readonly T[]): T[] {
    return items.filter((i) => this.ids.has(i.id));
  }

  /** Drop ids no longer present (deleted, filtered out of the data). */
  prune(validIds: readonly string[]): void {
    const keep = new Set(validIds);
    for (const id of [...this.ids]) if (!keep.has(id)) this.ids.delete(id);
  }
}
