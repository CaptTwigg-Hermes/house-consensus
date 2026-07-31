import { test, expect } from '../fixtures/test.js';
import { E2E_AUTH_HEADER, E2E_OWNER, identity, inviteMember } from '../helpers/household.js';

test('owner can deactivate and reactivate a member while ownership remains protected', async ({ page, context }, testInfo) => {
  const member = identity(testInfo, 'managed-member');
  await inviteMember(page, member);
  const memberContext = await context.browser()!.newContext({
    baseURL: testInfo.project.use.baseURL as string,
    extraHTTPHeaders: { [E2E_AUTH_HEADER]: member.email }
  });
  const memberPage = await memberContext.newPage();
  await memberPage.goto('/');
  await expect(memberPage.getByTestId('app-shell')).toBeVisible();

  await page.reload();
  const memberRow = page.getByTestId('member-row').filter({ hasText: member.email });
  await expect(memberRow.getByTestId('member-status')).toContainText(/active/i);
  await memberRow.getByRole('button', { name: /deactivate/i }).click();
  await expect(memberRow.getByTestId('member-status')).toContainText(/inactive/i);
  const unauthorized = await memberPage.request.get('/api/auth/me');
  expect(unauthorized.status()).toBe(401);

  await memberRow.getByRole('button', { name: /reactivate/i }).click();
  await expect(memberRow.getByTestId('member-status')).toContainText(/active/i);
  await memberPage.reload();
  await expect(memberPage.getByTestId('app-shell')).toBeVisible();
  const ownerRow = page.getByTestId('member-row').filter({ hasText: E2E_OWNER.email });
  await expect(ownerRow.getByTestId('member-role')).toContainText(/owner/i);
  await expect(ownerRow.getByRole('button', { name: /deactivate/i })).toHaveCount(0);
  await memberContext.close();
});
