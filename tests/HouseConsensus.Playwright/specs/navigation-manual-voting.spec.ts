import { expect, test } from '@playwright/test';

const csrf = { 'X-House-Consensus-CSRF': '1' };

test('universal drawer manages focus, keyboard dismissal, and has no legacy navigation', async ({ page }) => {
  await page.goto('/browse');
  const trigger = page.getByTestId('menu-trigger');
  await expect(trigger).toBeVisible();
  await expect(page.locator('.side-nav, .bottom-nav')).toHaveCount(0);
  await trigger.click();
  const drawer = page.getByTestId('main-drawer');
  await expect(drawer).toBeVisible();
  await expect.poll(() => page.locator('main.content').evaluate(el => (el as HTMLElement).inert)).toBe(true);
  await expect.poll(() => page.evaluate(() => document.activeElement?.closest('[data-testid="main-drawer"]') !== null)).toBe(true);
  await page.screenshot({ path: 'test-results/navigation-drawer-desktop.png' });
  await page.keyboard.press('Escape');
  await expect(drawer).toHaveCount(0);
  await expect(trigger).toBeFocused();
});

test('manual listing persists optional fields and opens the guided ten-category vote', async ({ page, request }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  const stamp = Date.now();
  const response = await request.post('/api/listings', {
    headers: csrf,
    data: { url: `https://example.test/manual-${stamp}?utm_source=e2e`, address: `Manualvej ${stamp}`, city: 'Roskilde', askingPrice: 7500000 },
  });
  expect(response.status()).toBe(201);
  const created = await response.json() as { listingId: string };
  await page.goto(`/listing/${created.listingId}`);
  await expect(page.getByText('Manually added', { exact: true })).toBeVisible();
  await expect(page.getByText('Unscored', { exact: true })).toBeVisible();
  await expect(page.getByText('Roskilde', { exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Start voting' }).click();
  const sheet = page.getByTestId('guided-vote-sheet');
  await expect(sheet).toBeVisible();
  await expect.poll(() => page.locator('.topbar').evaluate(el => (el as HTMLElement).inert)).toBe(true);
  await expect(sheet.locator('.category-rating')).toHaveCount(10);
  expect(await sheet.evaluate(el => el.scrollWidth === el.clientWidth)).toBe(true);
  await expect.poll(() => page.evaluate(() => document.activeElement?.closest('[data-testid="guided-vote-sheet"]') !== null)).toBe(true);
  const firstCategory = sheet.locator('.category-rating').first();
  const geometry = await firstCategory.evaluate((fieldset) => {
    const sheetBox = fieldset.closest('[data-testid="guided-vote-sheet"]')!.getBoundingClientRect();
    return [...fieldset.querySelectorAll('label')].map(label => {
      const box = label.getBoundingClientRect();
      return box.left >= sheetBox.left && box.right <= sheetBox.right;
    });
  });
  expect(geometry).toEqual([true, true, true]);
  await firstCategory.locator('label').filter({ hasText: /^Like$/ }).click();
  await sheet.getByRole('button', { name: 'Review vote' }).click();
  await expect(sheet.locator('.vote-summary')).toBeInViewport();
  await expect(sheet.locator('.vote-summary')).toContainText('Derived result: Like');
  await page.screenshot({ path: 'test-results/guided-vote-mobile.png' });
  await sheet.getByRole('button', { name: 'Confirm' }).click();
  await expect(page.getByRole('button', { name: 'Change vote' })).toBeVisible();
});

test('guided vote overlay stays viewport-fixed while its house card is hovered', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 800 });
  await page.goto('/');
  const card = page.getByTestId('listing-card').first();
  await expect(card).toBeVisible();
  await card.hover();
  await card.getByTestId('vote-interested').click();

  const backdrop = page.locator('.sheet-backdrop');
  await expect(backdrop).toBeVisible();
  const box = (await backdrop.boundingBox())!;
  expect(box.x).toBeLessThanOrEqual(1);
  expect(box.y).toBeLessThanOrEqual(1);
  expect(box.width).toBeGreaterThanOrEqual(1278);
  expect(box.height).toBeGreaterThanOrEqual(798);

  await page.mouse.move(-10, -10);
  await page.mouse.move(640, 400);
  const reenteredBox = (await backdrop.boundingBox())!;
  expect(reenteredBox.x).toBeLessThanOrEqual(1);
  expect(reenteredBox.y).toBeLessThanOrEqual(1);
  expect(reenteredBox.width).toBeGreaterThanOrEqual(1278);
  expect(reenteredBox.height).toBeGreaterThanOrEqual(798);
});
