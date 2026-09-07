import type { Torrent } from '../models/torrent.model';

export const torrentColumns = [
  { key: 'rdName', label: 'Name', value: (torrent: Torrent) => torrent.rdName },
  { key: 'category', label: 'Category', value: (torrent: Torrent) => torrent.category },
  { key: 'priority', label: 'Priority', value: (torrent: Torrent) => torrent.priority },
  { key: 'rdSeeders', label: 'Seeders', value: (torrent: Torrent) => torrent.rdSeeders },
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
