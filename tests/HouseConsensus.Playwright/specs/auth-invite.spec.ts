import { test, expect } from '../fixtures/test.js';
import { E2E_AUTH_HEADER, E2E_OWNER, identity, inviteMember } from '../helpers/household.js';

test('Cloudflare-authenticated owner invites a member without application email delivery', async ({ page, context }, testInfo) => {
  const member = identity(testInfo, 'invitee');
  await page.goto('/');
  await expect(page.getByTestId('current-user-email')).toHaveText(E2E_OWNER.email);
  await inviteMember(page, member);

  const memberContext = await context.browser()!.newContext({
    baseURL: testInfo.project.use.baseURL as string,
    extraHTTPHeaders: { [E2E_AUTH_HEADER]: member.email }
  });
  const memberPage = await memberContext.newPage();
  await memberPage.goto('/');
  await expect(memberPage.getByTestId('app-shell')).toBeVisible();
  await expect(memberPage.getByTestId('current-user-email')).toHaveText(member.email);
  await memberContext.close();
});
