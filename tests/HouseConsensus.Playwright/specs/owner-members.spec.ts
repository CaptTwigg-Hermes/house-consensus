import { test, expect } from '../fixtures/test.js';
import { E2E_AUTH_HEADER, E2E_OWNER, identity } from '../helpers/household.js';

test('owner sees Cloudflare-provisioned members without invitation controls', async ({ page, context }, testInfo) => {
  const member = identity(testInfo, 'allowed-member');
  const memberContext = await context.browser()!.newContext({
    baseURL: testInfo.project.use.baseURL as string,
    extraHTTPHeaders: { [E2E_AUTH_HEADER]: member.email }
  });
  const memberPage = await memberContext.newPage();
  await memberPage.goto('/');
  await expect(memberPage.getByTestId('app-shell')).toBeVisible();

  await page.goto('/owner/members');
  await expect(page.getByTestId('member-row').filter({ hasText: member.email })).toBeVisible();
  const ownerRow = page.getByTestId('member-row').filter({ hasText: E2E_OWNER.email });
  await expect(ownerRow.getByTestId('member-role')).toContainText(/owner/i);
  await expect(page.getByTestId('member-invite-email')).toHaveCount(0);
  await expect(page.getByRole('button', { name: /invite|deactivate|reactivate/i })).toHaveCount(0);
  await memberContext.close();
});
