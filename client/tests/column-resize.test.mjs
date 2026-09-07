import assert from 'node:assert/strict';
import test from 'node:test';
import { clampColumnWidth, columnWidthForKey } from '../src/app/shared/column-resize/column-resize.ts';
import {
  maxColumnWidth,
  resizeTorrentColumn,
  selectionColumnWidth,
  torrentColumnLayout,
} from '../src/app/torrent-table/torrent-table-state.ts';

const widthOf = (layout, key) => layout.columns.find((column) => column.key === key).width;

test('width changes clamp to bounds and round to whole pixels', () => {
  assert.equal(clampColumnWidth(135.6, 80, 500), 136);
  assert.equal(clampColumnWidth(-40, 80, 500), 80);
  assert.equal(clampColumnWidth(800, 80, 500), 500);
  assert.equal(clampColumnWidth(Number.NaN, 80, 500), 80);
});

test('arrow keys resize with bounded small and large steps', () => {
  assert.equal(columnWidthForKey('ArrowRight', 140, 80, 500, false), 156);
  assert.equal(columnWidthForKey('ArrowLeft', 140, 80, 500, false), 124);
  assert.equal(columnWidthForKey('ArrowRight', 140, 80, 500, true), 204);
  assert.equal(columnWidthForKey('ArrowLeft', 140, 80, 500, true), 80);
  assert.equal(columnWidthForKey('ArrowRight', 490, 80, 500, false), 500);
});

test('Home/End use limits, Enter resets, and unrelated keys remain unhandled', () => {
  assert.equal(columnWidthForKey('Home', 140, 80, 500, false), 80);
  assert.equal(columnWidthForKey('End', 140, 80, 500, false), 500);
  assert.equal(columnWidthForKey('Enter', 140, 80, 500, false), null);
  for (const key of ['Tab', 'Escape', 'ArrowUp', 'a']) {
    assert.equal(columnWidthForKey(key, 140, 80, 500, false), undefined);
  }
});

test('wide layouts give spare space to Name without stretching other columns', () => {
  const regular = torrentColumnLayout(1920, {});
  const wide = torrentColumnLayout(2560, {});
  assert.equal(regular.width, 1920);
  assert.equal(wide.width, 2560);
  assert.equal(widthOf(wide, 'rdName') - widthOf(regular, 'rdName'), 640);
  assert.deepEqual(wide.columns.slice(1), regular.columns.slice(1));
});

test('narrow layouts preserve readable widths and overflow horizontally', () => {
  const layout = torrentColumnLayout(390, {});
  assert.ok(layout.width > 390);
  for (const column of layout.columns) {
    assert.ok(column.width >= column.minWidth);
  }
  assert.equal(layout.width, selectionColumnWidth + layout.columns.reduce((total, column) => total + column.width, 0));
});

test('manual resize freezes flexible Name so the dragged edge moves predictably', () => {
  const current = torrentColumnLayout(1920, {});
  const defaults = Object.freeze({});
  const overrides = resizeTorrentColumn(defaults, current, 'category', 200);
  const resized = torrentColumnLayout(1920, overrides);
  assert.equal(widthOf(resized, 'rdName'), widthOf(current, 'rdName'));
  assert.equal(widthOf(resized, 'category'), 200);
  assert.equal(resized.width, current.width + 200 - widthOf(current, 'category'));
  assert.deepEqual(defaults, {});
});

test('explicit widths remain stable when viewport size changes', () => {
  const current = torrentColumnLayout(1920, {});
  const overrides = resizeTorrentColumn({}, current, 'rdName', 500);
  assert.deepEqual(torrentColumnLayout(1280, overrides), torrentColumnLayout(2560, overrides));
});

test('resetting Name restores flexible width and resetting all restores defaults', () => {
  const current = torrentColumnLayout(1920, {});
  const overrides = Object.freeze({ rdName: 500, category: 200 });
  const reset = resizeTorrentColumn(overrides, current, 'rdName', null);
  assert.equal(reset.rdName, undefined);
  assert.equal(reset.category, 200);
  assert.equal(torrentColumnLayout(1920, reset).width, 1920);
  assert.deepEqual(torrentColumnLayout(1920, {}), current);
  assert.deepEqual(overrides, { rdName: 500, category: 200 });
});

test('individual columns respect minimum and maximum widths', () => {
  const layout = torrentColumnLayout(1920, { rdName: 1, 'files.length': 1, category: maxColumnWidth + 100 });
  assert.equal(widthOf(layout, 'rdName'), 240);
  assert.equal(widthOf(layout, 'files.length'), 88);
  assert.equal(widthOf(layout, 'category'), maxColumnWidth);
});
