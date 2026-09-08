import './component-loader.mjs';
import '@angular/compiler';
import assert from 'node:assert/strict';
import test from 'node:test';
import { createEnvironmentInjector, runInInjectionContext } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { Subject } from 'rxjs';

const { TorrentComponent } = await import('../src/app/torrent/torrent.component.ts');
const { TorrentService } = await import('../src/app/torrent.service.ts');

function fixture(context) {
  const requests = [];
  const injector = createEnvironmentInjector([
    { provide: ActivatedRoute, useValue: {} },
    { provide: Router, useValue: {} },
    {
      provide: TorrentService,
      useValue: {
        update(payload) {
          const response = new Subject();
          requests.push({ payload, response });
          return response;
        },
      },
    },
  ]);
  context.after(() => injector.destroy());
  const component = runInInjectionContext(injector, () => new TorrentComponent());
  const original = Object.freeze({
    torrentId: 'test-torrent',
    category: 'original',
    priority: 1,
    downloadClient: 0,
    hostDownloadAction: 0,
    downloadRetryAttempts: 3,
    torrentRetryAttempts: 1,
    deleteOnError: 0,
    lifetime: 0,
    rdProgress: 40,
  });
  component.torrentState.set(original);
  component.showUpdateSettingsModal();
  component.updateSettingsCategory = 'changed';
  component.updateSettingsPriority = 2;
  return { component, original, requests };
}

test('failed settings save retains the displayed torrent, editable draft and open modal for retry', (context) => {
  const { component, original, requests } = fixture(context);
  component.updateSettingsOk();

  assert.equal(component.torrentState(), original);
  assert.equal(component.updating(), true);
  assert.deepEqual(requests[0].payload, { ...original, category: 'changed', priority: 2 });
  requests[0].response.error({ error: ' Invalid category. ' });

  assert.equal(component.torrentState(), original);
  assert.equal(component.updateSettingsCategory, 'changed');
  assert.equal(component.updateSettingsPriority, 2);
  assert.equal(component.isUpdateSettingsModalActive(), true);
  assert.equal(component.updating(), false);
  assert.equal(component.updateSettingsError(), 'Invalid category.');

  component.updateSettingsCategory = 'corrected';
  component.updateSettingsOk();
  assert.equal(component.updateSettingsError(), null);
  assert.equal(requests.length, 2);
  assert.equal(requests[1].payload.category, 'corrected');
  assert.equal(component.torrentState(), original);

  requests[1].response.next();
  requests[1].response.complete();
  assert.equal(component.isUpdateSettingsModalActive(), false);
  assert.equal(component.updating(), false);
  assert.deepEqual(component.torrentState(), { ...original, category: 'corrected', priority: 2 });
});

for (const error of ['', null, {}, { title: 'Bad Request' }]) {
  test(`non-text errors have readable retry feedback: ${JSON.stringify(error)}`, (context) => {
    const { component, requests } = fixture(context);
    component.updateSettingsOk();
    requests[0].response.error({ error });
    assert.equal(component.updateSettingsError(), 'Torrent settings could not be saved. Please try again.');
    assert.equal(component.isUpdateSettingsModalActive(), true);
    assert.equal(component.updating(), false);
  });
}

test('pending saves cannot be submitted twice or dismissed', (context) => {
  const { component, requests } = fixture(context);
  component.updateSettingsOk();
  component.updateSettingsOk();
  component.updateSettingsCancel();
  assert.equal(requests.length, 1);
  assert.equal(component.isUpdateSettingsModalActive(), true);

  requests[0].response.error({ error: 'Failure' });
  component.updateSettingsCancel();
  assert.equal(component.isUpdateSettingsModalActive(), false);
  component.showUpdateSettingsModal();
  assert.equal(component.updateSettingsCategory, 'original');
  assert.equal(component.updateSettingsError(), null);
});

test('a successful save preserves newer live download progress', (context) => {
  const { component, original, requests } = fixture(context);
  component.updateSettingsOk();
  component.update([{ ...original, rdProgress: 80 }]);
  requests[0].response.next();
  assert.deepEqual(component.torrentState(), { ...original, rdProgress: 80, category: 'changed', priority: 2 });
});

test('a save response cannot replace a different torrent after navigation', (context) => {
  const { component, requests } = fixture(context);
  component.updateSettingsOk();
  const otherTorrent = { torrentId: 'other-torrent', category: 'other' };
  component.torrentState.set(otherTorrent);
  requests[0].response.next();
  assert.equal(component.torrentState(), otherTorrent);
});
