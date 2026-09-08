import assert from 'node:assert/strict';
import { test } from 'node:test';
import '@angular/compiler';
import { DefaultUrlSerializer } from '@angular/router';
import {
  getLocalSettingsUrl,
  getMagnetHandlerAvailability,
  requestMagnetHandler,
} from '../src/app/settings/magnet-handler/magnet-handler.ts';

function browser({ origin = 'https://downloads.example', secure = true, register = () => {} } = {}) {
  return {
    isSecureContext: secure,
    location: { origin },
    navigator: { registerProtocolHandler: register },
  };
}

test('registration requires both a secure context and the browser API', () => {
  assert.equal(getMagnetHandlerAvailability(browser()), 'available');
  assert.equal(getMagnetHandlerAvailability(browser({ secure: false })), 'insecure');
  assert.equal(getMagnetHandlerAvailability(browser({ register: null })), 'unsupported');
  assert.equal(getMagnetHandlerAvailability(null), 'unsupported');
});

test('a trustworthy HTTP localhost context is supported without requiring HTTPS', () => {
  assert.equal(getMagnetHandlerAvailability(browser({ origin: 'http://localhost:6500' })), 'available');
});

test('an insecure LAN context never calls the registration API', () => {
  const page = browser({
    origin: 'http://192.168.0.150:6500',
    secure: false,
    register: () => assert.fail('Registration must not bypass browser security'),
  });
  assert.equal(requestMagnetHandler(page, '/').status, 'error');
});

for (const baseHref of ['/', '/client/', '/client']) {
  test(`registers a same-origin handler under base path ${baseHref}`, () => {
    const calls = [];
    const page = browser({
      register(scheme, url) {
        assert.equal(this, page.navigator);
        calls.push({ scheme, url });
      },
    });
    const expectedPath = baseHref === '/' ? '/add' : '/client/add';
    assert.deepEqual(requestMagnetHandler(page, baseHref), { status: 'requested' });
    assert.deepEqual(calls, [{ scheme: 'magnet', url: `https://downloads.example${expectedPath}?magnet=%s` }]);
  });
}

test('never registers a cross-origin base URL', () => {
  const page = browser({ register: () => assert.fail('Cross-origin handler must not be registered') });
  assert.equal(requestMagnetHandler(page, 'https://another.example/').status, 'error');
});

test('a successful API call reports a request, not an accepted or active handler', () => {
  const page = browser();
  assert.deepEqual(requestMagnetHandler(page, '/'), { status: 'requested' });
  assert.deepEqual(requestMagnetHandler(page, '/'), { status: 'requested' });
});

test('a blocked request is actionable and a later retry can succeed', () => {
  let blocked = true;
  const page = browser({
    register() {
      if (blocked) throw new DOMException('Blocked', 'SecurityError');
    },
  });
  const denied = requestMagnetHandler(page, '/');
  assert.equal(denied.status, 'error');
  assert.match(denied.message, /protocol-handler permissions/);
  blocked = false;
  assert.deepEqual(requestMagnetHandler(page, '/'), { status: 'requested' });
});

test('other browser errors remain failures, not false registration success', () => {
  const page = browser({
    register: () => {
      throw new Error('Unavailable');
    },
  });
  assert.equal(requestMagnetHandler(page, '/').status, 'error');
});

test('the explicit local-computer link preserves port and application base path', () => {
  const page = browser({ origin: 'http://192.168.0.150:6500', secure: false });
  assert.equal(getLocalSettingsUrl(page, '/'), 'http://localhost:6500/settings');
  assert.equal(getLocalSettingsUrl(page, '/client'), 'http://localhost:6500/client/settings');
  assert.equal(getLocalSettingsUrl(browser(), '/'), null);
  assert.equal(getLocalSettingsUrl(null, '/'), null);
  assert.equal(getLocalSettingsUrl(browser({ secure: false }), '/'), null);
});

for (const magnet of [
  'magnet:?xt=urn:btih:a031cac6baf81b804c4d034dfaef0e5e4a671145&dn=One%20Pace&tr=https%3A%2F%2Ftracker.example%2Fa%3Fkey%3Da%252Fb',
  'magnet:?xt=urn:btih:a031cac6baf81b804c4d034dfaef0e5e4a671145&dn=100% complete + extras',
]) {
  test(`the handler round-trips encoded content through Angular queryParamMap: ${magnet.includes('100%') ? 'literal percent' : 'nested escapes'}`, () => {
    let handler;
    requestMagnetHandler(
      browser({
        register: (_scheme, url) => {
          handler = url;
        },
      }),
      '/client/'
    );
    const openedUrl = new URL(handler.replace('%s', encodeURIComponent(magnet)));
    const params = new DefaultUrlSerializer().parse(`${openedUrl.pathname}${openedUrl.search}`).queryParamMap;
    assert.equal(params.get('magnet'), magnet);
  });
}
