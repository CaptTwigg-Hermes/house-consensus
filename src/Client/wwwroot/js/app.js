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
      const score = listing.score == null ? '' : Math.round(listing.score) + '/100';
      meta.textContent = [price, score].filter(Boolean).join(' · '); body.appendChild(meta); link.appendChild(body);
      group.addLayer(L.marker(point, { title: listing.title }).bindPopup(link, { minWidth: 220 })); bounds.push(point);
    }
    group.addTo(map); if (bounds.length) map.fitBounds(bounds, { padding: [30, 30], maxZoom: 14 });
    setTimeout(() => map.invalidateSize(), 50);
  }
,
  saveState: (key, value) => localStorage.setItem(key, JSON.stringify(value)),
  loadState: key => { try { const value = localStorage.getItem(key); return value ? JSON.parse(value) : null; } catch { return null; } },
  dialogOpen: id => {
    const dialog = document.getElementById(id);
    if (!dialog) return;
    const inerted = [];
    const hideOutside = root => {
      for (const child of root.children) {
        if (child === dialog) continue;
        if (child.contains(dialog)) { hideOutside(child); continue; }
        if (child.matches('.drawer-backdrop, .sheet-backdrop')) continue;
        inerted.push({ child, inert: child.inert, ariaHidden: child.getAttribute('aria-hidden') });
        child.inert = true; child.setAttribute('aria-hidden', 'true');
      }
    };
    hideOutside(document.body);
    window.hc.activeDialog = { dialog, previous: document.activeElement, inerted };
    document.body.classList.add('dialog-open');
    const focusable = dialog.querySelector('button, [href], input, select, textarea, [tabindex]:not([tabindex="-1"])');
    if (focusable) focusable.focus();
  },
  dialogClose: () => {
    const active = window.hc.activeDialog;
    document.body.classList.remove('dialog-open');
    for (const item of active?.inerted || []) {
      item.child.inert = item.inert;
      if (item.ariaHidden === null) item.child.removeAttribute('aria-hidden'); else item.child.setAttribute('aria-hidden', item.ariaHidden);
    }
    window.hc.activeDialog = null;
    if (active?.previous instanceof HTMLElement) active.previous.focus();
  }
};
document.documentElement.lang = window.hc.browserLanguage().startsWith('da') ? 'da' : 'en';
if ('serviceWorker' in navigator) navigator.serviceWorker.register('service-worker.js');

document.addEventListener('keydown', event => {
  if (event.key !== 'Tab' || !window.hc.activeDialog) return;
  const nodes = [...window.hc.activeDialog.dialog.querySelectorAll('button:not([disabled]), [href], input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])')].filter(x => x.offsetParent !== null);
  if (!nodes.length) return;
  const first = nodes[0], last = nodes[nodes.length - 1];
  if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last.focus(); }
  else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first.focus(); }
});
