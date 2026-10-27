import { APIRequestContext, Page, expect } from '@playwright/test';

export const ADMIN = {
  email: process.env.ADMIN_EMAIL ?? 'admin@hotel.local',
  password: process.env.ADMIN_PASSWORD ?? 'HotelAdmin2026',
};

export const CUSTOMER_PASSWORD = 'Browser-Passw0rd1';

export async function signIn(page: Page, account: { email: string; password: string }) {
  await page.goto('/');
  await page.getByLabel('Email').fill(account.email);
  await page.getByLabel('Password').fill(account.password);
  await page.getByRole('button', { name: 'Sign in' }).click();
  await expect(page.getByTestId('whoami')).toContainText(account.email);
}

/** Registers a fresh customer through the form and leaves the page signed in as them. */
export async function registerCustomer(page: Page): Promise<string> {
  const email = `e2e-${Date.now().toString(36)}${Math.floor(Math.random() * 1000)}@example.com`;
  await page.goto('/');
  await page.getByRole('button', { name: 'Need an account? Register' }).click();
  await page.getByLabel('Email').fill(email);
  await page.getByLabel('Password').fill(CUSTOMER_PASSWORD);
  await page.getByRole('button', { name: 'Create account' }).click();
  await expect(page.getByTestId('whoami')).toContainText(email);
  return email;
}

/** A bearer token for direct API calls from a spec. */
export async function adminToken(request: APIRequestContext): Promise<string> {
  const response = await request.post('/api/auth/login', { data: ADMIN });
  expect(response.ok()).toBeTruthy();
  return (await response.json()).accessToken;
}

/** The body of POST /api/room-types: one place to adapt per system. */
export const roomTypeBody = (code: string, rateCents: number) => ({
  code,
  name: 'Harbor Auth',
  city: 'Lisbon',
  totalRooms: '5',
  rateCents: 5000,
  rateCents,
});

/** Fills the taking form and submits it. A system whose form has other fields changes this one function. */
export async function submitGuest(page: Page, reference: string, nights: number) {
  await page.getByLabel('Guest reference').fill(reference);
  await page
    .getByLabel('Check-in')
    .fill(new Date(Date.now() + 2 * 86_400_000).toISOString().slice(0, 10));
  await page.getByLabel('Nights', { exact: true }).fill(String(nights));
  await page.getByLabel('Rooms', { exact: true }).fill('1');
  await page.getByRole('button', { name: 'Book stay' }).click();
}
