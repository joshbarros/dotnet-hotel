import { test, expect } from '@playwright/test';

const day = (n: number) => new Date(Date.now() + n * 86_400_000).toISOString().slice(0, 10);

test('add a room type, book a stay, see the voucher and the nights drop, cancel it', async ({
  page,
}) => {
  const run = Date.now().toString(36).toUpperCase();
  const code = `E2E-${run}`;
  const reference = `GUEST-${run}`;

  await page.goto('/');
  await page.getByLabel('Room type code').fill(code);
  await page.getByLabel('Total rooms').fill('5');
  await page.getByRole('button', { name: 'Add room type' }).click();
  await expect(page.getByRole('heading', { name: `Book a stay in ${code}` })).toBeVisible();
  await expect(page.getByTestId(`night-${day(2)}`)).toHaveText('5');

  await page.getByLabel('Guest reference').fill(reference);
  await page.getByLabel('Check-in').fill(day(2));
  await page.getByLabel('Nights', { exact: true }).fill('3');
  await page.getByLabel('Rooms', { exact: true }).fill('2');
  await page.getByRole('button', { name: 'Book stay' }).click();

  const row = page.locator(`tr[data-reference="${reference}"]`);
  await expect(row.getByTestId('status')).toHaveText('Confirmed', { timeout: 20_000 });
  await expect(row.getByTestId('voucher')).toHaveText(/VC-[0-9A-F]{12}/);
  // Three nights drop by two rooms; the night after check-out is untouched.
  for (const n of [2, 3, 4]) await expect(page.getByTestId(`night-${day(n)}`)).toHaveText('3');
  await expect(page.getByTestId(`night-${day(5)}`)).toHaveText('5');
  await expect(page.getByTestId(`night-${day(1)}`)).toHaveText('5');

  // Nothing is kept in the browser: a fresh load reads the same state from the API.
  await page.evaluate(() => localStorage.clear());
  await page.reload();
  await page.getByRole('button', { name: new RegExp(`^${code}`) }).click();
  const reloaded = page.locator(`tr[data-reference="${reference}"]`);
  await expect(reloaded.getByTestId('status')).toHaveText('Confirmed');
  await expect(page.getByTestId(`night-${day(3)}`)).toHaveText('3');

  await reloaded.getByRole('button', { name: 'Cancel' }).click();
  await expect(reloaded.getByTestId('status')).toHaveText('Cancelled');
  for (const n of [2, 3, 4]) await expect(page.getByTestId(`night-${day(n)}`)).toHaveText('5');
  // The issued voucher is not rolled back.
  await expect(reloaded.getByTestId('voucher')).toHaveText(/VC-[0-9A-F]{12}/);
  const bookings = await page.request.get(`/api/bookings?roomTypeCode=${code}`);
  const [booking] = await bookings.json();
  const voucher = await page.request.get(`/api/vouchers/${booking.id}`);
  expect((await voucher.json()).status).toBe('Issued');
});
