import { test, expect } from '../fixtures/test.js';
import { closeHousehold, createTwoMemberHousehold } from '../helpers/household.js';

test('two members voting Like produces a live unanimous match', async ({ browser, mailpit }, testInfo) => {
  const household = await createTwoMemberHousehold(browser, mailpit, testInfo, testInfo.project.use.baseURL as string);
  try {
    await household.ownerPage.goto('/browse');
    const href = await household.ownerPage.getByTestId('listing-card').first().getByRole('link').getAttribute('href');
    expect(href).toBeTruthy();
    await Promise.all([household.ownerPage.goto(href!), household.memberPage.goto(href!)]);
    await household.ownerPage.getByTestId('vote-interested').click();
    await household.ownerPage.getByTestId('vote-note').fill('Private note visible to the household');
    await household.ownerPage.getByRole('button', { name: /save vote|gem stemme/i }).click();
    await expect(household.memberPage.getByText('Private note visible to the household')).toBeVisible();
    await expect(household.ownerPage.getByTestId('unanimity-status')).toContainText(/waiting|venter/i);
    await household.memberPage.getByTestId('vote-interested').click();
    await household.memberPage.getByRole('button', { name: /save vote|gem stemme/i }).click();
    await expect(household.memberPage.getByTestId('match-banner')).toBeVisible();
    await expect(household.ownerPage.getByTestId('match-banner')).toBeVisible();
  } finally { await closeHousehold(household); }
});
