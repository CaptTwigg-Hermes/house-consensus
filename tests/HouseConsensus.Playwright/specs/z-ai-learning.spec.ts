import { test, expect } from '../fixtures/test.js';
import { castGuidedVote } from '../helpers/household.js';

test('owner generates an AI learning proposal with the configured LAN model', async ({ page }) => {
  test.setTimeout(180_000);
  await page.goto('/browse');
  await page.getByTestId('listing-card').first().locator('.card-main').click();
  await castGuidedVote(page, 'Dislike', 'Avoid homes requiring extensive renovation');
  await page.goto('/owner/feedback');
  await page.getByRole('button', { name: /generate/i }).click();
  await expect(page.locator('.learning-card').first()).toBeVisible({ timeout: 150_000 });
  await expect(page.locator('.error')).toHaveCount(0);
});
