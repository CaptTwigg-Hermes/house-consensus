import { expect, type Browser, type BrowserContext, type Page, type TestInfo } from '@playwright/test';
import type { MailpitClient } from './mailpit.js';

export interface Identity { name: string; email: string }
export interface Household {
  owner: Identity;
  member: Identity;
  ownerContext: BrowserContext;
  ownerPage: Page;
  memberContext: BrowserContext;
  memberPage: Page;
}

export function identity(testInfo: TestInfo, role: string): Identity {
  if (role === 'owner') return { name: 'Owner', email: 'owner@example.test' };
  const slug = `${testInfo.workerIndex}-${testInfo.testId}-${role}-${Date.now()}`
    .toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/(^-|-$)/g, '').slice(-48);
  return { name: `${role} ${slug.slice(-12)}`, email: `e2e+${slug}@example.test` };
}

export async function requestMagicLink(page: Page, mailpit: MailpitClient, person: Identity): Promise<void> {
  await page.goto('/sign-in');
  await page.getByTestId('auth-email').fill(person.email);
  await page.getByRole('button', { name: /send (magic|sign-in) link|continue/i }).click();
  await expect(page.getByTestId('auth-link-sent')).toBeVisible();
  const link = await mailpit.waitForLink(person.email, { subject: /sign|login|magic/i });
  await page.goto(link);
  const name = page.getByTestId('profile-name');
  if (await name.isVisible().catch(() => false)) {
    await name.fill(person.name);
    await page.getByRole('button', { name: /save|continue/i }).click();
  }
  await expect(page.getByTestId('app-shell')).toBeVisible();
}

export async function inviteMember(ownerPage: Page, mailpit: MailpitClient, member: Identity): Promise<string> {
  await ownerPage.goto('/owner/members');
  await ownerPage.getByTestId('member-invite-email').fill(member.email);
  await ownerPage.getByRole('button', { name: /invite/i }).click();
  await expect(ownerPage.getByTestId('member-notice')).toBeVisible();
  return mailpit.waitForLink(member.email, { subject: /sign|login|magic/i });
}

export async function createTwoMemberHousehold(
  browser: Browser,
  mailpit: MailpitClient,
  testInfo: TestInfo,
  baseURL: string
): Promise<Household> {
  const owner = identity(testInfo, 'owner');
  const member = identity(testInfo, 'member');
  const ownerContext = await browser.newContext({ baseURL });
  const ownerPage = await ownerContext.newPage();
  await requestMagicLink(ownerPage, mailpit, owner);
  const members = await ownerPage.request.get('/api/members');
  for (const member of await members.json() as Array<{ id: string; role: string | number; isActive: boolean }>) {
    if ((member.role === 'Member' || member.role === 'member' || member.role === 0) && member.isActive)
      expect((await ownerPage.request.post(`/api/members/${member.id}/deactivate`, { headers: { 'X-House-Consensus-CSRF': '1' } })).ok()).toBeTruthy();
  }
  const inviteLink = await inviteMember(ownerPage, mailpit, member);

  const memberContext = await browser.newContext({ baseURL });
  const memberPage = await memberContext.newPage();
  await memberPage.goto(inviteLink);
  const name = memberPage.getByTestId('profile-name');
  if (await name.isVisible().catch(() => false)) {
    await name.fill(member.name);
    await memberPage.getByRole('button', { name: /join|continue|save/i }).click();
  }
  await expect(memberPage.getByTestId('app-shell')).toBeVisible();
  return { owner, member, ownerContext, ownerPage, memberContext, memberPage };
}

export async function closeHousehold(household: Household): Promise<void> {
  await Promise.all([household.ownerContext.close(), household.memberContext.close()]);
}
