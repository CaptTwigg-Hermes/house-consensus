import { test, expect } from '../fixtures/test.js';

async function openLanguage(page: import('@playwright/test').Page) {
  await expect(page.getByTestId('app-shell')).toBeVisible();
  const desktopNavigation = page.getByTestId('desktop-navigation');
  if ((page.viewportSize()?.width ?? 0) >= 1000) {
    await expect(desktopNavigation).toBeVisible();
    await expect(desktopNavigation.getByTestId('desktop-language-select')).toBeVisible();
    return;
  }
  if (!(await page.getByTestId('main-drawer').isVisible())) await page.getByTestId('menu-trigger').click();
  await expect(page.getByTestId('main-drawer').getByTestId('language-select')).toBeVisible();
}

test('English and Danish preferences persist across navigation, reload, and a new tab', async ({ page, context }) => {
  await page.goto('/');
  await openLanguage(page);
  await page.getByTestId('desktop-language-select').selectOption('da');
  await expect(page.locator('html')).toHaveAttribute('lang', /^da/);
  await openLanguage(page);
  await expect(page.getByRole('link', { name: /gennemse/i }).first()).toBeVisible();
  await page.reload();
  await openLanguage(page);
  await expect(page.getByTestId('desktop-language-select')).toHaveValue('da');
  const secondPage = await context.newPage();
  await secondPage.goto('/browse');
  await openLanguage(secondPage);
  await expect(secondPage.getByTestId('desktop-language-select')).toHaveValue('da');
  await secondPage.getByTestId('desktop-language-select').selectOption('en');
  await expect(secondPage.locator('html')).toHaveAttribute('lang', /^en/);
  await secondPage.reload();
  await openLanguage(secondPage);
  await expect(secondPage.getByTestId('desktop-language-select')).toHaveValue('en');
});
