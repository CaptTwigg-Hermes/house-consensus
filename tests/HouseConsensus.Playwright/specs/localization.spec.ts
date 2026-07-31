import { test, expect } from '../fixtures/test.js';

test('English and Danish preferences persist across navigation, reload, and a new tab', async ({ page, context }) => {
  await page.goto('/');
  await page.getByTestId('language-select').selectOption('da');
  await expect(page.locator('html')).toHaveAttribute('lang', /^da/);
  await expect(page.getByRole('link', { name: /gennemse/i }).first()).toBeVisible();
  await page.reload();
  await expect(page.getByTestId('language-select')).toHaveValue('da');
  const secondPage = await context.newPage();
  await secondPage.goto('/browse');
  await expect(secondPage.getByTestId('language-select')).toHaveValue('da');
  await secondPage.getByTestId('language-select').selectOption('en');
  await expect(secondPage.locator('html')).toHaveAttribute('lang', /^en/);
  await secondPage.reload();
  await expect(secondPage.getByTestId('language-select')).toHaveValue('en');
});
