import { test, expect } from '../fixtures/test.js';
import { identity, inviteMember, requestMagicLink, sameOriginPath } from '../helpers/household.js';

test('owner invites a member who accepts through a Mailpit-delivered magic link', async ({ page, context, mailpit }, testInfo) => {
  const owner = identity(testInfo, 'owner');
  const member = identity(testInfo, 'invitee');

  await requestMagicLink(page, mailpit, owner);
  const inviteLink = await inviteMember(page, mailpit, member);

  const memberContext = await context.browser()!.newContext({ baseURL: testInfo.project.use.baseURL as string });
  const memberPage = await memberContext.newPage();
  await memberPage.goto(sameOriginPath(inviteLink));
  await expect(memberPage.getByTestId('app-shell')).toBeVisible();
  await expect(memberPage.getByTestId('current-user-email')).toHaveText(member.email);
  await memberContext.close();
});
