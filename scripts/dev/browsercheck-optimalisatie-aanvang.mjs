// #1979 browsercontrole voor de clubbrede starttijd op App instellingen.
// Draait tegen een eigen Development-BlazorAdmin-instantie; alle API-verzoeken naar :7094 zijn mocks.
// Extern verkeer wordt afgebroken. Playwright wordt vanaf de huidige werkmap geladen.
import { existsSync, mkdirSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

async function laadPlaywright() {
  try { return await import('playwright'); }
  catch (e) {
    if (e.code !== 'ERR_MODULE_NOT_FOUND') throw e;
    const lokaal = path.join(process.cwd(), 'node_modules', 'playwright', 'index.mjs');
    if (!existsSync(lokaal)) throw new Error(`Playwright niet gevonden vanuit ${process.cwd()}`);
    return await import(pathToFileURL(lokaal).href);
  }
}

const { chromium } = await laadPlaywright();
const base = process.env.BLAZOR_URL || 'http://localhost:15079';
const baseUrl = new URL(base);
if (!['localhost', '127.0.0.1'].includes(baseUrl.hostname))
  throw new Error('BLAZOR_URL moet naar de eigen lokale Blazor-instantie wijzen.');
const artifactDir = process.env.BROWSER_ARTIFACT_DIR || path.join(os.tmpdir(), 'sportlink-1979-browsercheck');
if (!path.isAbsolute(artifactDir)) throw new Error('BROWSER_ARTIFACT_DIR moet een absoluut pad zijn.');
mkdirSync(artifactDir, { recursive: true });
const state = { start: '08:00', updates: [] };
const errors = [];
const browser = await chromium.launch({ executablePath: process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE || undefined });
const page = await browser.newPage({ viewport: { width: 1280, height: 900 } });
page.on('pageerror', e => errors.push(e.message));
page.on('console', m => { if (m.type() === 'error') errors.push(m.text()); });

await page.route('**/*', async route => {
  const url = new URL(route.request().url());
  if (!['localhost', '127.0.0.1'].includes(url.hostname)) return route.abort();
  if (url.port !== '7094') return route.continue();
  const json = (body, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });
  const path = url.pathname;
  if (path === '/api/beheer/clubs') return json([{ ClubCode: 'TEST', ClubName: 'Synthetische testclub', SyncEnabled: false }]);
  if (path === '/api/beheer/settings' && route.request().method() === 'GET')
    return json({ ClubName: 'Synthetische testclub', ClubCode: 'TEST', SeasonStartMonth: 7, OptimalisatieAanvang: state.start });
  if (path === '/api/beheer/settings' && route.request().method() === 'PUT') {
    const request = route.request().postDataJSON();
    const fields = request.Velden ?? request.velden ?? {};
    state.updates.push(fields);
    const value = fields.OptimalisatieAanvang ?? fields.optimalisatieAanvang;
    state.start = value === '' ? null : value;
    return json({ gewijzigdeVelden: Object.keys(fields) });
  }
  if (path === '/api/beheer/sync/status') return json({ status: 'ok', fetchSchedule: '0 0 4 * * *' });
  if (path === '/api/beheer/email-log') return json({ items: [], statusCounts: {} });
  if (path === '/api/beheer/uitgesloten-emails') return json([]);
  if (path === '/api/health') return json({ status: 'healthy', database: 'online', version: '3.19.1.0' });
  return json([]);
});

const checks = [];
const check = (condition, label) => {
  checks.push({ condition: !!condition, label });
  console.log(`${condition ? 'OK' : 'FAIL'} ${label}`);
};

try {
  await page.goto(`${base}/instellingen`);
  await page.getByRole('heading', { name: 'App instellingen' }).waitFor();
  const input = page.getByLabel('Optimalisatie aanvang', { exact: true });
  await input.waitFor();
  check(await input.inputValue() === '08:00', 'Opgeslagen clubtijd wordt getoond bij laden');
  check((await page.locator('body').textContent()).includes('Teamvoorkeuren later dan deze tijd blijven leidend.'),
    'Instellingentekst beschrijft clubbrede starttijd en latere teamvoorkeuren');

  await input.fill('09:30');
  await page.reload();
  await page.getByRole('heading', { name: 'App instellingen' }).waitFor();
  check(await page.getByLabel('Optimalisatie aanvang', { exact: true }).inputValue() === '08:00',
    'Niet opgeslagen invoer verdwijnt na herladen');

  await page.getByLabel('Optimalisatie aanvang', { exact: true }).fill('10:15');
  await page.locator('#settings-form').getByRole('button', { name: 'Opslaan' }).click();
  await page.getByText('Instellingen opgeslagen.').waitFor();
  check(state.updates.at(-1)?.OptimalisatieAanvang === '10:15', 'Opslaan verstuurt de clubtijd als HH:mm');

  await page.getByLabel('Optimalisatie aanvang', { exact: true }).fill('');
  await page.locator('#settings-form').getByRole('button', { name: 'Opslaan' }).click();
  await page.getByText('Instellingen opgeslagen.').waitFor();
  check(state.updates.at(-1)?.OptimalisatieAanvang === null, 'Leeg opslaan wist de nullable instelling');
  check(await page.getByLabel('Optimalisatie aanvang', { exact: true }).inputValue() === '',
    'Na wissen blijft de instelling leeg');
  check(errors.length === 0, `Geen page- of console-errors${errors.length ? `: ${errors.join(' | ')}` : ''}`);
  await page.screenshot({ path: path.join(artifactDir, 'optimalisatie-aanvang-instellingen.png'), fullPage: true });
} finally {
  await browser.close();
}

const failed = checks.filter(c => !c.condition);
console.log(`\n${checks.length - failed.length}/${checks.length} geslaagd`);
process.exitCode = failed.length ? 1 : 0;
