import { expect, test } from '@playwright/test';

const csrf = { 'X-House-Consensus-CSRF': '1' };

test('mobile menu trigger is left of the brand and drawer remains keyboard accessible', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto('/browse');
  const trigger = page.getByTestId('menu-trigger');
  const brand = page.locator('.topbar .brand');
  await expect(trigger).toBeVisible();
  await expect(page.getByTestId('desktop-navigation')).toBeHidden();
  const triggerBox = (await trigger.boundingBox())!;
  const brandBox = (await brand.boundingBox())!;
  expect(triggerBox.x).toBeLessThan(brandBox.x);
  expect(triggerBox.width).toBeGreaterThanOrEqual(44);
  await page.screenshot({ path: 'test-results/navigation-mobile-header.png' });
  await trigger.click();
  const drawer = page.getByTestId('main-drawer');
  await expect(drawer).toBeVisible();
  await expect.poll(() => page.locator('main.content').evaluate(el => (el as HTMLElement).inert)).toBe(true);
  await expect.poll(() => page.evaluate(() => document.activeElement?.closest('[data-testid="main-drawer"]') !== null)).toBe(true);
  await page.screenshot({ path: 'test-results/navigation-drawer-mobile.png' });
  await page.keyboard.press('Escape');
  await expect(drawer).toHaveCount(0);
  await expect(trigger).toBeFocused();
});

test('desktop navigation stays open and content remains beside it', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 800 });
  await page.goto('/browse');
  const navigation = page.getByTestId('desktop-navigation');
  await expect(navigation).toBeVisible();
  await expect(page.getByTestId('menu-trigger')).toBeHidden();
  await expect(page.getByTestId('main-drawer')).toHaveCount(0);
  await expect(page.getByTestId('drawer-backdrop')).toHaveCount(0);
  await expect(navigation.getByRole('link', { name: 'Browse', exact: true })).toHaveClass(/active/);
  const navigationBox = (await navigation.boundingBox())!;
  const contentBox = (await page.locator('main.content').boundingBox())!;
  expect(navigationBox.x).toBeLessThanOrEqual(1);
  expect(navigationBox.width).toBeGreaterThanOrEqual(240);
  expect(contentBox.x).toBeGreaterThanOrEqual(navigationBox.x + navigationBox.width);
  await page.screenshot({ path: 'test-results/navigation-desktop-open.png' });
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
  const overallScore = sheet.getByTestId('overall-score');
  await expect(overallScore).toBeVisible();
  await expect(overallScore.getByRole('radio')).toHaveCount(5);
  await expect(sheet.getByRole('button', { name: 'Confirm' })).toBeDisabled();
  expect(await sheet.getByTestId('vote-note').evaluate((note, score) => Boolean(note.compareDocumentPosition(score as Node) & Node.DOCUMENT_POSITION_FOLLOWING), await overallScore.elementHandle())).toBe(true);
  await overallScore.locator('label').filter({ hasText: /^4$/ }).click();
  await expect(sheet.getByRole('button', { name: 'Confirm' })).toBeEnabled();
  await overallScore.scrollIntoViewIfNeeded();
  await expect(overallScore).toBeInViewport();
  await page.screenshot({ path: 'test-results/guided-vote-mobile.png' });
  await sheet.getByRole('button', { name: 'Confirm' }).click();
  await expect(page.getByRole('button', { name: 'Change vote' })).toBeVisible();
  let detail = await (await request.get(`/api/listings/${created.listingId}`)).json() as { votes: Array<{ overallScore: number }> };
  expect(detail.votes).toContainEqual(expect.objectContaining({ overallScore: 4 }));

  await page.getByRole('button', { name: 'Change vote' }).click();
  await page.getByTestId('guided-vote-sheet').getByRole('button', { name: 'Review vote' }).click();
  await expect(page.getByTestId('overall-score-4')).toBeChecked();
  await page.getByTestId('overall-score').locator('label').filter({ hasText: /^5$/ }).click();
  await expect(page.getByTestId('overall-score-5')).toBeChecked();
  await page.getByTestId('guided-vote-sheet').getByRole('button', { name: 'Confirm' }).click();
  await expect(page.getByTestId('guided-vote-sheet')).toBeHidden();
  detail = await (await request.get(`/api/listings/${created.listingId}`)).json() as { votes: Array<{ overallScore: number }> };
  expect(detail.votes).toContainEqual(expect.objectContaining({ overallScore: 5 }));
});

test('guided vote overlay stays viewport-fixed while its house card is hovered', async ({ page, request }) => {
  await page.setViewportSize({ width: 1280, height: 800 });
  const reset = await request.post('/api/e2e/reset-household-votes', { headers: csrf });
  expect(reset.ok()).toBeTruthy();
  await page.goto('/');
  const card = page.getByTestId('listing-card').filter({ has: page.getByTestId('vote-interested') }).first();
  await expect(card).toBeVisible();
  await card.hover();
  await card.getByTestId('vote-interested').click();

  const backdrop = page.locator('.sheet-backdrop');
  await expect(backdrop).toBeVisible();
  const expectViewportBackdrop = (box: { x: number; y: number; width: number; height: number }) => {
    expect(box.x).toBeGreaterThanOrEqual(-1);
    expect(box.x).toBeLessThanOrEqual(1);
    expect(box.y).toBeGreaterThanOrEqual(-1);
    expect(box.y).toBeLessThanOrEqual(1);
    expect(box.x + box.width).toBeGreaterThanOrEqual(1279);
    expect(box.x + box.width).toBeLessThanOrEqual(1281);
    expect(box.y + box.height).toBeGreaterThanOrEqual(799);
    expect(box.y + box.height).toBeLessThanOrEqual(801);
  };
  expectViewportBackdrop((await backdrop.boundingBox())!);

  await page.mouse.move(-10, -10);
  await page.mouse.move(640, 400);
  expectViewportBackdrop((await backdrop.boundingBox())!);
});

test('header shows the authoritative running server version', async ({ page }) => {
  await page.goto('/');
  const response = await page.request.get('/api/version');
  expect(response.ok()).toBeTruthy();
  expect(response.headers()['cache-control']).toContain('no-store');
  const body = await response.json() as { version: string };
  expect(body.version).toMatch(/^v(dev|[0-9a-f]{7})$/);
  if (process.env.E2E_EXPECTED_VERSION) expect(body.version).toBe(process.env.E2E_EXPECTED_VERSION);
  await expect(page.getByTestId('app-version')).toHaveText(body.version);
  for (const path of ['/service-worker.js', '/service-worker-assets.js']) {
    const workerAsset = await page.request.get(path);
    expect(workerAsset.ok()).toBeTruthy();
    expect(workerAsset.headers()['cache-control']).toContain('no-store');
  }
  const stylesheet = await page.request.get('/css/app.css');
  expect(stylesheet.ok()).toBeTruthy();
  expect(stylesheet.headers()['cache-control']).toContain('no-store');
});
