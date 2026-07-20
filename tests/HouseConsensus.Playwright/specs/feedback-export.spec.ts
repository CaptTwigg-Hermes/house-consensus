import { test, expect } from '../fixtures/test.js';
import { identity, requestMagicLink } from '../helpers/household.js';

test('owner can export feedback as valid CSV and JSON containing the submitted record', async ({ page, mailpit }, testInfo) => {
  const owner = identity(testInfo, 'owner');
  const marker = `e2e-feedback-${testInfo.workerIndex}-${Date.now()}`;
  await requestMagicLink(page, mailpit, owner);

  await page.goto('/feedback');
  await page.getByTestId('feedback-message').fill(marker);
  await page.getByTestId('feedback-category').selectOption({ index: 1 });
  await page.getByRole('button', { name: /submit|send feedback/i }).click();
  await expect(page.getByTestId('feedback-success')).toBeVisible();

  await page.goto('/settings/feedback');
  const csvDownload = page.waitForEvent('download');
  await page.getByTestId('feedback-export-csv').click();
  const csv = await (await csvDownload).createReadStream();
  const csvText = await streamText(csv);
  expect(csvText).toMatch(/(?:message|feedback).*(?:category|created)/i);
  expect(csvText).toContain(marker);

  const jsonDownload = page.waitForEvent('download');
  await page.getByTestId('feedback-export-json').click();
  const json = await (await jsonDownload).createReadStream();
  const payload: unknown = JSON.parse(await streamText(json));
  const records = Array.isArray(payload) ? payload : (payload as { feedback?: unknown[] }).feedback;
  expect(Array.isArray(records)).toBeTruthy();
  expect(JSON.stringify(records)).toContain(marker);
  expect(JSON.stringify(records)).not.toMatch(/magic[_-]?token|authorization|password/i);
});

async function streamText(stream: NodeJS.ReadableStream): Promise<string> {
  const chunks: Buffer[] = [];
  for await (const chunk of stream) chunks.push(Buffer.isBuffer(chunk) ? chunk : Buffer.from(chunk));
  return Buffer.concat(chunks).toString('utf8');
}
