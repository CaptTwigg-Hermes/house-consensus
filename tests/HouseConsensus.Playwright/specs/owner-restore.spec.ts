import { test, expect } from '../fixtures/test.js';

test('owner restores an unresolved AI rejection to Browse', async ({ page }) => {
  const reset = await page.request.post('/api/e2e/reset-review-listing', { headers: { 'X-House-Consensus-CSRF': '1' } });
  expect(reset.ok()).toBeTruthy();
  const response = await page.request.get('/api/review');
  expect(response.ok()).toBeTruthy();
  const queue = await response.json() as Array<{ id: string; state: string | number }>;
  expect(queue.length).toBeGreaterThan(0);
  const listingId = queue[0]!.id;

  await page.goto('/owner/review');
  const rejected = page.locator(`[data-testid="listing-card"][data-listing-id="${listingId}"]`);
  await expect(rejected).toBeVisible();
  await expect(rejected.locator('.score-chip')).not.toContainText('??');
  await expect(rejected.getByTestId('ai-evidence')).toContainText('Single-family layout');
  await expect(rejected.getByTestId('ai-evidence').locator('pre')).toHaveCount(0);
  await expect(rejected.locator('.property-facts')).toContainText('164');
  await expect(rejected.getByTestId('commute-table')).toContainText('Høje Taastrup St.');
  await expect(rejected.getByRole('link', { name: /open original listing|åbn original annonce/i })).toHaveAttribute('target', '_blank');
  await rejected.getByTestId('restore-listing').click();
  await expect(rejected).toBeHidden();
  await page.goto('/browse');
  await expect(page.locator(`[data-testid="listing-card"][data-listing-id="${listingId}"]`)).toBeVisible();
});
