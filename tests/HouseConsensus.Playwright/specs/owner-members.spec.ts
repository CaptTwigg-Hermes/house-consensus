import { test, expect } from '../fixtures/test.js';
import { identity, inviteMember, requestMagicLink, sameOriginPath } from '../helpers/household.js';

test('owner can deactivate and reactivate a member while ownership remains protected', async ({ page, context, mailpit }, testInfo) => {
  const owner = identity(testInfo, 'owner');
  const member = identity(testInfo, 'managed-member');
  await requestMagicLink(page, mailpit, owner);
  const inviteLink = await inviteMember(page, mailpit, member);
  const memberContext = await context.browser()!.newContext({ baseURL: testInfo.project.use.baseURL as string });
  const memberPage = await memberContext.newPage();
  await memberPage.goto(sameOriginPath(inviteLink));
  await expect(memberPage.getByTestId('app-shell')).toBeVisible();

  await page.reload();
  const memberRow = page.getByTestId('member-row').filter({ hasText: member.email });
  await expect(memberRow.getByTestId('member-status')).toContainText(/active/i);
  await memberRow.getByRole('button', { name: /deactivate/i }).click();
  await expect(memberRow.getByTestId('member-status')).toContainText(/inactive/i);
  await memberPage.reload();
  await expect(memberPage.getByTestId('auth-email')).toBeVisible();

  await memberRow.getByRole('button', { name: /reactivate/i }).click();
  await expect(memberRow.getByTestId('member-status')).toContainText(/active/i);
  const ownerRow = page.getByTestId('member-row').filter({ hasText: owner.email });
  await expect(ownerRow.getByTestId('member-role')).toContainText(/owner/i);
  await expect(ownerRow.getByRole('button', { name: /deactivate/i })).toHaveCount(0);
  await memberContext.close();
});
