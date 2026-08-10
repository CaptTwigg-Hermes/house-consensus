import { test, expect } from '../fixtures/test.js';
import { castGuidedVote, closeHousehold, createSeededE2EHousehold, E2E_AUTH_HEADER } from '../helpers/household.js';

test('combined identity conflict, personalized views, raw audit and separation work in English and Danish', async ({ browser }, testInfo) => {
  test.setTimeout(120_000);
  const household = await createSeededE2EHousehold(browser, testInfo.project.use.baseURL as string);
  const aliasEmail = `combined-alias-${Date.now()}@example.test`;
  const aliasContext = await browser.newContext({
    baseURL: testInfo.project.use.baseURL as string,
    extraHTTPHeaders: { [E2E_AUTH_HEADER]: aliasEmail }
  });
  const aliasPage = await aliasContext.newPage();
  const ownerObserver = await household.ownerPage.context().newPage();
  const aliasNote = `Alias audit note ${Date.now()}`;
  const memberNote = `Separate member note ${Date.now()}`;
  try {
    const step = (name: string) => console.log(`[combined-identities] ${name}`);
    step('open browse');
    await household.ownerPage.goto('/browse');
    const href = await household.ownerPage.getByTestId('listing-card').first().locator('.card-main').getAttribute('href');
    expect(href).toBeTruthy();
    step('cast alias vote');
    await aliasPage.goto(href!);
    await castGuidedVote(aliasPage, 'Like', aliasNote);
    await ownerObserver.goto(href!);
    await expect(ownerObserver.getByRole('button', { name: /start voting/i })).toBeVisible();
    await household.memberPage.goto(href!);
    await castGuidedVote(household.memberPage, 'Like', memberNote);

    step('combine identities');
    await household.ownerPage.goto('/owner/members');
    const ownerRow = household.ownerPage.getByTestId('member-row').filter({ hasText: household.owner.email });
    const aliasRow = household.ownerPage.getByTestId('member-row').filter({ hasText: aliasEmail });
    step('check owner');
    await ownerRow.getByRole('checkbox').check();
    step('check alias');
    await aliasRow.getByRole('checkbox').check();
    step('open combine dialog');
    await household.ownerPage.getByTestId('combine-identities-open').click();
    step('choose primary');
    await expect(household.ownerPage.getByRole('radio', { name: /^Owner$/i })).toBeChecked();
    step('preview');
    await household.ownerPage.getByTestId('combine-preview').click();
    await expect(household.ownerPage.getByTestId('combine-confirm')).toBeVisible();
    step('confirm');
    await household.ownerPage.getByTestId('combine-confirm').click();
    await expect(household.ownerPage.getByTestId('combine-dialog')).toHaveCount(0);
    await expect(ownerObserver.getByRole('button', { name: /change vote/i })).toBeVisible();

    step('verify browse queue and my votes');
    await household.ownerPage.goto('/browse');
    const card = household.ownerPage.getByTestId('listing-card').filter({ has: household.ownerPage.locator(`[href="${href}"]`) });
    await expect(card).toBeVisible();
    await household.ownerPage.goto('/queue');
    await expect(household.ownerPage.locator(`[href="${href}"]`)).toHaveCount(0);
    await household.ownerPage.goto('/my-votes');
    await expect(household.ownerPage.locator(`[href="${href}"]`)).toBeVisible();
    await household.ownerPage.goto('/everyone-likes');
    await expect(household.ownerPage.locator(`[href="${href}"]`)).toBeVisible();

    step('verify detail and household votes');
    await household.ownerPage.goto(href!);
    await expect(household.ownerPage.getByTestId('vote-via')).toContainText(/Owner.*via.*combined-alias/i);
    await expect(household.ownerPage.getByText(aliasNote)).toBeVisible();
    await expect(household.ownerPage.getByTestId('detail-edit-note-open')).toHaveCount(0);
    await household.ownerPage.goto('/household-votes');
    await expect(household.ownerPage.getByText(aliasNote)).toHaveCount(0);
    await expect(household.ownerPage.getByText(memberNote)).toBeVisible();

    await aliasPage.goto(href!);
    await expect(aliasPage.getByTestId('detail-edit-note-open')).toBeVisible();
    step('switch language');
    await household.ownerPage.goto('/owner/members');
    await household.ownerPage.getByTestId('desktop-language-select').selectOption('da');
    await expect(household.ownerPage.getByRole('heading', { name: /Medlemmer/i })).toBeVisible();

    step('separate alias');
    const combinedAlias = household.ownerPage.getByTestId('member-row').filter({ hasText: aliasEmail });
    await combinedAlias.getByRole('button', { name: /adskil/i }).click();
    await expect(combinedAlias.getByTestId('member-alias')).toHaveCount(0);
    await aliasPage.goto('/my-votes');
    await expect(aliasPage.getByText(aliasNote)).toBeVisible();
  } finally {
    await household.ownerPage.request.post('/api/e2e/reset-household-votes', { headers: { 'X-House-Consensus-CSRF': '1' } });
    await ownerObserver.close();
    await aliasContext.close();
    await closeHousehold(household);
  }
});