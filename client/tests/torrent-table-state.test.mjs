import assert from 'node:assert/strict';
import test from 'node:test';
import {
  pruneTorrentSelection,
  selectVisibleTorrents,
  torrentColumns,
  visibleSelectionState,
  visibleTorrents,
} from '../src/app/torrent-table/torrent-table-state.ts';

function torrent(torrentId, overrides = {}) {
  return {
    torrentId,
    rdName: torrentId,
    category: '',
    priority: 0,
    files: [],
    downloads: [],
    rdSize: 0,
    added: '2026-09-01T12:00:00Z',
    rdStatus: 0,
    ...overrides,
  };
}

const ids = (rows) => rows.map((row) => row.torrentId);

test('every displayed sortable column has a selector', () => {
  assert.deepEqual(
    torrentColumns.map(({ key }) => key),
    ['rdName', 'category', 'priority', 'files.length', 'downloads.length', 'rdSize', 'added', 'rdStatus']
  );
});

test('name filtering trims whitespace, ignores case, and handles unnamed rows', () => {
  const rows = [torrent('one', { rdName: 'One Pace 01' }), torrent('two'), torrent('unnamed', { rdName: null })];
  assert.deepEqual(ids(visibleTorrents(rows, '  PACE  ', 'rdName', 'asc')), ['one']);
  assert.equal(visibleTorrents(rows, '   ', 'rdName', 'asc').length, 3);
});

test('name sorting is natural and case insensitive without modifying the source list', () => {
  const rows = Object.freeze([
    Object.freeze(torrent('ten', { rdName: 'Episode 10' })),
    Object.freeze(torrent('two', { rdName: 'episode 2' })),
    Object.freeze(torrent('one', { rdName: 'Episode 1' })),
  ]);
  assert.deepEqual(ids(visibleTorrents(rows, '', 'rdName', 'asc')), ['one', 'two', 'ten']);
  assert.deepEqual(ids(visibleTorrents(rows, '', 'rdName', 'desc')), ['ten', 'two', 'one']);
  assert.deepEqual(ids(rows), ['ten', 'two', 'one']);
});

test('file and download columns sort by their counts, not dotted property names', () => {
  const rows = [
    torrent('one', { files: [{}], downloads: [{}, {}] }),
    torrent('three', { files: [{}, {}, {}], downloads: [] }),
    torrent('two', { files: [{}, {}], downloads: [{}] }),
  ];
  assert.deepEqual(ids(visibleTorrents(rows, '', 'files.length', 'asc')), ['one', 'two', 'three']);
  assert.deepEqual(ids(visibleTorrents(rows, '', 'files.length', 'desc')), ['three', 'two', 'one']);
  assert.deepEqual(ids(visibleTorrents(rows, '', 'downloads.length', 'asc')), ['three', 'two', 'one']);
});

for (const key of ['priority', 'rdSize', 'rdStatus']) {
  test(`${key} sorts numerically in both directions and preserves equal-value order`, () => {
    const rows = [torrent('ten', { [key]: 10 }), torrent('first', { [key]: 2 }), torrent('second', { [key]: 2 })];
    assert.deepEqual(ids(visibleTorrents(rows, '', key, 'asc')), ['first', 'second', 'ten']);
    assert.deepEqual(ids(visibleTorrents(rows, '', key, 'desc')), ['ten', 'first', 'second']);
  });
}

test('requested date sorting accepts API timestamps and Date values, with missing dates last', () => {
  const rows = [
    torrent('later', { added: '2026-09-07T12:00:00Z' }),
    torrent('invalid', { added: 'invalid' }),
    torrent('earlier', { added: new Date('2026-09-07T08:30:00-03:00') }),
    torrent('missing', { added: null }),
  ];
  assert.deepEqual(ids(visibleTorrents(rows, '', 'added', 'asc')), ['earlier', 'later', 'invalid', 'missing']);
  assert.deepEqual(ids(visibleTorrents(rows, '', 'added', 'desc')), ['later', 'earlier', 'invalid', 'missing']);
});

test('select all only selects matching rows and is idempotent', () => {
  const rows = [torrent('first'), torrent('second'), torrent('hidden')];
  const visibleRows = visibleTorrents(rows, 'first', 'rdName', 'asc');
  const selected = selectVisibleTorrents([], visibleRows, true);
  assert.deepEqual(selected, ['first']);
  assert.deepEqual(selectVisibleTorrents(selected, visibleRows, true), ['first']);
  assert.deepEqual(selectVisibleTorrents(selected, visibleRows, false), []);
});

test('selecting or deselecting visible rows preserves explicitly selected hidden rows', () => {
  const rows = [torrent('first'), torrent('second')];
  const selected = Object.freeze(['hidden', 'first']);
  assert.deepEqual(selectVisibleTorrents(selected, rows, true), ['hidden', 'first', 'second']);
  assert.deepEqual(selectVisibleTorrents(selected, rows, false), ['hidden']);
  assert.deepEqual(selected, ['hidden', 'first']);
});

test('header selection distinguishes none, mixed, all, and an empty filtered list', () => {
  const rows = [torrent('first'), torrent('second')];
  assert.deepEqual(visibleSelectionState([], rows), { all: false, some: false });
  assert.deepEqual(visibleSelectionState(['first', 'hidden'], rows), { all: false, some: true });
  assert.deepEqual(visibleSelectionState(['first', 'second', 'hidden'], rows), { all: true, some: false });
  assert.deepEqual(visibleSelectionState(['hidden'], []), { all: false, some: false });
});

test('refresh prunes deleted selections and is idempotent without changing selection order', () => {
  const selected = Object.freeze(['second', 'removed', 'first']);
  const rows = [torrent('first'), torrent('second')];
  const refreshed = pruneTorrentSelection(selected, rows);
  assert.deepEqual(refreshed, ['second', 'first']);
  assert.deepEqual(pruneTorrentSelection(refreshed, rows), refreshed);
  assert.deepEqual(pruneTorrentSelection(refreshed, []), []);
  assert.deepEqual(selected, ['second', 'removed', 'first']);
});
