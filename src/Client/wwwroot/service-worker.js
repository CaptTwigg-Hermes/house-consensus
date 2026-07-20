self.addEventListener('install', event => event.waitUntil(caches.open('hc-shell-v1').then(cache => cache.addAll(['/', '/css/app.css', '/js/app.js', '/manifest.webmanifest']))));
self.addEventListener('activate', event => event.waitUntil(self.clients.claim()));
self.addEventListener('fetch', event => {
  if (event.request.method !== 'GET' || new URL(event.request.url).pathname.startsWith('/api/') || new URL(event.request.url).pathname.startsWith('/hubs/')) return;
  event.respondWith(fetch(event.request).then(response => { const copy = response.clone(); caches.open('hc-runtime-v1').then(cache => cache.put(event.request, copy)); return response; }).catch(() => caches.match(event.request)));
});
