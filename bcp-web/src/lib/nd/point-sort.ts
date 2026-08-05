import { comparePointNumber, sortByPointRef, type SortDir } from './list-utils';

export type PointSortMode = 'default' | 'number' | 'status';

export function complianceSeveritySortRank(severity: string): number {
  if (severity === 'non_compliant') return 0;
  if (severity === 'partial_compliant') return 1;
  if (severity === 'compliant') return 2;
  return 3;
}

export function togglePointSort(
  current: PointSortMode,
  dir: SortDir,
  next: 'number' | 'status',
): { sort: PointSortMode; dir: SortDir } {
  if (current === next) {
    return { sort: next, dir: dir === 'asc' ? 'desc' : 'asc' };
  }
  return { sort: next, dir: 'asc' };
}

export function sortByPointKey<T>(
  items: T[],
  sort: PointSortMode,
  dir: SortDir,
  pointKey: (item: T) => string,
  severityKey: (item: T) => string,
): T[] {
  if (!items.length) return [];
  if (sort === 'default') {
    return sortByPointRef(items, pointKey, 'asc');
  }
  const list = [...items];
  if (sort === 'number') {
    return list.sort((a, b) => comparePointNumber(pointKey(a), pointKey(b), dir));
  }
  return list.sort(
    (a, b) =>
      (dir === 'asc' ? 1 : -1) *
        (complianceSeveritySortRank(severityKey(a)) - complianceSeveritySortRank(severityKey(b))) ||
      comparePointNumber(pointKey(a), pointKey(b), 'asc'),
  );
}
