import { test as base, expect } from '@playwright/test';
import { MailpitClient } from '../helpers/mailpit.js';

interface Fixtures { mailpit: MailpitClient }

export const test = base.extend<Fixtures>({
  mailpit: async ({ request }, use) => {
    const client = new MailpitClient(request);
    await client.clear();
    await use(client);
  }
});

export { expect };
