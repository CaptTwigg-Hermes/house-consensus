import { test, expect } from '../fixtures/test.js';

test('asbestos assessment is visible, correctable, filterable, and keyboard safe', async ({ page }, testInfo) => {
  const consoleErrors: string[] = [];
  page.on('console', message => {
    if (message.type() !== 'error') return;
    const location = message.location().url;
    const knownE2ENoise = location.includes('fonts.gstatic.com')
      || message.text().includes('fonts.gstatic.com')
      || location.includes('images.example.test')
      || location.endsWith('/image')
      || message.text().includes("WebSocket connection to 'ws:");
    if (!knownE2ENoise) consoleErrors.push(message.text());
  });
  page.on('pageerror', error => consoleErrors.push(error.message));
  await page.setViewportSize({ width: 390, height: 844 });
  const danish = await page.request.put('/api/auth/language', {
    headers: { 'X-House-Consensus-CSRF': '1' },
    data: { language: 'da' }
  });
  expect(danish.ok()).toBeTruthy();
  await page.goto('/browse');

  const card = page.getByTestId('listing-card').first();
  await expect(card).toBeVisible();
  const listingId = await card.getAttribute('data-listing-id');
  const href = await card.locator('.card-main').getAttribute('href');
  expect(listingId).toBeTruthy();
  expect(href).toBeTruthy();

  const reset = await page.request.put(`/api/listings/${listingId}/asbestos-roof-correction`, {
    headers: { 'X-House-Consensus-CSRF': '1' },
    data: { status: null, confirmed: true }
  });
  expect(reset.ok()).toBeTruthy();

  await page.goto(href!);
  const status = page.getByTestId('asbestos-status');
  const trigger = page.getByTestId('asbestos-assessment-open');
  await expect(status).toHaveAttribute('data-status', 'unknown');
  await expect(status).toContainText(/unknown|ukendt/i);
  await expect(status.getByTestId('asbestos-evidence-list')).toContainText('Ingen tagrelevant dokumentation var tilgængelig.');
  await expect(status.getByTestId('asbestos-evidence-list')).not.toContainText(/[{}\[\]]/);

  await trigger.click();
  const dialog = page.getByTestId('asbestos-confirm-dialog');
  await expect(dialog).toBeVisible();
  await expect.poll(() => page.evaluate(() => document.querySelector('[data-testid="asbestos-confirm-dialog"]')?.contains(document.activeElement))).toBe(true);
  const box = await dialog.boundingBox();
  expect(box).not.toBeNull();
  expect(box!.x).toBeGreaterThanOrEqual(0);
  expect(box!.x + box!.width).toBeLessThanOrEqual(390);
  expect(await dialog.evaluate(element => element.scrollWidth <= element.clientWidth)).toBe(true);
  await page.screenshot({ path: testInfo.outputPath('asbestos-confirm-mobile.png'), fullPage: true });

  await page.keyboard.press('Escape');
  await expect(dialog).toBeHidden();
  await expect(trigger).toBeFocused();

  await trigger.click();
  await dialog.getByRole('button', { name: /possible|mulig/i }).click();
  await dialog.getByTestId('asbestos-confirm').click();
  await expect(dialog).toBeHidden();
  await expect(status).toHaveAttribute('data-status', 'possible');
  await expect(status.getByTestId('asbestos-automated-provenance')).toBeVisible();
  await expect(status.getByTestId('asbestos-automated-status')).toContainText(/unknown|ukendt/i);
  await expect(trigger).toBeFocused();

  await trigger.click();
  await dialog.getByRole('button', { name: /likely|sandsynlig/i }).click();
  await dialog.getByTestId('asbestos-confirm').click();
  await expect(status).toHaveAttribute('data-status', 'likely');

  await trigger.click();
  await page.locator('.modal-backdrop').click({ position: { x: 2, y: 2 } });
  await expect(dialog).toBeHidden();
  await expect(status).toHaveAttribute('data-status', 'likely');

  await trigger.click();
  await dialog.getByRole('button', { name: /use latest automated assessment|brug seneste automatiske vurdering/i }).click();
  await dialog.getByTestId('asbestos-confirm').click();
  await expect(status).toHaveAttribute('data-status', 'unknown');

  await trigger.click();
  await dialog.getByRole('button', { name: /possible|mulig/i }).click();
  await dialog.getByTestId('asbestos-confirm').click();
  await expect(status).toHaveAttribute('data-status', 'possible');

  await page.goto('/browse');
  await page.getByTestId('filter-open').click();
  await page.getByTestId('filter-asbestos-Possible').click();
  await page.getByTestId('filter-asbestos-human-corrected').click();
  await page.getByTestId('filter-apply').click();

  await expect(page.locator(`[data-testid="listing-card"][data-listing-id="${listingId}"]`)).toBeVisible();
  await expect(page.getByTestId('listing-card')).toHaveCount(1);
  const english = await page.request.put('/api/auth/language', {
    headers: { 'X-House-Consensus-CSRF': '1' },
    data: { language: 'en' }
  });
  expect(english.ok()).toBeTruthy();
  expect(consoleErrors).toEqual([]);
});
