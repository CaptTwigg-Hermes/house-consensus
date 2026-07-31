import { test, expect } from '../fixtures/test.js';

test('vote notes appear on compact My Votes cards and remain editable', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto('/browse');
  const card = page.getByTestId('listing-card').first();
  await expect(card).toBeVisible();
  const href = await card.locator('.card-main').getAttribute('href');
  expect(href).toBeTruthy();
  await page.goto(href!);
  await page.getByTestId('vote-reject').click();
  await expect(page.getByTestId('vote-note-sheet')).toBeVisible();
  await page.getByTestId('vote-note').fill('Kitchen needs too much work');
  await page.getByRole('button', { name: /save vote|gem stemme/i }).click();
  await expect(page.getByTestId('vote-note-sheet')).toBeHidden();
  await page.getByTestId('detail-edit-note-open').click();
  await page.getByTestId('detail-edit-note').fill('Kitchen and bath need work');
  await page.getByRole('button', { name: /^save$|^gem$/i }).click();
  await expect(page.locator('.household-votes')).toContainText('Kitchen and bath need work');

  await page.goto('/my-votes');
  const voted = page.getByTestId('my-vote-card').first();
  await expect(voted).toBeVisible();
  await expect(voted.locator('.my-vote-image img')).toHaveAttribute('src', /\/api\/listings\/.+\/image$/);
  await expect(voted.locator('.my-vote-current.dislike')).toBeVisible();
  await expect(voted.getByTestId('commute-table')).toContainText(/20 min/);
  const imageBox = (await voted.locator('.my-vote-image').boundingBox())!;
  const copyBox = (await voted.locator('.my-vote-copy').boundingBox())!;
  expect(copyBox.x).toBeGreaterThanOrEqual(imageBox.x + imageBox.width - 1);
  await expect(voted).toContainText('Kitchen and bath need work');
  await voted.getByTestId('my-vote-edit-note-open').click();
  await voted.getByTestId('my-vote-edit-note').fill('Kitchen and bathroom need too much work');
  await voted.getByTestId('my-vote-save-note').click();
  await expect(voted).toContainText('Kitchen and bathroom need too much work');

  await voted.getByTestId('vote-interested').click();
  await page.getByTestId('vote-skip-comment').click();
  await expect(voted.locator('.choice.like')).toBeVisible();

  await page.getByTestId('my-votes-like-filter').click();
  await expect(page.getByTestId('my-vote-card')).toHaveCount(0);
  await page.reload();
  await expect(page.getByTestId('my-vote-card')).toHaveCount(0);
});
