self.importScripts('./service-worker-assets.js');
const CACHE = 'hc-static-v1';
self.addEventListener('install', event => event.waitUntil(caches.open(CACHE).then(cache => cache.addAll(self.assetsManifest.assets.filter(a => !a.url.endsWith('.pdb')).map(a => new Request(a.url, { integrity: a.hash, cache: 'no-cache' }))))));
self.addEventListener('activate', event => event.waitUntil(caches.keys().then(keys => Promise.all(keys.filter(k => k !== CACHE).map(k => caches.delete(k)))).then(() => self.clients.claim())));
self.addEventListener('fetch', event => { if (event.request.method !== 'GET' || new URL(event.request.url).pathname.startsWith('/api/') || new URL(event.request.url).pathname.startsWith('/hubs/')) return; event.respondWith(caches.match(event.request).then(cached => cached || fetch(event.request))); });
