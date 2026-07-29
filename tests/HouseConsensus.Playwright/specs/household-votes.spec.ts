import { test, expect } from '../fixtures/test.js';
import { closeHousehold, createTwoMemberHousehold } from '../helpers/household.js';

test('household votes presents shared feedback as a responsive visual dashboard', async ({ browser, mailpit }, testInfo) => {
  const household = await createTwoMemberHousehold(browser, mailpit, testInfo, testInfo.project.use.baseURL as string);
  const privateNote = `Current member private note ${Date.now()}.`;
  try {
    await household.ownerPage.goto('/browse');
    const href = await household.ownerPage.getByTestId('listing-card').first().locator('.card-main').getAttribute('href');
    expect(href).toBeTruthy();

    await household.ownerPage.goto(href!);
    await household.ownerPage.getByTestId('vote-interested').click();
    await household.ownerPage.getByTestId('vote-note').fill('The private garden and separate wing both feel promising.');
    await household.ownerPage.getByRole('button', { name: /save vote|gem stemme/i }).click();

    await household.memberPage.setViewportSize({ width: 390, height: 844 });
    await household.memberPage.goto('/household-votes');
    const pulse = {
      homes: await household.memberPage.getByTestId('household-homes-count').locator('strong').innerText(),
      positive: await household.memberPage.getByTestId('household-positive-count').locator('strong').innerText(),
      notes: await household.memberPage.getByTestId('household-note-count').locator('strong').innerText()
    };
    expect(Number(pulse.homes)).toBeGreaterThanOrEqual(1);
    expect(Number(pulse.positive)).toBeGreaterThanOrEqual(1);
    expect(Number(pulse.notes)).toBeGreaterThanOrEqual(1);

    await household.memberPage.goto(href!);
    await household.memberPage.getByTestId('vote-interested').click();
    await household.memberPage.getByTestId('vote-note').fill(privateNote);
    await household.memberPage.getByRole('button', { name: /save vote|gem stemme/i }).click();
    await household.memberPage.goto('/household-votes');

    const votes = household.memberPage.getByTestId('household-votes');
    await expect(votes).toBeVisible();
    await expect(household.memberPage.getByTestId('household-homes-count').locator('strong')).toHaveText(pulse.homes);
    await expect(household.memberPage.getByTestId('household-positive-count').locator('strong')).toHaveText(pulse.positive);
    await expect(household.memberPage.getByTestId('household-note-count').locator('strong')).toHaveText(pulse.notes);
    const pulseContrasts = await household.memberPage.locator('.household-pulse-stat span').evaluateAll((labels) => {
      const channel = (value: number) => {
        const normalized = value / 255;
        return normalized <= 0.04045 ? normalized / 12.92 : ((normalized + 0.055) / 1.055) ** 2.4;
      };
      const luminance = (color: string) => {
        const values = color.match(/\d+/g)!.slice(0, 3).map(Number);
        return 0.2126 * channel(values[0]!) + 0.7152 * channel(values[1]!) + 0.0722 * channel(values[2]!);
      };
      return labels.map((label) => {
        const foreground = luminance(getComputedStyle(label).color);
        const background = luminance(getComputedStyle(label.parentElement!).backgroundColor);
        return (Math.max(foreground, background) + 0.05) / (Math.min(foreground, background) + 0.05);
      });
    });
    expect(pulseContrasts.every((ratio) => ratio >= 4.5)).toBe(true);

    const card = household.memberPage.getByTestId('household-vote-card').first();
    await expect(card.locator('.household-vote-cover')).toBeVisible();
    await expect(card.locator('.vote-choice-pill').first()).toContainText(/like|kan lide/i);
    await expect(card).toContainText('The private garden and separate wing both feel promising.');
    await expect(card).not.toContainText(privateNote);
    await expect(card.locator('textarea')).toHaveCount(0);
    await expect(card.getByRole('button', { name: /edit|save|redigér|gem/i })).toHaveCount(0);

    const mobileGeometry = await household.memberPage.evaluate(() => ({
      viewport: window.innerWidth,
      pageWidth: document.documentElement.scrollWidth,
      columns: getComputedStyle(document.querySelector('.household-votes')!).gridTemplateColumns,
      cardWidth: document.querySelector('.household-vote-card')!.getBoundingClientRect().width
    }));
    expect(mobileGeometry.pageWidth).toBe(mobileGeometry.viewport);
    expect(mobileGeometry.columns.split(' ')).toHaveLength(1);
    expect(mobileGeometry.cardWidth).toBeGreaterThan(340);

    await household.memberPage.getByTestId('language-select').selectOption('da');
    await expect(household.memberPage.getByRole('heading', { name: 'Husstandens stemmer' })).toBeVisible();
    const danishHeroHeight = await household.memberPage.locator('.household-hero').evaluate((element) => element.getBoundingClientRect().height);
    expect(danishHeroHeight).toBeLessThanOrEqual(190);

    await household.memberPage.setViewportSize({ width: 1180, height: 900 });
    const desktopColumns = await votes.evaluate((element) => getComputedStyle(element).gridTemplateColumns.split(' ').length);
    expect(desktopColumns).toBe(2);
  } finally {
    await closeHousehold(household);
  }
});
