import { test, expect } from '../fixtures/test.js';
import { identity, requestMagicLink } from '../helpers/household.js';

test('Browse applies price filters and renders the filtered homes on the map', async ({ page, mailpit }, testInfo) => {
  await requestMagicLink(page, mailpit, identity(testInfo, 'owner'));
  await page.goto('/browse');
  await expect(page.getByRole('heading', { name: /browse|gennemse/i })).toBeVisible();
  await expect(page.getByTestId('listing-card').first()).toBeVisible();
  await page.getByTestId('filter-open').click();
  await page.getByTestId('filter-price-max').fill('5000000');
  await page.getByTestId('filter-apply').click();
  const cards = page.getByTestId('listing-card');
  await expect(cards).toHaveCount(1);
  expect(Number(await cards.first().getAttribute('data-price'))).toBeLessThanOrEqual(5_000_000);
  await page.getByRole('button', { name: /map|kort/i }).click();
  await expect(page.getByTestId('browse-map')).toBeVisible();
  await expect(page.getByTestId('browse-map')).toHaveClass(/leaflet-container/);
  await page.getByTestId('filter-open').click();
  await page.getByTestId('filter-clear').click();
  await expect(page.getByTestId('filter-price-max')).toHaveValue('');
});


test('Mobile browse keeps controls compact and opens filters in a bottom drawer', async ({ page, mailpit }, testInfo) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await requestMagicLink(page, mailpit, identity(testInfo, 'owner'));
  await page.goto('/browse');

  const toolbar = page.locator('.browse-toolbar');
  await expect(toolbar).toBeVisible();
  expect((await toolbar.boundingBox())!.height).toBeLessThanOrEqual(64);
  await expect(page.getByTestId('filter-price-max')).toBeHidden();

  const card = page.getByTestId('listing-card').first();
  await expect(card.locator('.property-facts')).toBeVisible();
  await expect(card.locator('.card-image img')).toHaveAttribute('src', /\/api\/listings\/.+\/image$/);

  await page.getByTestId('filter-open').click();
  await expect(page.getByTestId('filter-price-max')).toBeVisible();
  await expect(page.locator('.filter-drawer')).toBeVisible();
});
