// Content script: injects a floating button on boligsiden.dk listing pages
// ("/adresse/...") that sends the current listing to House Consensus.

(function () {
  if (document.getElementById("house-consensus-quick-add")) return;

  const button = document.createElement("button");
  button.id = "house-consensus-quick-add";
  button.type = "button";
  button.textContent = "🏠 Add to House Consensus";
  document.body.appendChild(button);

  function guessAddress() {
    // Fall back to the page title (Boligsiden titles usually start with the address).
    return document.title.split("|")[0].trim();
  }

  function setState(state, text) {
    button.dataset.state = state;
    button.textContent = text;
  }

  button.addEventListener("click", () => {
    setState("busy", "Adding…");
    button.disabled = true;

    chrome.runtime.sendMessage(
      {
        type: "house-consensus-add-listing",
        payload: {
          url: location.href,
          address: guessAddress(),
          city: null,
          askingPrice: null
        }
      },
      (response) => {
        button.disabled = false;
        if (chrome.runtime.lastError) {
          setState("error", "Error — try again");
          console.error("House Consensus Quick Add:", chrome.runtime.lastError.message);
          return;
        }
        if (response && response.ok) {
          const existing = response.data?.result?.existing;
          setState("success", existing ? "✓ Already added" : "✓ Added!");
          setTimeout(() => setState("idle", "🏠 Add to House Consensus"), 4000);
        } else {
          setState("error", "Error — try again");
          console.error("House Consensus Quick Add:", response && response.error);
          alert(`Could not add listing: ${response && response.error}`);
        }
      }
    );
  });
})();
