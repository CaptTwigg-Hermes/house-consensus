import { test, expect } from '../fixtures/test.js';
import { E2E_AUTH_HEADER, identity } from '../helpers/household.js';

test('Cloudflare-allowed identity is admitted without an application invite', async ({ context }, testInfo) => {
  const member = identity(testInfo, 'allowed-member');
  const memberContext = await context.browser()!.newContext({
    baseURL: testInfo.project.use.baseURL as string,
    extraHTTPHeaders: { [E2E_AUTH_HEADER]: member.email }
  });
  const memberPage = await memberContext.newPage();
  await memberPage.goto('/');

  await expect(memberPage.getByTestId('app-shell')).toBeVisible();
  const me = await memberPage.request.get('/api/auth/me');
  expect(me.ok()).toBeTruthy();
  expect((await me.json()).email).toBe(member.email);
  await memberContext.close();
});
