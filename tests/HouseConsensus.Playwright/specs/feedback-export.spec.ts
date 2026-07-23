import { test, expect } from '../fixtures/test.js';
import { identity, requestMagicLink } from '../helpers/household.js';

test('owner can export feedback as valid CSV and JSON containing the submitted record', async ({ page, mailpit }, testInfo) => {
  const marker = `=e2e-feedback-${Date.now()}`;
  await requestMagicLink(page, mailpit, identity(testInfo, 'owner'));
  await page.getByTestId('feedback-open').click();
  await page.getByTestId('feedback-message').fill(marker);
  await page.getByRole('button', { name: /send feedback/i }).click();
  await expect(page.getByTestId('feedback-success')).toBeVisible();
  await page.goto('/owner/feedback');
  const csvResponse = await page.request.get('/api/feedback/export.csv');
  expect(csvResponse.ok()).toBeTruthy();
  expect(await csvResponse.text()).toContain(`'${marker}`);
  const jsonResponse = await page.request.get('/api/feedback/export.json');
  expect(jsonResponse.ok()).toBeTruthy();
  const json = await jsonResponse.text();
  expect(json).toContain(marker);
  expect(json).not.toMatch(/magic[_-]?token|authorization|password/i);
});
