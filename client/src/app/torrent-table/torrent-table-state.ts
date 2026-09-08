import type { Torrent } from '../models/torrent.model';

export const torrentColumns = [
  { key: 'rdName', label: 'Name', value: (torrent: Torrent) => torrent.rdName },
  { key: 'category', label: 'Category', value: (torrent: Torrent) => torrent.category },
  { key: 'priority', label: 'Priority', value: (torrent: Torrent) => torrent.priority },
  { key: 'files.length', label: 'Files', value: (torrent: Torrent) => torrent.files.length },
  { key: 'downloads.length', label: 'Downloads', value: (torrent: Torrent) => torrent.downloads.length },
  { key: 'rdSize', label: 'Size', value: (torrent: Torrent) => torrent.rdSize },
  {
    key: 'added',
    label: 'Requested',
    value: (torrent: Torrent) => (torrent.added instanceof Date ? torrent.added.getTime() : Date.parse(torrent.added)),
  },
  { key: 'rdStatus', label: 'Status', value: (torrent: Torrent) => torrent.rdStatus },
] as const;

export type TorrentSortKey = (typeof torrentColumns)[number]['key'];
export type SortDirection = 'asc' | 'desc';

export const selectionColumnWidth = 44;
export const maxColumnWidth = 16384;

const columnSizes = {
  rdName: { width: 320, min: 240 },
  category: { width: 140, min: 104 },
  priority: { width: 110, min: 110 },
  'files.length': { width: 88, min: 88 },
  'downloads.length': { width: 148, min: 148 },
  rdSize: { width: 112, min: 96 },
  added: { width: 148, min: 136 },
  rdStatus: { width: 176, min: 112 },
} satisfies Record<TorrentSortKey, { width: number; min: number }>;

export function torrentColumnLayout(containerWidth: number, overrides: Partial<Record<TorrentSortKey, number>>) {
  const columns = torrentColumns.map((column) => {
    const { width, min } = columnSizes[column.key];
    return {
      ...column,
      width: Math.max(min, Math.min(maxColumnWidth, overrides[column.key] ?? width)),
      minWidth: min,
    };
  });
  const name = columns[0];
  if (overrides.rdName === undefined) {
    const otherWidth = selectionColumnWidth + columns.slice(1).reduce((total, column) => total + column.width, 0);
    name.width = Math.min(maxColumnWidth, Math.max(columnSizes.rdName.width, containerWidth - otherWidth));
  }
  return {
    columns,
    width: selectionColumnWidth + columns.reduce((total, column) => total + column.width, 0),
  };
}

export function resizeTorrentColumn(
  widths: Partial<Record<TorrentSortKey, number>>,
  layout: ReturnType<typeof torrentColumnLayout>,
  key: TorrentSortKey,
  width: number | null
): Partial<Record<TorrentSortKey, number>> {
  const next = { ...widths };
  if (width === null) {
    delete next[key];
  } else {
    // Freeze the flexible column so the dragged edge follows the pointer.
    next.rdName ??= layout.columns[0].width;
    next[key] = width;
  }
  return next;
}

const nameComparer = new Intl.Collator(undefined, { numeric: true, sensitivity: 'base' });

export function visibleTorrents(
  torrents: readonly Torrent[],
  filter: string,
  sortKey: TorrentSortKey,
  direction: SortDirection
): Torrent[] {
  const search = filter.trim().toLocaleLowerCase();
  const rows = torrents.filter((torrent) => !search || torrent.rdName?.toLocaleLowerCase().includes(search));
  const { value } = torrentColumns.find((column) => column.key === sortKey)!;

  return rows.sort((left, right) => {
    const a = value(left);
    const b = value(right);
    const aMissing = a == null || Number.isNaN(a);
    const bMissing = b == null || Number.isNaN(b);
    if (aMissing || bMissing) {
      return Number(aMissing) - Number(bMissing);
    }

    const order = typeof a === 'string' && typeof b === 'string' ? nameComparer.compare(a, b) : Number(a) - Number(b);
    return direction === 'asc' ? order : -order;
  });
}

export function selectVisibleTorrents(
  selectedIds: readonly string[],
  visibleRows: readonly Torrent[],
  selected: boolean
): string[] {
  const visibleIds = new Set(visibleRows.map((torrent) => torrent.torrentId));
  return selected
    ? [...new Set([...selectedIds, ...visibleIds])]
    : selectedIds.filter((torrentId) => !visibleIds.has(torrentId));
}

export function visibleSelectionState(selectedIds: readonly string[], visibleRows: readonly Torrent[]) {
  const selected = new Set(selectedIds);
  const count = visibleRows.filter((torrent) => selected.has(torrent.torrentId)).length;
  return {
    all: visibleRows.length > 0 && count === visibleRows.length,
    some: count > 0 && count < visibleRows.length,
  };
}

export function pruneTorrentSelection(selectedIds: readonly string[], torrents: readonly Torrent[]): string[] {
  const currentIds = new Set(torrents.map((torrent) => torrent.torrentId));
  return selectedIds.filter((torrentId) => currentIds.has(torrentId));
}
