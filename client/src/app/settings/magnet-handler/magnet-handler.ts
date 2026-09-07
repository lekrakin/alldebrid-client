export interface MagnetHandlerBrowser {
  readonly isSecureContext: boolean;
  readonly location: Pick<Location, 'origin'>;
  readonly navigator: {
    registerProtocolHandler?: (scheme: string, url: string) => void;
  };
}

export type MagnetHandlerAvailability = 'available' | 'insecure' | 'unsupported';
export type MagnetHandlerRequest = { status: 'requested' } | { status: 'error'; message: string };

export function getMagnetHandlerAvailability(browser: MagnetHandlerBrowser | null): MagnetHandlerAvailability {
  if (!browser) {
    return 'unsupported';
  }

  if (!browser.isSecureContext) {
    return 'insecure';
  }

  return typeof browser.navigator.registerProtocolHandler === 'function' ? 'available' : 'unsupported';
}

function appUrl(path: string, origin: string, baseHref: string): URL {
  const base = new URL(baseHref, origin);

  if (base.origin !== origin) {
    throw new Error('The handler address must use the same origin as this application.');
  }

  base.pathname = `${base.pathname.replace(/\/+$/, '')}/`;
  return new URL(path, base);
}

export function requestMagnetHandler(browser: MagnetHandlerBrowser | null, baseHref: string): MagnetHandlerRequest {
  const register = browser?.navigator.registerProtocolHandler;

  if (!browser?.isSecureContext || typeof register !== 'function') {
    return { status: 'error', message: 'Magnet-link registration is not available on this page.' };
  }

  try {
    const handler = appUrl('add?magnet=%s', browser.location.origin, baseHref);
    register.call(browser.navigator, 'magnet', handler.href);

    // The API returns no permission result, including when a handler already exists or the user declines.
    return { status: 'requested' };
  } catch (error) {
    const blocked = error instanceof Error && error.name === 'SecurityError';
    return {
      status: 'error',
      message: blocked
        ? 'The browser blocked registration. Check its protocol-handler permissions for this site.'
        : 'The browser could not request registration. Check its protocol-handler settings and try again.',
    };
  }
}

export function getLocalSettingsUrl(browser: MagnetHandlerBrowser | null, baseHref: string): string | null {
  if (!browser || getMagnetHandlerAvailability(browser) !== 'insecure') {
    return null;
  }

  try {
    const url = appUrl('settings', browser.location.origin, baseHref);

    if (url.protocol !== 'http:') {
      return null;
    }

    url.hostname = 'localhost';
    return url.href;
  } catch {
    return null;
  }
}
