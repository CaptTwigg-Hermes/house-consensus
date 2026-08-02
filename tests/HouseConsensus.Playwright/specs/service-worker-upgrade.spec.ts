import { expect, test } from '@playwright/test';
import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { createServer } from 'node:http';
import type { AddressInfo } from 'node:net';

const deployedWorker = `self.importScripts('./service-worker-assets.js');
const CACHE = 'hc-static-v1';
self.addEventListener('install', event => event.waitUntil(caches.open(CACHE).then(cache => cache.addAll(self.assetsManifest.assets.filter(a => !a.url.endsWith('.pdb')).map(a => new Request(a.url, { integrity: a.hash, cache: 'no-cache' }))))));
self.addEventListener('activate', event => event.waitUntil(caches.keys().then(keys => Promise.all(keys.filter(k => k !== CACHE).map(k => caches.delete(k)))).then(() => self.clients.claim())));
self.addEventListener('fetch', event => { if (event.request.method !== 'GET' || new URL(event.request.url).pathname.startsWith('/api/') || new URL(event.request.url).pathname.startsWith('/hubs/')) return; event.respondWith(caches.match(event.request).then(cached => cached || fetch(event.request))); });`;

function integrity(content: string) {
  return `sha256-${createHash('sha256').update(content).digest('base64')}`;
}

