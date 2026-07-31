import { expect, type Browser, type BrowserContext, type Page, type TestInfo } from '@playwright/test';

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

export async function inviteMember(ownerPage: Page, member: Identity): Promise<void> {
  await ownerPage.goto('/owner/members');
  await ownerPage.getByTestId('member-invite-email').fill(member.email);
  await ownerPage.getByRole('button', { name: /invite member/i }).click();
  await expect(ownerPage.getByTestId('member-notice')).toBeVisible();
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

export async function closeHousehold(household: HouseholdSession): Promise<void> {
  await Promise.all([household.ownerContext.close(), household.memberContext.close()]);
}
