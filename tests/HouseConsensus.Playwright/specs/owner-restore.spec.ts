import { test, expect } from '../fixtures/test.js';
import { identity, requestMagicLink } from '../helpers/household.js';

test('owner restores an unresolved AI rejection to Browse', async ({ page, mailpit }, testInfo) => {
  await requestMagicLink(page, mailpit, identity(testInfo, 'owner'));
  const response = await page.request.get('/api/review');
  expect(response.ok()).toBeTruthy();
  const queue = await response.json() as Array<{ id: string; state: string | number }>;
  expect(queue.length).toBeGreaterThan(0);
  const listingId = queue[0]!.id;

  await page.goto('/owner/review');
  const rejected = page.locator(`[data-testid="listing-card"][data-listing-id="${listingId}"]`);
  await expect(rejected).toBeVisible();
  await rejected.getByTestId('restore-listing').click();
  await expect(rejected).toBeHidden();
  await page.goto('/browse');
  await expect(page.locator(`[data-testid="listing-card"][data-listing-id="${listingId}"]`)).toBeVisible();
});
