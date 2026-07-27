self.importScripts('./service-worker-assets.js');
const CACHE = `hc-static-${self.assetsManifest.version}`;
const IS_UPDATE = Boolean(self.registration.active);
self.addEventListener('install', event => {
  event.waitUntil(
    caches.open(CACHE)
      .then(cache => cache.addAll(self.assetsManifest.assets
        .filter(a => !a.url.endsWith('.pdb'))
        .map(a => new Request(a.url, { integrity: a.hash, cache: 'no-cache' }))))
      .then(() => self.skipWaiting())
  );
});
self.addEventListener('activate', event => {
  event.waitUntil(
    caches.keys()
      .then(keys => Promise.all(keys.filter(k => k.startsWith('hc-static-') && k !== CACHE).map(k => caches.delete(k))))
      .then(() => self.clients.claim())
      .then(() => IS_UPDATE ? self.clients.matchAll({ type: 'window' }) : [])
      .then(windows => Promise.all(windows.map(client => client.navigate(client.url))))
  );
});
self.addEventListener('fetch', event => {
  const path = new URL(event.request.url).pathname;
  if (event.request.method !== 'GET' || path.startsWith('/api/') || path.startsWith('/hubs/')) return;
  event.respondWith(caches.open(CACHE).then(cache => cache.match(event.request)).then(cached => cached || fetch(event.request)));
});