test('deployed worker upgrades atomically without stale vote-dialog CSS', async ({ browser }) => {
  test.setTimeout(90_000);
  let release: 'old' | 'new' | 'next' | 'final' = 'old';
  const candidateWorker = readFileSync('../../src/Client/wwwroot/service-worker.published.js', 'utf8');
  const candidateApp = readFileSync('../../src/Client/wwwroot/js/app.js', 'utf8');
  const deployedApp = `navigator.serviceWorker.register('/service-worker.js');`;
  let failPrecache = false;
  let failMapWrite = false;
  let networkValue = 'one';
  let crossRequests = 0;
  const css = {
    old: '.listing-card { width: 100px; height: 100px; } .listing-card:hover { transform: translateY(-4px); }',
    new: '.listing-card { width: 100px; height: 100px; } .listing-card:hover { transform: translateY(-4px); } .listing-card:has(.sheet-backdrop) { transform: none; }',
    next: '.listing-card { width: 100px; height: 100px; } .listing-card:hover { transform: translateY(-4px); } .listing-card:has(.sheet-backdrop) { transform: none; }',
    final: '.listing-card { width: 100px; height: 100px; } .listing-card:hover { transform: translateY(-4px); } .listing-card:has(.sheet-backdrop) { transform: none; }',
  };
  const lazy = { old: 'old-lazy', new: 'new-lazy', next: 'next-lazy', final: 'final-lazy' };
  const html = (version: 'old' | 'new' | 'next' | 'final') => `<!doctype html><link rel="stylesheet" href="/css/app.css"><div id="release">${version}</div><button id="before-dialog">Before</button><div class="listing-card"><div class="sheet-backdrop"></div></div><div id="vote-dialog"><button>Vote</button></div><script src="/app.js"></script>`;
  const server = createServer((request, response) => {
    response.setHeader('Cache-Control', 'no-store');
    if (request.url?.startsWith('/service-worker.js')) {
      response.setHeader('Content-Type', 'text/javascript');
      let worker = release === 'old' ? deployedWorker : candidateWorker;
      if (failMapWrite)
        worker = worker.replace('recordClientRelease(event.resultingClientId, CACHE)', `((await fetch('/map-write-mode')).ok ? recordClientRelease(event.resultingClientId, CACHE) : Promise.reject(new Error('forced navigation map write failure')))`);
      response.end(`${worker}\n/* ${release} */`);
      return;
    }
    if (request.url?.startsWith('/service-worker-assets.js')) {
      response.setHeader('Content-Type', 'text/javascript');
      const manifestRelease = request.url.includes('hc=upgrade-v2') ? release : 'old';
      const app = manifestRelease === 'old' ? deployedApp : candidateApp;
      response.end(`self.assetsManifest = ${JSON.stringify({ version: manifestRelease, assets: [{ url: 'index.html', hash: integrity(html(manifestRelease)) }, { url: 'css/app.css', hash: integrity(css[manifestRelease]) }, { url: 'app.js', hash: integrity(app) }, { url: 'lazy.js', hash: integrity(lazy[manifestRelease]) }] })};`);
      return;
    }
    if (request.url === '/map-write-mode') {
      response.statusCode = failMapWrite ? 503 : 204;
      response.end();
      return;
    }
    if (request.url === '/app.js') {
      response.setHeader('Content-Type', 'text/javascript');
      response.end(release === 'old' ? deployedApp : candidateApp);
      return;
    }
    if (request.url === '/api' || request.url?.startsWith('/api/') || request.url === '/hubs' || request.url?.startsWith('/hubs/')) {
      response.setHeader('Content-Type', 'text/plain');
      response.end(`${request.url.startsWith('/api') ? 'api' : 'hub'}-${networkValue}`);
      return;
    }
    if (request.url?.startsWith('/lazy.js')) {
      response.setHeader('Content-Type', 'text/javascript');
      response.end(failPrecache ? 'corrupt-lazy' : lazy[release]);
      return;
    }
    if (request.url === '/css/app.css') {
      response.setHeader('Content-Type', 'text/css');
      response.end(css[release]);
      return;
    }
    if (request.url === '/orphan.html') {
      response.setHeader('Content-Type', 'text/html');
      response.end('<!doctype html><html><body><div id="release">old-orphan</div></body></html>');
      return;
    }
    response.setHeader('Content-Type', 'text/html');
    response.end(html(release));
  });
  const crossServer = createServer((_request, response) => {
    crossRequests += 1;
    response.setHeader('Cache-Control', 'no-store');
    response.end(`cross-${networkValue}`);
  });
  await Promise.all([
    new Promise<void>(resolve => server.listen(0, '127.0.0.1', resolve)),
    new Promise<void>(resolve => crossServer.listen(0, '127.0.0.1', resolve)),
  ]);
  const origin = `http://127.0.0.1:${(server.address() as AddressInfo).port}`;
  const crossOrigin = `http://127.0.0.1:${(crossServer.address() as AddressInfo).port}`;
  const context = await browser.newContext();
  let page = await context.newPage();
  try {
    await page.goto(origin);
    await page.evaluate(() => navigator.serviceWorker.ready);
    await page.reload();
    await expect(page.locator('#release')).toHaveText('old');
    await page.locator('.listing-card').hover();
    await expect.poll(() => page.locator('.listing-card').evaluate(element => getComputedStyle(element).transform)).not.toBe('none');

    release = 'new';
    await page.evaluate(() => navigator.serviceWorker.getRegistration().then(registration => registration?.update()));
    await expect.poll(() => page.evaluate(() => navigator.serviceWorker.getRegistration().then(registration => registration?.waiting?.state))).toBe('installed');
    await expect(page.locator('#release')).toHaveText('old');
    expect(await page.evaluate(() => fetch('/lazy.js').then(response => response.text()))).toBe('old-lazy');
    await page.close();
    page = await context.newPage();
    await page.goto(origin);
    await page.evaluate(() => navigator.serviceWorker.ready);
    await expect(page.locator('#release')).toHaveText('new', { timeout: 12_000 });
    await expect.poll(() => page.locator('.listing-card').evaluate(element => getComputedStyle(element).transform)).toBe('none');
    await expect.poll(() => page.evaluate(async () => (await caches.keys()).sort())).toEqual(['hc-active-release-v2', 'hc-client-release-map-v2', 'hc-managed-protocol-v2', 'hc-static-new', 'hc-static-v1']);
    await page.evaluate(async () => {
      await caches.open('hc-static-future-precache');
      (await navigator.serviceWorker.getRegistration())?.active?.postMessage({ type: 'hc-client-upgraded', cache: 'hc-static-new' });
    });
    await expect.poll(() => page.evaluate(async () => (await caches.keys()).includes('hc-static-future-precache'))).toBe(true);
    await page.evaluate(() => caches.delete('hc-static-future-precache'));
    networkValue = 'two';
    expect(await page.evaluate(() => fetch('/api').then(response => response.text()))).toBe('api-two');
    expect(await page.evaluate(() => fetch('/api/probe').then(response => response.text()))).toBe('api-two');
    expect(await page.evaluate(() => fetch('/hubs').then(response => response.text()))).toBe('hub-two');
    expect(await page.evaluate(() => fetch('/hubs/probe').then(response => response.text()))).toBe('hub-two');
    await page.evaluate(async url => {
      const response = await fetch(url, { mode: 'no-cors' });
      await (await caches.open('hc-static-new')).put(url, response.clone());
    }, crossOrigin);
    expect(crossRequests).toBe(1);
    await page.evaluate(url => fetch(url, { mode: 'no-cors' }), crossOrigin);
    expect(crossRequests).toBe(2);

    const secondPage = await context.newPage();
    await secondPage.goto(origin);
    await expect(secondPage.locator('#release')).toHaveText('new');
    await page.locator('#before-dialog').focus();
    await page.evaluate(() => (window as unknown as { hc: { dialogOpen: (id: string) => void } }).hc.dialogOpen('vote-dialog'));
    await expect(page.locator('body')).toHaveClass(/dialog-open/);
    await expect(page.getByRole('button', { name: 'Vote' })).toBeFocused();
    await expect.poll(() => page.evaluate(() => (window as unknown as { hc: { activeDialog?: { dialog: HTMLElement } } }).hc.activeDialog?.dialog.id)).toBe('vote-dialog');
    release = 'next';
    await page.evaluate(() => navigator.serviceWorker.getRegistration().then(registration => registration?.update()));
    await expect(secondPage.locator('#release')).toHaveText('next', { timeout: 30_000 });
    await expect(page.locator('#release')).toHaveText('new');
    await expect.poll(() => page.evaluate(() => (window as unknown as { hc: { updateAvailable?: boolean } }).hc.updateAvailable)).toBe(true);
    await expect.poll(() => page.evaluate(async () => (await caches.keys()).sort())).toEqual(['hc-active-release-v2', 'hc-client-release-map-v2', 'hc-managed-protocol-v2', 'hc-static-new', 'hc-static-next', 'hc-static-v1']);
    const cdp = await context.newCDPSession(page);
    await cdp.send('ServiceWorker.enable');
    await cdp.send('ServiceWorker.stopAllWorkers');
    await cdp.detach();
    expect(await page.evaluate(() => fetch('/lazy.js').then(response => response.text()))).toBe('new-lazy');
    await page.evaluate(() => caches.open('hc-static-new').then(cache => cache.delete('/lazy.js')));
    const missingMappedAsset = await page.evaluate(() => fetch('/lazy.js').then(async response => ({ status: response.status, body: await response.text() })));
    expect(missingMappedAsset).toEqual({ status: 503, body: 'Release asset unavailable; reload required.' });
    await secondPage.close();
    await page.evaluate(() => {
      const hc = (window as unknown as { hc: { dialogClose: () => void; dialogOpen: (id: string) => void } }).hc;
      hc.dialogClose();
      hc.dialogOpen('vote-dialog');
    });
    await expect(page.locator('body')).not.toHaveClass(/dialog-open/);
    await expect(page.locator('#release')).toHaveText('next');
    await expect.poll(() => page.evaluate(async () => (await caches.keys()).sort())).toEqual(['hc-active-release-v2', 'hc-client-release-map-v2', 'hc-managed-protocol-v2', 'hc-static-new', 'hc-static-next', 'hc-static-v1']);

    await context.close();

    release = 'old';
    const precacheContext = await browser.newContext();
    let precachePage = await precacheContext.newPage();
    await precachePage.goto(origin);
    await precachePage.evaluate(() => navigator.serviceWorker.ready);
    await precachePage.reload();
    release = 'new';
    failPrecache = true;
    await precachePage.evaluate(() => navigator.serviceWorker.getRegistration().then(registration => registration?.update()).catch(() => undefined));
    await precachePage.waitForTimeout(1000);
    await expect(precachePage.locator('#release')).toHaveText('old');
    expect(await precachePage.evaluate(() => fetch('/lazy.js').then(response => response.text()))).toBe('old-lazy');
    failPrecache = false;
    await precachePage.evaluate(() => navigator.serviceWorker.getRegistration().then(registration => registration?.update()).catch(() => undefined));
    await expect.poll(() => precachePage.evaluate(() => navigator.serviceWorker.getRegistration().then(registration => registration?.waiting?.state))).toBe('installed');
    await precachePage.close();
    precachePage = await precacheContext.newPage();
    await precachePage.goto(origin);
    await expect(precachePage.locator('#release')).toHaveText('new', { timeout: 12_000 });
    await precacheContext.close();


    release = 'old';
    const deleteContext = await browser.newContext();
    let deletePage = await deleteContext.newPage();
    await deletePage.goto(origin);
    await deletePage.evaluate(() => navigator.serviceWorker.ready);
    await deletePage.reload();
    release = 'new';
    await deletePage.evaluate(() => navigator.serviceWorker.getRegistration().then(registration => registration?.update()));
    await expect.poll(() => deletePage.evaluate(() => navigator.serviceWorker.getRegistration().then(registration => registration?.waiting?.state))).toBe('installed');
    await deletePage.close();
    deletePage = await deleteContext.newPage();
    await deletePage.goto(origin);
    await expect(deletePage.locator('#release')).toHaveText('new', { timeout: 12_000 });
    let failedTransitionNavigations = 0;
    deletePage.on('framenavigated', frame => { if (frame === deletePage.mainFrame()) failedTransitionNavigations += 1; });
    await deletePage.evaluate(() => {
      const blocker = document.createElement('div'); blocker.id = 'draft-blocker'; blocker.className = 'filter-backdrop open';
      blocker.innerHTML = '<input id="unsaved-feedback" type="text" value="">';
      document.body.append(blocker);
      (document.querySelector('#unsaved-feedback') as HTMLInputElement).value = 'not sent';
    });
    release = 'next';
    failMapWrite = true;
    await deletePage.evaluate(() => navigator.serviceWorker.getRegistration().then(registration => registration?.update()));
    await expect.poll(() => deletePage.evaluate(() => new Promise<string | null>(resolve => {
      const timeout = setTimeout(() => resolve(null), 500);
      navigator.serviceWorker.addEventListener('message', function handler(event) {
        if (event.data?.type !== 'hc-worker-check') return;
        clearTimeout(timeout); navigator.serviceWorker.removeEventListener('message', handler); resolve(event.data.cache);
      });
      void navigator.serviceWorker.getRegistration().then(registration => {
        registration?.waiting?.postMessage({ type: 'hc-evaluate-activation' });
        registration?.active?.postMessage({ type: 'hc-check-update' });
      });
    })), { timeout: 30_000 }).toBe('hc-static-next');
    await expect(deletePage.locator('#release')).toHaveText('new');
    await expect(deletePage.locator('#unsaved-feedback')).toHaveValue('not sent');
    await deletePage.evaluate(() => document.querySelector('#draft-blocker')?.remove());
    await expect(deletePage.getByRole('heading', { name: 'Update paused safely' })).toBeVisible({ timeout: 12_000 });
    await deletePage.waitForTimeout(6_000);
    expect(failedTransitionNavigations).toBe(1);
    expect(await deletePage.locator('script[src], link[rel="stylesheet"]').count()).toBe(0);
    const deleteCdp = await deleteContext.newCDPSession(deletePage);
    await deleteCdp.send('ServiceWorker.enable');
    await deleteCdp.send('ServiceWorker.stopAllWorkers');
    await deleteCdp.detach();
    await expect(deletePage.getByRole('heading', { name: 'Update paused safely' })).toBeVisible();
    failMapWrite = false;
    await deletePage.getByRole('button', { name: 'Retry update' }).click();
    await expect(deletePage.locator('#release')).toHaveText('next', { timeout: 12_000 });
    expect(await deletePage.evaluate(() => fetch('/lazy.js').then(response => response.text()))).toBe('next-lazy');
    release = 'final';
    await deletePage.evaluate(() => navigator.serviceWorker.getRegistration().then(registration => registration?.update()));
    await expect.poll(() => deletePage.evaluate(() => new Promise<string | null>(resolve => {
      const timeout = setTimeout(() => resolve(null), 500);
      navigator.serviceWorker.addEventListener('message', function handler(event) {
        if (event.data?.type !== 'hc-worker-check') return;
        clearTimeout(timeout); navigator.serviceWorker.removeEventListener('message', handler); resolve(event.data.cache);
      });
      void navigator.serviceWorker.getRegistration().then(registration => registration?.active?.postMessage({ type: 'hc-check-update' }));
    })), { timeout: 30_000 }).toBe('hc-static-final');
    await expect(deletePage.locator('#release')).toHaveText('final', { timeout: 30_000 });
    expect(await deletePage.evaluate(() => fetch('/lazy.js').then(response => response.text()))).toBe('final-lazy');
    await deleteContext.close();


    release = 'new';
    const orphanContext = await browser.newContext();
    const orphanPage = await orphanContext.newPage();
    await orphanPage.goto(`${origin}/orphan.html`);
    await orphanPage.evaluate(async () => {
      await (await caches.open('hc-static-v1')).put('/lazy.js', new Response('old-lazy'));
      await navigator.serviceWorker.register('/service-worker.js?orphan=1', { updateViaCache: 'none' });
      await navigator.serviceWorker.ready;
    });
    await expect(orphanPage.locator('#release')).toHaveText('old-orphan');
    const orphanMiss = await orphanPage.evaluate(() => fetch('/lazy.js?cache-bust=orphan').then(async response => ({ status: response.status, body: await response.text() })));
    expect(orphanMiss).toEqual({ status: 503, body: 'Release identity unavailable; reload required.' });
    const aliasMisses = await orphanPage.evaluate(() => Promise.all(['/%6cazy.js', '/lazy%2Ejs', '/LAZY.JS', '/lazy.js/'].map(path => fetch(path).then(async response => ({ status: response.status, body: await response.text() })))));
    expect(aliasMisses).toEqual(Array(4).fill({ status: 503, body: 'Release identity unavailable; reload required.' }));
    const freshPage = await orphanContext.newPage();
    await freshPage.goto(origin);
    await freshPage.evaluate(() => navigator.serviceWorker.ready);
    if (!await freshPage.evaluate(() => !!navigator.serviceWorker.controller)) await freshPage.reload();
    await expect.poll(() => freshPage.evaluate(() => !!navigator.serviceWorker.controller)).toBe(true);
    await expect(freshPage.locator('#release')).toHaveText('new');
    await expect.poll(() => freshPage.evaluate(async () => (await caches.keys()).includes('hc-managed-protocol-v2')), { timeout: 30_000 }).toBe(true);
    release = 'next';
    await freshPage.evaluate(() => navigator.serviceWorker.getRegistration().then(registration => registration?.update()));
    await expect.poll(() => freshPage.evaluate(() => navigator.serviceWorker.getRegistration().then(registration => registration?.waiting?.state))).toBe('installed');
    await expect(orphanPage.locator('#release')).toHaveText('old-orphan');
    const blockedOrphan = await orphanPage.evaluate(() => fetch('/lazy.js?cache-bust=later').then(async response => ({ status: response.status, body: await response.text() })));
    expect(blockedOrphan).toEqual({ status: 503, body: 'Release identity unavailable; reload required.' });
    await orphanPage.close();
    await freshPage.evaluate(() => navigator.serviceWorker.getRegistration().then(registration => registration?.waiting?.postMessage({ type: 'hc-evaluate-activation' })));
    await expect(freshPage.locator('#release')).toHaveText('next', { timeout: 30_000 });
    expect(await freshPage.evaluate(() => fetch('/lazy.js').then(response => response.text()))).toBe('next-lazy');
    await orphanContext.close();


    release = 'final';
    const lossContext = await browser.newContext();
    const lossPage = await lossContext.newPage();
    await lossPage.goto(origin);
    await lossPage.evaluate(() => navigator.serviceWorker.ready);
    await lossPage.reload();
    await expect(lossPage.locator('#release')).toHaveText('final');
    await lossPage.evaluate(async () => {
      const registration = await navigator.serviceWorker.getRegistration();
      const cacheName = await new Promise<string | null>(resolve => {
        const timeout = setTimeout(() => resolve(null), 1000);
        navigator.serviceWorker.addEventListener('message', function handler(event) {
          if (event.data?.type !== 'hc-cache-identity') return;
          clearTimeout(timeout); navigator.serviceWorker.removeEventListener('message', handler); resolve(event.data.cache);
        });
        registration?.active?.postMessage({ type: 'hc-cache-identity' });
      });
      if (!cacheName) throw new Error('controlled page lacked durable release identity');
      await (await caches.open(cacheName)).delete('index.html');
    });
    release = 'next';
    await lossPage.reload();
    await expect(lossPage.getByRole('heading', { name: 'Update paused safely' })).toBeVisible();
    await expect(lossPage.locator('script[src], link[rel="stylesheet"]')).toHaveCount(0);
    await expect(lossPage.evaluate(() => fetch('/lazy.js'))).rejects.toThrow('Failed to fetch');
    await lossContext.close();
  } finally {
    if (!page.isClosed()) await context.close();
    server.closeAllConnections();
    crossServer.closeAllConnections();
    await Promise.all([
      new Promise<void>((resolve, reject) => server.close(error => error ? reject(error) : resolve())),
      new Promise<void>((resolve, reject) => crossServer.close(error => error ? reject(error) : resolve())),
    ]);
  }
});
