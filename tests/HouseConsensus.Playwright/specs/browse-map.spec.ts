import { test, expect } from '../fixtures/test.js';
import { identity, requestMagicLink } from '../helpers/household.js';

test('Browse combines price/area filters and keeps list and map results in sync', async ({ page, mailpit }, testInfo) => {
  await requestMagicLink(page, mailpit, identity(testInfo, 'owner'));
  await page.goto('/browse');
  await expect(page.getByRole('heading', { name: /browse|homes|boliger/i })).toBeVisible();

  await page.getByTestId('filter-price-max').fill('5000000');
  await page.getByTestId('filter-area-min').fill('100');
  await page.getByTestId('filter-apply').click();
  await expect(page).toHaveURL(/(?:priceMax|price_max)=5000000/);
  await expect(page).toHaveURL(/(?:areaMin|area_min)=100/);

  const cards = page.getByTestId('listing-card');
  await expect(cards.first()).toBeVisible();
  const count = await cards.count();
  for (let index = 0; index < count; index += 1) {
    const card = cards.nth(index);
    const price = Number(await card.getAttribute('data-price'));
    const area = Number(await card.getAttribute('data-area'));
    expect(price, 'filtered card data-price').toBeLessThanOrEqual(5_000_000);
    expect(area, 'filtered card data-area').toBeGreaterThanOrEqual(100);
  }

  await expect(page.getByTestId('browse-map')).toBeVisible();
  await expect(page.getByTestId('map-marker')).toHaveCount(count);
  await cards.first().hover();
  await expect(page.getByTestId('map-marker').filter({ has: page.locator('[data-highlighted="true"]') })
    .or(page.locator('[data-testid="map-marker"][data-highlighted="true"]'))).toBeVisible();

  await page.getByTestId('filter-clear').click();
  await expect(page.getByTestId('filter-price-max')).toHaveValue('');
  await expect(page.getByTestId('filter-area-min')).toHaveValue('');
});
