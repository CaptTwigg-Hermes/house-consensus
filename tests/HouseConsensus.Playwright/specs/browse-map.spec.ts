import { test, expect } from '../fixtures/test.js';
import { identity, requestMagicLink } from '../helpers/household.js';

test('Browse applies price filters and renders the filtered homes on the map', async ({ page, mailpit }, testInfo) => {
  await requestMagicLink(page, mailpit, identity(testInfo, 'owner'));
  await page.goto('/browse');
  await expect(page.getByRole('heading', { name: /browse|gennemse/i })).toBeVisible();
  await expect(page.getByTestId('listing-card').first()).toBeVisible();
  await page.getByTestId('filter-price-max').fill('5000000');
  await page.getByTestId('filter-apply').click();
  const cards = page.getByTestId('listing-card');
  await expect(cards).toHaveCount(1);
  expect(Number(await cards.first().getAttribute('data-price'))).toBeLessThanOrEqual(5_000_000);
  await page.getByRole('button', { name: /map|kort/i }).click();
  await expect(page.getByTestId('browse-map')).toBeVisible();
  await expect(page.getByTestId('browse-map')).toHaveClass(/leaflet-container/);
  await page.getByTestId('filter-clear').click();
  await expect(page.getByTestId('filter-price-max')).toHaveValue('');
});
