import { test, expect, Page } from '@playwright/test';
import { ADMIN, signIn } from './support';

const run = () => Date.now().toString(36).toUpperCase() + Math.floor(Math.random() * 1000);
const day = (n: number) => new Date(Date.now() + n * 86_400_000).toISOString().slice(0, 10);

async function addRoomType(page: Page, code: string, totalRooms: number) {
  await signIn(page, ADMIN);
  await page.getByLabel('Room type code').fill(code);
  await page.getByLabel('Total rooms').fill(String(totalRooms));
  await page.getByRole('button', { name: 'Add room type' }).click();
  await expect(page.getByRole('heading', { name: `Book a stay in ${code}` })).toBeVisible();
  await expect(page.getByTestId(`night-${day(1)}`)).toHaveText(String(totalRooms));
}

async function book(
  page: Page,
  reference: string,
  checkInOffset: number,
  nights: number,
  rooms: number,
) {
  await page.getByLabel('Guest reference').fill(reference);
  await page.getByLabel('Check-in').fill(day(checkInOffset));
  await page.getByLabel('Nights', { exact: true }).fill(String(nights));
  await page.getByLabel('Rooms', { exact: true }).fill(String(rooms));
  await page.getByRole('button', { name: 'Book stay' }).click();
}

const row = (page: Page, reference: string) => page.locator(`tr[data-reference="${reference}"]`);

test('the API refuses bad input and the page says why', async ({ page }) => {
  const id = run();
  await addRoomType(page, `UI-ERR-${id}`, 5);

  await book(page, `GUEST-${id}`, 1, 15, 1);
  await expect(page.getByRole('alert')).toContainText('at most 14 nights');

  await book(page, `GUEST-${id}`, 1, 2, 4);
  await expect(page.getByRole('alert')).toContainText('at most 3 rooms');

  await book(page, `GUEST-${id}`, -2, 2, 1);
  await expect(page.getByRole('alert')).toContainText('in the past');

  await book(page, ' ', 1, 2, 1);
  await expect(page.getByRole('alert')).toContainText(/reference is required/i);

  await book(page, `GUEST-${id}`, 1, 2, 1);
  await expect(row(page, `GUEST-${id}`).getByTestId('status')).toHaveText('Confirmed', {
    timeout: 20_000,
  });
  await book(page, `GUEST-${id}`, 1, 2, 1);
  await expect(page.getByRole('alert')).toContainText('already');
  await expect(page.locator('tbody tr[data-reference]')).toHaveCount(1);
});

test('a poison reference is rejected, gets no voucher, and every night comes back', async ({
  page,
}) => {
  const id = run();
  await addRoomType(page, `UI-POI-${id}`, 3);

  await book(page, `GUEST-POISON-${id}`, 2, 3, 2);
  const poisoned = row(page, `GUEST-POISON-${id}`);
  await expect(poisoned.getByTestId('status')).toHaveText('Rejected', { timeout: 20_000 });
  await expect(poisoned).toContainText('poison');
  await expect(poisoned.getByTestId('voucher')).toHaveText('—');
  await expect(poisoned.getByRole('button', { name: 'Cancel' })).toHaveCount(0);
  for (const n of [2, 3, 4]) await expect(page.getByTestId(`night-${day(n)}`)).toHaveText('3');
});

test('the last room goes to one stay, and a stay overlapping a sold-out night changes nothing', async ({
  page,
}) => {
  const id = run();
  await addRoomType(page, `UI-LAST-${id}`, 1);

  await book(page, `GUEST-A-${id}`, 2, 3, 1);
  await book(page, `GUEST-B-${id}`, 2, 3, 1);

  const statuses = page.locator('tbody tr[data-reference] [data-testid="status"]');
  await expect(statuses).toHaveCount(2);
  await expect
    .poll(async () => (await statuses.allTextContents()).map((s) => s.trim()).sort(), {
      timeout: 20_000,
    })
    .toEqual(['Confirmed', 'Rejected']);
  await expect(page.locator('tbody')).toContainText('Short');
  for (const n of [2, 3, 4]) await expect(page.getByTestId(`night-${day(n)}`)).toHaveText('0');

  // This stay needs nights 4, 5 and 6; night 4 is sold out, so nights 5 and 6 must stay at 1.
  await book(page, `GUEST-C-${id}`, 4, 3, 1);
  await expect(row(page, `GUEST-C-${id}`).getByTestId('status')).toHaveText('Rejected', {
    timeout: 20_000,
  });
  await expect(page.getByTestId(`night-${day(4)}`)).toHaveText('0');
  for (const n of [5, 6]) await expect(page.getByTestId(`night-${day(n)}`)).toHaveText('1');
});

test('the event log shows the Kafka steps and the cancellation keeps the voucher', async ({
  page,
}) => {
  const id = run();
  await addRoomType(page, `UI-LOG-${id}`, 4);

  await book(page, `GUEST-${id}`, 2, 2, 2);
  const mine = row(page, `GUEST-${id}`);
  await expect(mine.getByTestId('status')).toHaveText('Confirmed', { timeout: 20_000 });

  await mine.getByRole('button', { name: 'Event log' }).click();
  const log = page.getByRole('region', { name: 'Kafka event log' });
  await expect(log).toContainText('rooms.reserve', { timeout: 20_000 });
  await expect(log).toContainText('rooms.held');
  await expect(log).toContainText('voucher.issued');

  await mine.getByRole('button', { name: 'Cancel' }).click();
  await expect(mine.getByTestId('status')).toHaveText('Cancelled');
  await expect(mine.getByRole('button', { name: 'Cancel' })).toHaveCount(0);
  await expect(mine.getByTestId('voucher')).toHaveText(/VC-[0-9A-F]{12}/);
  for (const n of [2, 3]) await expect(page.getByTestId(`night-${day(n)}`)).toHaveText('4');
  await expect(log).toContainText('rooms.released', { timeout: 20_000 });
});
