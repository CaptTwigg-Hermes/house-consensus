import { test, expect } from '../fixtures/test.js';
import { identity, requestMagicLink } from '../helpers/household.js';

test('owner generates an AI learning proposal with the configured LAN model', async ({ page, mailpit }, testInfo) => {
  test.setTimeout(180_000);
  await requestMagicLink(page, mailpit, identity(testInfo, 'owner'));
  await page.goto('/browse');
  await page.getByTestId('listing-card').first().locator('.card-main').click();
  await page.getByTestId('vote-reject').click();
  await page.getByTestId('vote-note').fill('Avoid homes requiring extensive renovation');
  await page.getByRole('button', { name: /save vote|gem stemme/i }).click();
  await page.goto('/owner/feedback');
  await page.getByRole('button', { name: /generate/i }).click();
  await expect(page.locator('.learning-card').first()).toBeVisible({ timeout: 150_000 });
  await expect(page.locator('.error')).toHaveCount(0);
});
