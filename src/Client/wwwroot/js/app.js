window.hc = {
  browserLanguage: () => localStorage.getItem('hc.culture') || navigator.language || 'en',
  setCulture: language => { localStorage.setItem('hc.culture', language); document.documentElement.lang = language; location.reload(); },
  maps: {},
  renderMap: async function (id, listings) {
    if (!window.L) return;
    if (this.maps[id]) { this.maps[id].remove(); delete this.maps[id]; }
    const map = L.map(id, { scrollWheelZoom: false }).setView([56.12, 10.1], 7);
    this.maps[id] = map;
    L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', { maxZoom: 19, attribution: '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a>' }).addTo(map);
    const group = L.markerClusterGroup ? L.markerClusterGroup({ showCoverageOnHover: false }) : L.layerGroup();
    const bounds = [];
    for (const listing of listings) {
      const key = 'hc.geo.' + listing.address + '|' + (listing.city || '');
      let point;
      try { point = JSON.parse(sessionStorage.getItem(key)); } catch { point = null; }
      if (!point) {
        try {
          const query = encodeURIComponent(listing.address + ', ' + (listing.city || '') + ', Denmark');
          const result = await fetch('https://nominatim.openstreetmap.org/search?format=jsonv2&limit=1&countrycodes=dk&q=' + query, { headers: { 'Accept-Language': document.documentElement.lang } }).then(r => r.json());
          if (result.length) { point = [Number(result[0].lat), Number(result[0].lon)]; sessionStorage.setItem(key, JSON.stringify(point)); }
          await new Promise(resolve => setTimeout(resolve, 1100));
        } catch { point = null; }
      }
      if (point) {
        const content = document.createElement('a'); content.href = listing.url; content.className = 'map-popup'; content.textContent = listing.title;
        group.addLayer(L.marker(point, { title: listing.title }).bindPopup(content)); bounds.push(point);
      }
    }
    group.addTo(map); if (bounds.length) map.fitBounds(bounds, { padding: [30, 30], maxZoom: 14 });
    setTimeout(() => map.invalidateSize(), 50);
  }
};
document.documentElement.lang = window.hc.browserLanguage().startsWith('da') ? 'da' : 'en';
if ('serviceWorker' in navigator) navigator.serviceWorker.register('service-worker.js');
