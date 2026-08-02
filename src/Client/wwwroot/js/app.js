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
    if (window.hc.updateActivating) {
      dialog.hidden = true;
      window.hc.queuedDialogId = id;
      return;
    }
    dialog.hidden = false;
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
    window.hc.applyPendingUpdate();
  },
  hasDirtyFormControl: element => {
    if (element instanceof HTMLSelectElement) return [...element.options].some(option => option.selected !== option.defaultSelected);
    if (!(element instanceof HTMLInputElement)) return false;
    if (element.type === 'file') return !!element.files?.length;
    if (['button', 'submit', 'reset', 'image', 'hidden'].includes(element.type)) return false;
    if (['checkbox', 'radio'].includes(element.type)) return element.checked !== element.defaultChecked;
    return element.value !== element.defaultValue;
  },
  hasUnsafeInput: () => document.body.classList.contains('dialog-open') ||
    !!document.querySelector('.filter-backdrop.open, textarea, [contenteditable="true"], [aria-busy="true"]') ||
    [...document.querySelectorAll('input, select')].some(window.hc.hasDirtyFormControl),
  applyPendingUpdate: () => {
    if (!window.hc.updateAvailable || window.hc.updateActivating) return;
    if (window.hc.hasUnsafeInput()) return;
    const key = `hc-update-attempt:${window.hc.updateTarget || 'unknown'}`;
    const attempts = Number(sessionStorage.getItem(key) || 0);
    if (attempts >= 2) { window.hc.updateAvailable = false; return; }
    sessionStorage.setItem(key, String(attempts + 1));
    window.hc.updateActivating = true;
    location.reload();
    window.hc.updateActivationTimer = setTimeout(() => window.hc.resumeQueuedDialog(), 5000);
  },
  resumeQueuedDialog: () => {
    window.hc.updateActivating = false;
    clearTimeout(window.hc.updateActivationTimer);
    const id = window.hc.queuedDialogId;
    window.hc.queuedDialogId = null;
    if (id) window.hc.dialogOpen(id);
  }
};
document.documentElement.lang = window.hc.browserLanguage().startsWith('da') ? 'da' : 'en';
const retrySafeUpdate = () => { if (window.hc.updateAvailable && !window.hc.hasUnsafeInput()) window.hc.applyPendingUpdate(); };
document.addEventListener('input', retrySafeUpdate);
document.addEventListener('change', retrySafeUpdate);
new MutationObserver(retrySafeUpdate).observe(document.body, { childList: true, subtree: true, attributes: true, attributeFilter: ['class', 'aria-busy'] });
if ('serviceWorker' in navigator) {
  let pendingWorker = null;
  let loadedWorkerCache = null;
  const prepareWaitingWorker = worker => {
    if (!worker) return;
    pendingWorker = worker;
    worker.postMessage({ type: 'hc-evaluate-activation' });
    navigator.serviceWorker.controller?.postMessage({ type: 'hc-cache-identity' });
  };
  const applyUpdateFrom = (worker, targetCache) => {
    if (!worker || worker.state !== 'activated') return;
    pendingWorker = null;
    window.hc.updateTarget = targetCache;
    window.hc.updateAvailable = true;
    window.hc.applyPendingUpdate();
  };
  navigator.serviceWorker.addEventListener('message', event => {
    if (event.data?.type === 'hc-update-waiting') {
      prepareWaitingWorker(event.source);
    } else if (event.data?.type === 'hc-cache-identity') {
      loadedWorkerCache ||= event.data.cache;
      event.source?.postMessage({ type: 'hc-check-update' });
    } else if (event.data?.type === 'hc-worker-check') {
      if (loadedWorkerCache && event.data.cache === loadedWorkerCache) {
        sessionStorage.removeItem(`hc-update-attempt:${loadedWorkerCache}`);
        if (!pendingWorker) event.source?.postMessage({ type: 'hc-client-upgraded', cache: loadedWorkerCache });
      } else if (loadedWorkerCache)
        applyUpdateFrom(event.source, event.data.cache);
    }
  });
  const recheckUpdate = () => {
    void navigator.serviceWorker.getRegistration().then(registration => {
      if (!registration) return;
      if (!loadedWorkerCache) navigator.serviceWorker.controller?.postMessage({ type: 'hc-cache-identity' });
      prepareWaitingWorker(registration.waiting);
      registration.active?.postMessage({ type: 'hc-check-update' });
    });
  };
  navigator.serviceWorker.register('service-worker.js', { updateViaCache: 'none' }).then(recheckUpdate);
  navigator.serviceWorker.addEventListener('controllerchange', recheckUpdate);
  window.addEventListener('focus', recheckUpdate);
  document.addEventListener('visibilitychange', () => { if (!document.hidden) recheckUpdate(); });
  setInterval(recheckUpdate, 5000);
}


document.addEventListener('keydown', event => {
  if (event.key !== 'Tab' || !window.hc.activeDialog) return;
  const nodes = [...window.hc.activeDialog.dialog.querySelectorAll('button:not([disabled]), [href], input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])')].filter(x => x.offsetParent !== null);
  if (!nodes.length) return;
  const first = nodes[0], last = nodes[nodes.length - 1];
  if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last.focus(); }
  else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first.focus(); }
});
