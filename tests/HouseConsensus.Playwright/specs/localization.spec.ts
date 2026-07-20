import { test, expect } from '../fixtures/test.js';
import { identity, requestMagicLink } from '../helpers/household.js';

test('English and Danish preferences persist across navigation, reload, and a new tab', async ({ page, context, mailpit }, testInfo) => {
  await requestMagicLink(page, mailpit, identity(testInfo, 'owner'));
  const language = page.getByTestId('language-select');

  await language.selectOption('da');
  await expect(page.locator('html')).toHaveAttribute('lang', /^da/);
  await expect(page.getByRole('link', { name: /boliger|gennemse/i })).toBeVisible();
  await page.reload();
  await expect(page.locator('html')).toHaveAttribute('lang', /^da/);

  const secondPage = await context.newPage();
  await secondPage.goto('/browse');
  await expect(secondPage.locator('html')).toHaveAttribute('lang', /^da/);
  await secondPage.getByTestId('language-select').selectOption('en');
  await expect(secondPage.locator('html')).toHaveAttribute('lang', /^en/);
  await secondPage.goto('/settings');
  await expect(secondPage.getByRole('heading', { name: /settings/i })).toBeVisible();
  await secondPage.reload();
  await expect(secondPage.getByTestId('language-select')).toHaveValue('en');
});
