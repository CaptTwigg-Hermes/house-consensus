self.importScripts('./service-worker-assets.js?hc=upgrade-v2');
const CACHE_PREFIX = 'hc-static-';
const CACHE = `${CACHE_PREFIX}${self.assetsManifest.version}`;
const LEGACY_CACHE = 'hc-static-v1';
const MANAGED_PROTOCOL = 'hc-managed-protocol-v2';
const ACTIVE_RELEASE = 'hc-active-release-v2';
const CLIENT_MAP = 'hc-client-release-map-v2';
const ASSETS = self.assetsManifest.assets
  .filter(asset => !asset.url.endsWith('.pdb'))
  .map(asset => new Request(asset.url, { integrity: asset.hash, cache: 'no-cache' }));
const mapRequest = clientId => new Request(new URL(`/__hc-client-release/${clientId}`, self.location.origin));

const recordClientRelease = async (clientId, cacheName) => {
  if (!clientId || !cacheName?.startsWith(CACHE_PREFIX)) return;
  await (await caches.open(CLIENT_MAP)).put(mapRequest(clientId), new Response(cacheName));
};

const mappedClientRelease = async clientId => {
  if (!clientId) return null;
  const response = await (await caches.open(CLIENT_MAP)).match(mapRequest(clientId));
  return response?.text() || null;
};

const activeRelease = async () => (await (await caches.open(ACTIVE_RELEASE)).match('/__hc-active-release'))?.text() || null;
const recordActiveRelease = async () => (await caches.open(ACTIVE_RELEASE)).put('/__hc-active-release', new Response(CACHE));
const recoveryResponse = () => new Response('<!doctype html><html lang="en"><head><meta name="viewport" content="width=device-width"><title>Updating House Consensus</title></head><body><main><h1>Update paused safely</h1><p>No mixed release was loaded. Retry when ready.</p><form method="get"><button id="hc-retry" type="submit">Retry update</button></form></main></body></html>', { status: 503, headers: { 'Content-Type': 'text/html; charset=utf-8', 'Cache-Control': 'no-store', 'Content-Security-Policy': "default-src 'none'; form-action 'self'; base-uri 'none'" } });

const activateWhenEveryClientMapped = async () => {
  const windows = await self.clients.matchAll({ type: 'window', includeUncontrolled: true });
  const mappings = await Promise.all(windows.map(client => mappedClientRelease(client.id)));
  if (mappings.every(Boolean)) await self.skipWaiting();
};

self.addEventListener('install', event => {
  event.waitUntil((async () => {
    await (await caches.open(CACHE)).addAll(ASSETS);
    const keys = await caches.keys();
    const managedUpgrade = keys.includes(MANAGED_PROTOCOL);
    const legacyUpgrade = keys.includes(LEGACY_CACHE) && !managedUpgrade;
    if (!legacyUpgrade && !managedUpgrade) {
      await caches.open(MANAGED_PROTOCOL);
      await self.skipWaiting();
    }
    if (managedUpgrade) await activateWhenEveryClientMapped();
  })());
});

self.addEventListener('message', event => {
  if (event.data?.type === 'hc-evaluate-activation') {
    event.waitUntil(activateWhenEveryClientMapped());
  } else if (event.data?.type === 'hc-cache-identity' && event.source?.id) {
    event.waitUntil(mappedClientRelease(event.source.id).then(cache => {
      event.source?.postMessage({ type: 'hc-cache-identity', cache });
    }));
  } else if (event.data?.type === 'hc-check-update') {
    event.source?.postMessage({ type: 'hc-worker-check', cache: CACHE });
  } else if (event.data?.type === 'hc-client-upgraded' && event.source?.id && event.data.cache === CACHE) {
    event.waitUntil((async () => {
      await caches.open(MANAGED_PROTOCOL);
    })());
  }
});

self.addEventListener('activate', event => {
  event.waitUntil((async () => {
    const keys = await caches.keys();
    const legacyMigration = keys.includes(LEGACY_CACHE) && !keys.includes(MANAGED_PROTOCOL);
    await self.clients.claim();
    await recordActiveRelease();
  })());
});

self.addEventListener('fetch', event => {
  const url = new URL(event.request.url);
  if (url.origin !== self.location.origin || event.request.method !== 'GET' || url.pathname === '/api' || url.pathname.startsWith('/api/') || url.pathname === '/hubs' || url.pathname.startsWith('/hubs/')) return;
  event.respondWith((async () => {
    if (event.request.mode === 'navigate') {
      const cache = await caches.open(CACHE);
      const response = await cache.match('index.html') || await cache.match(event.request);
      if (!response?.ok) return recoveryResponse();
      if (!event.resultingClientId || !await recordClientRelease(event.resultingClientId, CACHE).then(() => true).catch(() => false))
        return recoveryResponse();
      return response;
    }
    const mappedCache = await mappedClientRelease(event.clientId).catch(() => null);
    if (mappedCache)
      return await (await caches.open(mappedCache)).match(event.request) || new Response('Release asset unavailable; reload required.', { status: 503 });
    return new Response('Release identity unavailable; reload required.', { status: 503 });
  })());
});
