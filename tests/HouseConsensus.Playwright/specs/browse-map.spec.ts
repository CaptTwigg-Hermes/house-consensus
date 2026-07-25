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
  const map = page.getByTestId('browse-map');
  const zoomBeforeWheel = await page.evaluate(() => (window as unknown as { hc: { maps: Record<string, { getZoom(): number }> } }).hc.maps['browse-map']!.getZoom());
  await map.hover();
  await page.mouse.wheel(0, -600);
  await expect.poll(() => page.evaluate(() => (window as unknown as { hc: { maps: Record<string, { getZoom(): number }> } }).hc.maps['browse-map']!.getZoom())).toBeGreaterThan(zoomBeforeWheel);
  const marker = page.locator('.leaflet-marker-icon').first();
  await expect(marker).toBeVisible();
  await marker.click();
  await expect(page.locator('.leaflet-popup .map-popup.rich')).toContainText('/100');
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

  const score = card.locator('.score-chip');
  await expect(score).toHaveJSProperty('tagName', 'BUTTON');
  expect((await score.boundingBox())!.height).toBeLessThanOrEqual(36);
  const tooltip = score.locator('.score-tooltip');
  const tooltipId = await tooltip.getAttribute('id');
  expect(tooltipId).toBeTruthy();
  await expect(score).toHaveAttribute('aria-describedby', tooltipId!);
  await score.hover();
  await expect(tooltip).toBeVisible();
  await expect(tooltip).toContainText(/Privacy|Privatliv/);
  await expect(tooltip).toContainText(/Children's space|Børnerum/);
  await expect(tooltip).toContainText(/Total/);

  await page.getByTestId('filter-open').click();
  await expect(page.getByTestId('filter-price-max')).toBeVisible();
  await expect(page.locator('.filter-drawer')).toBeVisible();
});


test('Browse persists applied parity filters independently', async ({ page, mailpit }, testInfo) => {
  await requestMagicLink(page, mailpit, identity(testInfo, 'owner'));
  await page.goto('/browse');
  await page.getByTestId('filter-open').click();
  await page.getByTestId('filter-preferred').click();
  await page.getByTestId('filter-apply').click();
  await page.reload();
  await page.getByTestId('filter-open').click();
  await expect(page.getByTestId('filter-preferred')).toHaveClass(/selected/);
  const keys = await page.evaluate(() => Object.keys(localStorage));
  expect(keys).toContain('hc.filters.browse');
  expect(keys).not.toContain('hc.filters.myvotes');
});


test('Listing detail shows commute and readable AI evidence without a conversation editor', async ({ page, mailpit }, testInfo) => {
  await requestMagicLink(page, mailpit, identity(testInfo, 'owner'));
  await page.goto('/browse');
  const card = page.getByTestId('listing-card').first();
  await expect(card.getByTestId('commute-table')).toContainText(/Høje Taastrup St\./);
  await expect(card.getByTestId('commute-table')).toContainText(/20 min/);
  await expect(card.getByTestId('commute-table')).toContainText(/31 min/);
  await expect(card.getByRole('link', { name: /open original listing|åbn original annonce/i })).toHaveAttribute('target', '_blank');
  await card.locator('.card-main').click();
  const evidence = page.getByTestId('ai-evidence');
  await expect(evidence).toBeVisible();
  await expect(evidence).toContainText('Two private household zones');
  await expect(evidence.locator('pre')).toHaveCount(0);
  await expect(page.locator('.comment-form')).toHaveCount(0);
});
