# House Consensus Quick Add (Browser Extension)

An unpacked Chrome/Brave extension that adds a "🏠 Add to House Consensus"
button on Boligsiden listing pages (`boligsiden.dk/adresse/...`) and wires
the pinned toolbar icon to do the same. Clicking either sends the current
listing straight into House Consensus via `/api/listings/preview` and
`/api/listings`, reusing your existing browser session cookie for
`house-consensus.jahn-software.com`.

## How it works

- The background service worker performs the cross-origin fetches. Because
  it runs with `host_permissions` for both `boligsiden.dk` and
  `house-consensus.jahn-software.com`, these requests are not subject to
  the target site's CORS policy.
- Requests are sent with `credentials: "include"`, so you must already be
  signed in to House Consensus in the same browser profile.
- The server's same-origin CSRF guard requires the
  `X-House-Consensus-CSRF: 1` header on all unsafe `/api/*` requests; the
  extension sets it automatically.
- Boligsiden is a single-page app, so navigating between listings doesn't
  trigger a full page (re)load. A `chrome.webNavigation.onHistoryStateUpdated`
  listener re-injects the content script on every in-app navigation so the
  floating button always matches the currently viewed address.

## Install (unpacked, developer mode)

1. Open `chrome://extensions` (or `brave://extensions`).
2. Enable **Developer mode** (top-right toggle).
3. Click **Load unpacked** and select this `browser-extension` folder.
4. Make sure you're signed in to `https://house-consensus.jahn-software.com`
   in the same browser.
5. Open a Boligsiden listing (URL contains `/adresse/`) and either click the
   floating button or the pinned toolbar icon.
