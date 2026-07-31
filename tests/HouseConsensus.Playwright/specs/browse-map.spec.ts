import { test, expect } from '../fixtures/test.js';
import { readFile } from 'node:fs/promises';
import { resolve } from 'node:path';

test('Browse cards stay equal height without space below the original listing link', async ({ page }) => {
  const css = await readFile(resolve(__dirname, '../../../src/Client/wwwroot/css/app.css'), 'utf8');
  await page.setViewportSize({ width: 900, height: 800 });
  await page.setContent(`<style>${css}</style><section class="card-grid">
    <article class="listing-card" data-testid="short-card"><a class="card-main"><div class="card-image"></div><div class="card-body"><div style="height:40px;flex:none"></div><footer class="card-footer"><span class="vote-dots"><i class="dislike"><span class="vote-symbol">×</span></i></span></footer></div></a><div class="card-external"><a class="btn ghost source-link">Open original listing</a></div></article>
    <article class="listing-card"><a class="card-main"><div class="card-image"></div><div class="card-body"><div style="height:160px;flex:none"></div><footer class="card-footer"><span class="vote-dots"><i class="like"><span class="vote-symbol">♥</span></i></span></footer></div></a><div class="card-external"><a class="btn ghost source-link">Open original listing</a></div></article>
  </section>`);

  const geometry = await page.locator('.listing-card').evaluateAll(cards => cards.map(card => {
    const cardBox = card.getBoundingClientRect();
    const footerBox = card.querySelector<HTMLElement>('.card-footer')!.getBoundingClientRect();
    const externalBox = card.querySelector<HTMLElement>('.card-external')!.getBoundingClientRect();
    const sourceLinkBox = card.querySelector<HTMLElement>('.source-link')!.getBoundingClientRect();
    return {
      height: cardBox.height,
      footerTop: footerBox.top,
      externalTop: externalBox.top,
      gapAfterExternal: cardBox.bottom - externalBox.bottom,
      gapUnderSourceLink: cardBox.bottom - sourceLinkBox.bottom
    };
  }));
  expect(Math.max(...geometry.map(item => item.gapAfterExternal))).toBeLessThanOrEqual(2);
  expect(Math.abs(geometry[0]!.height - geometry[1]!.height)).toBeLessThanOrEqual(2);
  expect(Math.abs(geometry[0]!.footerTop - geometry[1]!.footerTop)).toBeLessThanOrEqual(2);
  expect(Math.abs(geometry[0]!.externalTop - geometry[1]!.externalTop)).toBeLessThanOrEqual(2);
  expect(Math.min(...geometry.map(item => item.gapUnderSourceLink))).toBeGreaterThanOrEqual(14);
  expect(Math.max(...geometry.map(item => item.gapUnderSourceLink))).toBeLessThanOrEqual(16);
});


test('Browse applies price filters and renders the filtered homes on the map', async ({ page }) => {
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


test('Mobile browse keeps controls compact and opens filters in a bottom drawer', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
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

  const mapToggle = page.getByTestId('mobile-map-toggle');
  await expect(mapToggle).toBeVisible();
  expect((await mapToggle.boundingBox())!.height).toBeGreaterThanOrEqual(38);
  await mapToggle.click();
  await expect(page.getByTestId('browse-map')).toBeVisible();
  await expect(page.getByTestId('browse-map')).toHaveClass(/leaflet-container/);
  await page.getByTestId('mobile-list-toggle').click();

  await page.getByTestId('filter-open').click();
  await expect(page.getByTestId('filter-price-max')).toBeVisible();
  await expect(page.locator('.filter-drawer')).toBeVisible();
});


test('Browse persists applied parity filters independently', async ({ page }) => {
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


test('Listing detail shows commute and readable AI evidence without a conversation editor', async ({ page }) => {
  await page.goto('/browse');
  const card = page.getByTestId('listing-card').first();
  await expect(card.getByTestId('commute-table')).toContainText(/Høje Taastrup St\./);
  await expect(card.getByTestId('commute-table')).toContainText(/20 min/);
  await expect(card.getByTestId('commute-table')).toContainText(/31 min/);
  await expect(card.getByTestId('noise-levels')).toContainText(/road|vej/i);
  await expect(card.getByTestId('noise-levels')).toContainText(/rail|jernbane/i);
  await expect(card.getByTestId('noise-levels')).toContainText(/air|fly/i);
  await expect(card.getByRole('link', { name: /open original listing|åbn original annonce/i })).toHaveAttribute('target', '_blank');
  await card.locator('.card-main').click();
  const evidence = page.getByTestId('ai-evidence');
  await expect(evidence).toBeVisible();
  await expect(evidence).toContainText('Two private household zones');
  await expect(evidence.locator('pre')).toHaveCount(0);
  await expect(page.locator('.comment-form')).toHaveCount(0);
});
