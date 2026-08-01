import { test, expect } from '../fixtures/test.js';
import { castGuidedVote, closeHousehold, createSeededE2EHousehold } from '../helpers/household.js';

test('two members voting Like produces a live unanimous match', async ({ browser }, testInfo) => {
  const household = await createSeededE2EHousehold(browser, testInfo.project.use.baseURL as string);
  try {
    await household.ownerPage.goto('/owner/members');
    const ownerAvatar = household.ownerPage.getByTestId('member-row')
      .filter({ hasText: household.owner.email }).locator('.avatar');
    const ownerInitials = (await ownerAvatar.textContent())?.trim();
    const ownerColor = await ownerAvatar.evaluate((element) => getComputedStyle(element).backgroundColor);
    expect(ownerInitials).toBeTruthy();

    await household.ownerPage.goto('/browse');
    const href = await household.ownerPage.getByTestId('listing-card').first().locator('.card-main').getAttribute('href');
    expect(href).toBeTruthy();
    await Promise.all([household.ownerPage.goto(href!), household.memberPage.goto(href!)]);
    await castGuidedVote(household.ownerPage, 'Like', 'Private note visible to the household');
    await expect(household.memberPage.getByText('Private note visible to the household')).toBeVisible();
    const listingAvatar = household.memberPage.locator('.household-votes .avatar');
    await expect(listingAvatar).toHaveText(ownerInitials!);
    await expect(listingAvatar).toHaveCSS('background-color', ownerColor);
    await expect(household.ownerPage.getByTestId('unanimity-status')).toContainText(/waiting|venter/i);
    await castGuidedVote(household.memberPage, 'Like');
    await expect(household.memberPage.getByTestId('match-banner')).toBeVisible();
    await expect(household.ownerPage.getByTestId('match-banner')).toBeVisible();
  } finally { await closeHousehold(household); }
});
