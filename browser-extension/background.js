// Background service worker: performs the actual cross-origin requests to
// House Consensus. Runs with host_permissions granted in manifest.json, so
// these fetches are not subject to the target site's CORS policy.

const API_BASE = "https://house-consensus.jahn-software.com";

// Minimal 16x16 green square PNG, inlined so notifications work without a
// separate icon asset file.
const NOTIFICATION_ICON =
  "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAABAAAAAQCAYAAAAf8/9hAAAAJUlEQVR4AWNgGAWjYBSMglEwCkbBKBgFo2AUjIJRMApGwSgAAP8gAT0N2gK/AAAAAElFTkSuQmCC";

async function callApi(path, options) {
  const response = await fetch(`${API_BASE}${path}`, {
    ...options,
    credentials: "include", // send the browser's House Consensus session cookie
    headers: {
      "Content-Type": "application/json",
      "X-House-Consensus-CSRF": "1", // required by the server's same-origin CSRF guard
      ...(options && options.headers ? options.headers : {})
    }
  });

  let body = null;
  const text = await response.text();
  if (text) {
    try { body = JSON.parse(text); } catch { body = text; }
  }

  if (!response.ok) {
    const message =
      (body && body.error) ||
      (typeof body === "string" ? body : null) ||
      `Request failed (${response.status})`;
    if (response.status === 401 || response.status === 403) {
      throw new Error("Not signed in to House Consensus. Open the site in this browser and sign in, then try again.");
    }
    throw new Error(message);
  }

  return body;
}

async function addListing({ url, address, city, askingPrice }) {
  // Ask the server to resolve full details from the Boligsiden URL first.
  let preview = null;
  try {
    preview = await callApi("/api/listings/preview", {
      method: "POST",
      body: JSON.stringify({ url })
    });
  } catch {
    // Preview is best-effort; creation still works with a manual address fallback.
  }

  const result = await callApi("/api/listings", {
    method: "POST",
    body: JSON.stringify({
      url,
      address: (preview && preview.address) || address || url,
      city: (preview && preview.city) || city || null,
      askingPrice: (preview && preview.askingPrice) ?? askingPrice ?? null
    })
  });

  return { result, preview };
}

chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
  if (message?.type !== "house-consensus-add-listing") return undefined;

  addListing(message.payload)
    .then((data) => sendResponse({ ok: true, data }))
    .catch((error) => sendResponse({ ok: false, error: String(error?.message || error) }));

  return true; // keep the message channel open for the async response
});

// Boligsiden is a single-page app: clicking between listings updates the URL
// without a full page (re)load, so Chrome/Brave never re-run content_scripts
// automatically. Re-inject on every in-app navigation to a listing page so
// the floating button always reflects the currently viewed address.
chrome.webNavigation.onHistoryStateUpdated.addListener(
  (details) => {
    if (details.frameId !== 0) return;
    chrome.scripting.executeScript({
      target: { tabId: details.tabId },
      files: ["content.js"]
    }).catch(() => {
      // Tab may have navigated away already; safe to ignore.
    });
    chrome.scripting.insertCSS({
      target: { tabId: details.tabId },
      files: ["content.css"]
    }).catch(() => {});
  },
  { url: [{ hostSuffix: "boligsiden.dk", pathContains: "/adresse/" }] }
);

// Clicking the pinned toolbar icon adds the listing immediately, without
// needing the floating on-page button to be present first.
chrome.action.onClicked.addListener(async (tab) => {
  if (!tab.id || !tab.url || !/\/adresse\//.test(tab.url)) {
    chrome.notifications?.create({
      type: "basic",
      iconUrl: NOTIFICATION_ICON,
      title: "House Consensus Quick Add",
      message: "Open a boligsiden.dk listing page (/adresse/...) first."
    });
    return;
  }
  try {
    const [{ result: address }] = await chrome.scripting.executeScript({
      target: { tabId: tab.id },
      func: () => document.title.split("|")[0].trim()
    });
    const { result } = await addListing({ url: tab.url, address, city: null, askingPrice: null });
    chrome.notifications?.create({
      type: "basic",
      iconUrl: NOTIFICATION_ICON,
      title: "House Consensus Quick Add",
      message: result?.existing ? "Already added." : "Listing added!"
    });
  } catch (error) {
    chrome.notifications?.create({
      type: "basic",
      iconUrl: NOTIFICATION_ICON,
      title: "House Consensus Quick Add",
      message: `Could not add listing: ${String(error?.message || error)}`
    });
  }
});

