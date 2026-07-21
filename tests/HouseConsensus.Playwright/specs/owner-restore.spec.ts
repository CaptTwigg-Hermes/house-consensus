import { test, expect } from '../fixtures/test.js';
import { identity, requestMagicLink } from '../helpers/household.js';

test('owner restores a rejected listing to the active browse queue', async ({ page, mailpit }, testInfo) => {
  await requestMagicLink(page, mailpit, identity(testInfo, 'owner'));
  await page.goto('/browse');
  const active = page.getByTestId('listing-card').first();
  await expect(active).toBeVisible();
  const listingId = await active.getAttribute('data-listing-id');
  expect(listingId).toBeTruthy();
  const reject = await page.request.post(`/api/review/${listingId}/override`, {
    data: { action: 1, reason: 'Playwright restore setup' },
    headers: { 'X-House-Consensus-CSRF': '1' }
  });
  expect(reject.ok()).toBeTruthy();
  await page.goto('/owner/review');
  const rejected = page.locator(`[data-testid="listing-card"][data-listing-id="${listingId}"]`);
  await expect(rejected).toBeVisible();
  await rejected.getByTestId('restore-listing').click();
  await expect(rejected).toBeHidden();
  await page.goto('/browse');
  await expect(page.locator(`[data-testid="listing-card"][data-listing-id="${listingId}"]`)).toBeVisible();
});
