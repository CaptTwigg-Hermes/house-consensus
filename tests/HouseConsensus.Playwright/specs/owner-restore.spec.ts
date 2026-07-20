import { test, expect } from '../fixtures/test.js';
import { identity, requestMagicLink } from '../helpers/household.js';

test('owner restores a rejected listing to the active browse queue', async ({ page, mailpit }, testInfo) => {
  await requestMagicLink(page, mailpit, identity(testInfo, 'owner'));
  await page.goto('/browse');
  const listing = page.getByTestId('listing-card').first();
  const listingId = await listing.getAttribute('data-listing-id');
  expect(listingId, 'listing cards expose data-listing-id').toBeTruthy();
  await listing.getByTestId('vote-reject').click();
  await expect(listing).toBeHidden();

  await page.goto('/rejected');
  const rejected = page.getByTestId('listing-card').filter({ has: page.locator(`[data-listing-id="${listingId}"]`) });
  const exactRejected = page.locator(`[data-testid="listing-card"][data-listing-id="${listingId}"]`);
  await expect(exactRejected.or(rejected)).toBeVisible();
  await exactRejected.or(rejected).getByTestId('restore-listing').click();
  await expect(exactRejected.or(rejected)).toBeHidden();

  await page.goto('/browse');
  await expect(page.locator(`[data-testid="listing-card"][data-listing-id="${listingId}"]`)).toBeVisible();
});
