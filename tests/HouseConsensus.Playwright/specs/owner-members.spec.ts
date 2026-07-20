import { test, expect } from '../fixtures/test.js';
import { identity, inviteMember, requestMagicLink } from '../helpers/household.js';

test('owner can invite, resend, revoke, and remove members while ownership remains protected', async ({ page, mailpit }, testInfo) => {
  const owner = identity(testInfo, 'owner');
  const pending = identity(testInfo, 'pending');
  await requestMagicLink(page, mailpit, owner);
  await inviteMember(page, mailpit, pending);

  const pendingRow = page.getByTestId('member-row').filter({ hasText: pending.email });
  await expect(pendingRow.getByTestId('member-status')).toContainText(/pending/i);
  await mailpit.clear();
  await pendingRow.getByRole('button', { name: /resend/i }).click();
  await expect(page.getByTestId('member-notice')).toContainText(/resent|sent/i);
  await mailpit.waitForLink(pending.email, { subject: /invite|join/i });

  await pendingRow.getByRole('button', { name: /revoke|cancel/i }).click();
  await page.getByRole('button', { name: /confirm|revoke/i }).click();
  await expect(pendingRow).toBeHidden();

  const accepted = identity(testInfo, 'accepted');
  const inviteLink = await inviteMember(page, mailpit, accepted);
  const memberContext = await page.context().browser()!.newContext({ baseURL: testInfo.project.use.baseURL as string });
  const memberPage = await memberContext.newPage();
  await memberPage.goto(inviteLink);
  const name = memberPage.getByTestId('profile-name');
  if (await name.isVisible().catch(() => false)) await name.fill(accepted.name);
  await memberPage.getByRole('button', { name: /accept|join|continue/i }).click();
  await expect(memberPage.getByTestId('app-shell')).toBeVisible();

  await page.reload();
  const memberRow = page.getByTestId('member-row').filter({ hasText: accepted.email });
  await expect(memberRow.getByTestId('member-status')).toContainText(/active|member/i);
  await memberRow.getByRole('button', { name: /remove/i }).click();
  await page.getByRole('button', { name: /confirm|remove/i }).click();
  await expect(memberRow).toBeHidden();
  await memberPage.reload();
  await expect(memberPage.getByTestId('household-access-revoked')).toBeVisible();

  const ownerRow = page.getByTestId('member-row').filter({ hasText: owner.email });
  await expect(ownerRow.getByTestId('member-role')).toContainText(/owner/i);
  await expect(ownerRow.getByRole('button', { name: /remove/i })).toHaveCount(0);
  await memberContext.close();
});
