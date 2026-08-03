import { expect, type Browser, type BrowserContext, type Locator, type Page, type TestInfo } from '@playwright/test';

export interface Identity { email: string; name: string }
export interface HouseholdSession {
  owner: Identity;
  member: Identity;
  ownerContext: BrowserContext;
  memberContext: BrowserContext;
  ownerPage: Page;
  memberPage: Page;
}

export const E2E_AUTH_HEADER = 'X-House-Consensus-E2E-Email';
export const E2E_OWNER: Identity = { email: 'owner@example.test', name: 'E2E Owner' };
export const E2E_MEMBER: Identity = { email: 'e2e-member@example.test', name: 'E2E Member' };

export function identity(testInfo: TestInfo, role: string): Identity {
  const stem = `${testInfo.project.name}-${testInfo.workerIndex}-${testInfo.repeatEachIndex}-${Date.now()}-${role}`
    .toLowerCase().replace(/[^a-z0-9-]/g, '-');
  return { email: `${stem}@example.test`, name: role === 'owner' ? 'E2E Owner' : 'E2E Member' };
}

export async function createSeededE2EHousehold(browser: Browser, baseURL: string): Promise<HouseholdSession> {
  const ownerContext = await browser.newContext({
    baseURL,
    extraHTTPHeaders: { [E2E_AUTH_HEADER]: E2E_OWNER.email }
  });
  const ownerPage = await ownerContext.newPage();
  const reset = await ownerPage.request.post('/api/e2e/reset-household-votes', {
    headers: { 'X-House-Consensus-CSRF': '1' }
  });
  expect(reset.ok()).toBeTruthy();
  await ownerPage.goto('/');
  await expect(ownerPage.getByTestId('app-shell')).toBeVisible();

  const memberContext = await browser.newContext({
    baseURL,
    extraHTTPHeaders: { [E2E_AUTH_HEADER]: E2E_MEMBER.email }
  });
  const memberPage = await memberContext.newPage();
  await memberPage.goto('/');
  await expect(memberPage.getByTestId('app-shell')).toBeVisible();

  return {
    owner: E2E_OWNER,
    member: E2E_MEMBER,
    ownerContext,
    memberContext,
    ownerPage,
    memberPage
  };
}

export async function castGuidedVote(page: Page, choice: 'Like' | 'Dislike', note?: string, root?: Locator, overallScore = 4): Promise<void> {
  await (root ?? page).getByTestId('vote-interested').click();
  const sheet = page.getByTestId('guided-vote-sheet');
  await expect(sheet).toBeVisible();
  const localizedChoice = choice === 'Like' ? /^(Like|Kan lide)$/ : /^(Dislike|Kan ikke lide)$/;
  await sheet.locator('.category-rating').first().locator('label').filter({ hasText: localizedChoice }).click();
  await sheet.getByRole('button', { name: /review vote|gennemgå stemme/i }).click();
  if (note) await sheet.getByTestId('vote-note').fill(note);
  await sheet.getByTestId('overall-score').locator('label').filter({ hasText: new RegExp(`^${overallScore}$`) }).click();
  await sheet.getByRole('button', { name: /confirm|bekræft/i }).click();
  await expect(sheet).toHaveCount(0);
}

export async function closeHousehold(household: HouseholdSession): Promise<void> {
  await Promise.all([household.ownerContext.close(), household.memberContext.close()]);
}
