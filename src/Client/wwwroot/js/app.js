window.hc = {
  browserLanguage: () => localStorage.getItem('hc.culture') || navigator.language || 'en',
  navigate: path => { window.location.assign(path); },
  setCulture: language => { localStorage.setItem('hc.culture', language); document.documentElement.lang = language; location.reload(); },
  maps: {},
  renderMap: function (id, listings) {
    if (!window.L) return;
    if (this.maps[id]) { this.maps[id].remove(); delete this.maps[id]; }
    const map = L.map(id, { scrollWheelZoom: true }).setView([55.68, 12.2], 9);
    this.maps[id] = map;
    L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', { maxZoom: 19, attribution: '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a>' }).addTo(map);
    const group = L.markerClusterGroup ? L.markerClusterGroup({ showCoverageOnHover: false }) : L.layerGroup();
    const bounds = [];
    for (const listing of listings) {
      const point = [Number(listing.latitude), Number(listing.longitude)];
      if (!point.every(Number.isFinite)) continue;
      const link = document.createElement('a'); link.href = listing.url; link.className = 'map-popup rich';
      const image = document.createElement('img'); image.src = listing.image; image.alt = ''; image.loading = 'lazy'; image.onerror = () => image.remove(); link.appendChild(image);
      const body = document.createElement('span');
      const title = document.createElement('strong'); title.textContent = listing.title; body.appendChild(title);
      const meta = document.createElement('small');
      const price = listing.price == null ? '' : Number(listing.price).toLocaleString(document.documentElement.lang, { maximumFractionDigits: 0 }) + ' kr.';
      meta.textContent = [price, Math.round(listing.score) + '/100'].filter(Boolean).join(' · '); body.appendChild(meta); link.appendChild(body);
      group.addLayer(L.marker(point, { title: listing.title }).bindPopup(link, { minWidth: 220 })); bounds.push(point);
    }
    group.addTo(map); if (bounds.length) map.fitBounds(bounds, { padding: [30, 30], maxZoom: 14 });
    setTimeout(() => map.invalidateSize(), 50);
  }
,
  saveState: (key, value) => localStorage.setItem(key, JSON.stringify(value)),
  loadState: key => { try { const value = localStorage.getItem(key); return value ? JSON.parse(value) : null; } catch { return null; } }
};
document.documentElement.lang = window.hc.browserLanguage().startsWith('da') ? 'da' : 'en';
if ('serviceWorker' in navigator) navigator.serviceWorker.register('service-worker.js', { updateViaCache: 'none' });
