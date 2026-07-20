import { test, expect } from '../fixtures/test.js';
import { closeHousehold, createTwoMemberHousehold } from '../helpers/household.js';

test('two members voting interested produces a live unanimous match', async ({ browser, mailpit }, testInfo) => {
  const baseURL = testInfo.project.use.baseURL as string;
  const household = await createTwoMemberHousehold(browser, mailpit, testInfo, baseURL);
  try {
    await household.ownerPage.goto('/browse');
    const listing = household.ownerPage.getByTestId('listing-card').first();
    await expect(listing).toBeVisible();
    const href = await listing.getByRole('link').first().getAttribute('href');
    expect(href).toBeTruthy();

    await Promise.all([household.ownerPage.goto(href!), household.memberPage.goto(href!)]);
    await household.ownerPage.getByTestId('vote-interested').click();
    await expect(household.ownerPage.getByTestId('unanimity-status')).toContainText(/1\s*of\s*2|waiting/i);

    await household.memberPage.getByTestId('vote-interested').click();
    await expect(household.memberPage.getByTestId('unanimity-status')).toContainText(/unanimous|match|2\s*of\s*2/i);
    // The owner's open page must update through the live channel, without reload.
    await expect(household.ownerPage.getByTestId('unanimity-status')).toContainText(/unanimous|match|2\s*of\s*2/i);
    await expect(household.ownerPage.getByTestId('match-banner')).toBeVisible();
  } finally {
    await closeHousehold(household);
  }
});
