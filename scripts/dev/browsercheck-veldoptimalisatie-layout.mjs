// Isolated browser regression check for #1978. It starts no API, database, or shared debug service:
// every request to the configured Function API is synthetic and every other external request is blocked.
// Run BlazorAdmin on an owner-reserved isolated port, then:
//   BLAZOR_URL=http://localhost:<port> node scripts/dev/browsercheck-veldoptimalisatie-layout.mjs

import { existsSync, mkdirSync } from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

async function laadPlaywright() {
  try { return await import('playwright'); }
  catch (e) {
    if (e.code !== 'ERR_MODULE_NOT_FOUND') throw e;
    const lokal = path.join(process.cwd(), 'node_modules', 'playwright', 'index.mjs');
    if (!existsSync(lokal)) throw new Error(`Playwright not found from ${process.cwd()}`);
    return await import(pathToFileURL(lokal).href);
  }
}

const { chromium } = await laadPlaywright();
const base = process.env.BLAZOR_URL || 'http://localhost:15078';
const apiPort = process.env.FUNCTION_API_PORT || '7094';
const results = [];
let failures = 0;
const ok = (condition, message) => {
  const line = `${condition ? 'OK   ' : 'FAIL '} ${message}`;
  results.push(line);
  console.log(line);
  if (!condition) failures++;
};
const time = index => `${String(9 + Math.floor(index / 4)).padStart(2, '0')}:${String((index % 4) * 15).padStart(2, '0')}`;
const wedstrijden = Array.from({ length: 36 }, (_, i) => ({
  wedstrijdCode: 1978000 + i,
  wedstrijd: `Synthetic wedstrijd ${String(i + 1).padStart(2, '0')}`,
  teamNaam: `Synthetic team ${String(i + 1).padStart(2, '0')}`,
  leeftijdsCategorie: 'JO12',
  competitiesoort: 'competitie',
  duurMinuten: 90,
  veldafmeting: 1,
  huidigeVeld: `Kunstgras ${1 + (i % 3)}`,
  huidigeTijd: time(i),
  heeftVeld: true,
  heeftTijd: true,
  optimaalVeldNummer: 1 + (i % 3),
  optimaalVeldNaam: `Kunstgras ${1 + (i % 3)}`,
  optimaalVeld: `Kunstgras ${1 + (i % 3)}`,
  optimaalTijd: time(i),
  status: i === 0 ? 'ongewijzigd' : 'wijziging',
  voorkeurTijd: '10:00',
  voorkeurAfwijkingMinuten: 0,
  voorkeurBron: 'leeftijd',
  voorkeurStatus: 'op-tijd'
}));

const browser = await chromium.launch({
  headless: true,
  ...(process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE
    ? { executablePath: process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE }
    : {})
});
const context = await browser.newContext({ viewport: { width: 1360, height: 900 } });
const page = await context.newPage();
const appErrors = [];
const apiCalls = [];
const blockedExternal = [];
page.on('pageerror', error => appErrors.push(error.message));
page.on('console', message => { if (message.type() === 'error') appErrors.push(message.text()); });
await page.addInitScript(() => localStorage.setItem('selectedClubCode', 'ALLSTARS'));
await page.route('**/*', async route => {
  const request = route.request();
  const url = new URL(request.url());
  if (url.hostname !== 'localhost') {
    blockedExternal.push(url.origin);
    return route.abort();
  }
  if (url.port !== apiPort) return route.continue();

  apiCalls.push(`${request.method()} ${url.pathname}`);
  const json = body => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
  if (url.pathname === '/api/health') return json({ status: 'healthy', database: 'online', version: 'synthetic' });
  if (url.pathname === '/api/beheer/clubs') return json([{ clubCode: 'ALLSTARS', clubName: 'ALLSTARS testmodus', syncEnabled: true }]);
  if (url.pathname === '/api/beheer/settings') return json({ sportlinkExtensionEnabled: false });
  if (url.pathname === '/api/planner/pdf-export') return json({ pdfExportIngeschakeld: false });
  if (url.pathname === '/api/planner/auto-plan' && request.method() === 'POST') {
    await new Promise(resolve => setTimeout(resolve, 650));
    return json({
      datum: '2026-11-21', totaalWedstrijden: wedstrijden.length, zonderVeld: 0, zonderTijd: 0,
      teWijzigen: wedstrijden.length - 1, nietInplanbaar: 0, geschatteEindTijd: '18:00',
      wedstrijden, huidigeHtml: '', optimaleHtml: ''
    });
  }
  return json([]);
});

try {
  await page.goto(`${base}/veld-optimalisatie`, { waitUntil: 'domcontentloaded', timeout: 60000 });
  await page.getByText('Synthetic wedstrijd 01', { exact: true }).waitFor({ timeout: 60000 });
  const layout = page.locator('.veldoptimalisatie-layout');
  const top = page.locator('.veldoptimalisatie-boven-paneel');
  const list = page.locator('.veldoptimalisatie-tabel-scroll');
  const splitter = page.locator('.veldoptimalisatie-splitter');
  const block = (i = 0) => page.locator(`.gantt-blok[data-wedstrijd-code="${wedstrijden[i].wedstrijdCode}"]`);

  const layoutBox = await layout.boundingBox();
  const topBox = await top.boundingBox();
  const listBox = await list.boundingBox();
  ok(layoutBox && layoutBox.height > 400 && topBox && listBox && topBox.y < listBox.y,
    'viewport is divided into visible field-planning and match-list panes');
  ok(await list.locator('tbody tr').count() === wedstrijden.length,
    'synthetic rows render in the independently scrollable list');
  const measuredSizing = await top.evaluate(el => {
    const layout = el.closest('.veldoptimalisatie-layout');
    const separator = layout.querySelector('.veldoptimalisatie-splitter');
    const max = Math.max(280, layout.clientHeight - separator.offsetHeight) - 180;
    return { content: el.scrollHeight, visible: el.clientHeight, max };
  });
  ok(Math.abs(measuredSizing.visible - Math.min(measuredSizing.content, measuredSizing.max)) <= 2,
    `delayed plan response sizes the initial upper pane to measured content within the list minimum (${JSON.stringify(measuredSizing)})`);
  const artifactDir = process.env.BROWSER_ARTIFACT_DIR || '/private/tmp/sportlink-1978-browser-20261116';
  mkdirSync(artifactDir, { recursive: true });
  const desktopInitial = path.join(artifactDir, 'veld-optimalisatie-1360x900-initial.png');
  await page.screenshot({ path: desktopInitial });
  console.log(`INFO initial desktop screenshot saved to ${desktopInitial}`);

  await page.getByRole('button', { name: /Wijzigingen/ }).click();
  const filterBefore = await page.getByRole('button', { name: /Wijzigingen/ }).getAttribute('class');
  const listTopBefore = await list.evaluate(el => el.scrollTop);
  const pageScrollBefore = await page.evaluate(() => window.scrollY);
  await list.evaluate(el => { el.scrollTop = el.scrollHeight; });
  await block(0).dblclick();
  const selectedRow = page.locator(`#wedstrijd-${wedstrijden[0].wedstrijdCode}`);
  await selectedRow.waitFor({ state: 'visible' });
  await page.waitForFunction((id) => {
    const row = document.getElementById(id);
    return row?.getAttribute('aria-current') === 'true';
  }, `wedstrijd-${wedstrijden[0].wedstrijdCode}`);
  const rowBox = await selectedRow.boundingBox();
  const afterScrollTop = await list.evaluate(el => el.scrollTop);
  const headerBox = await list.locator('thead').boundingBox();
  ok((await selectedRow.textContent()).includes('Buiten filter'),
    'double-click reveals the selected out-of-filter row with an explicit indication');
  ok((await page.getByRole('button', { name: /Wijzigingen/ }).getAttribute('class')) === filterBefore,
    'active filter remains unchanged after revealing the selected row');
  ok(afterScrollTop !== listTopBefore && Math.abs(await page.evaluate(() => window.scrollY) - pageScrollBefore) < 2,
    'selection changes the table scroll position without moving the page');
  ok(rowBox && headerBox && rowBox.y >= headerBox.y + headerBox.height - 1,
    'selected row is positioned below the sticky table header');

  await block(1).hover();
  await page.waitForTimeout(100);
  const hoveredClass = await page.locator(`#wedstrijd-${wedstrijden[1].wedstrijdCode}`).getAttribute('class');
  ok((hoveredClass || '').includes('gantt-rij-hover'),
    `Gantt hover still highlights the corresponding table row (class=${hoveredClass})`);

  await page.getByRole('button', { name: /Optimaal/ }).click();
  const optimalBlock = block(0);
  ok(await optimalBlock.getAttribute('draggable') === 'true', 'optimal Gantt blocks remain draggable');
  await optimalBlock.focus();
  await optimalBlock.press('Enter');
  ok((await page.locator(`#wedstrijd-${wedstrijden[0].wedstrijdCode}`).getAttribute('aria-current')) === 'true',
    'keyboard activation selects a Gantt match in the Optimaal tab');

  await block(1).dblclick();
  ok((await page.locator(`#wedstrijd-${wedstrijden[1].wedstrijdCode}`).getAttribute('aria-current')) === 'true'
    && await page.locator(`#wedstrijd-${wedstrijden[0].wedstrijdCode}`).count() === 0,
  'selection changes to another match and stays synchronized across the tab switch');
  const pageScrollBeforeSpace = await page.evaluate(() => window.scrollY);
  await block(2).focus();
  await block(2).press('Space');
  ok((await page.locator(`#wedstrijd-${wedstrijden[2].wedstrijdCode}`).getAttribute('aria-current')) === 'true'
    && Math.abs(await page.evaluate(() => window.scrollY) - pageScrollBeforeSpace) < 2,
  'Space activates the focused block without scrolling the page');
  await block(2).focus();
  await block(2).press('Tab');
  ok(await block(2).evaluate(el => el !== document.activeElement), 'Tab leaves a focused Gantt block');

  await block(0).dblclick();
  const rowBeforeDrop = (await page.locator(`#wedstrijd-${wedstrijden[0].wedstrijdCode} td`).nth(1).textContent()).trim();
  const targetTrack = page.locator('.gantt-veld-track').nth(1);
  await block(0).dragTo(targetTrack, { targetPosition: { x: 160, y: 36 } });
  await page.waitForFunction(({ id, before }) => {
    const row = document.getElementById(id);
    return row && row.cells[1].textContent.trim() !== before;
  }, { id: `wedstrijd-${wedstrijden[0].wedstrijdCode}`, before: rowBeforeDrop }, { timeout: 5000 }).catch(() => {});
  const rowAfterDrop = (await page.locator(`#wedstrijd-${wedstrijden[0].wedstrijdCode} td`).nth(1).textContent()).trim();
  ok(rowAfterDrop !== rowBeforeDrop && rowAfterDrop.includes('Kunstgras 2'),
    `native drag/drop still updates the synthetic optimal field (${rowBeforeDrop} -> ${rowAfterDrop})`);
  ok(!apiCalls.some(call => call.includes('/auto-plan/toepassen')),
    'drag/drop changed only the local plan and made no apply/write API request');

  const beforeResize = Number(await splitter.getAttribute('aria-valuenow'));
  await splitter.focus();
  await splitter.press('ArrowUp');
  const afterKeyboardResize = Number(await splitter.getAttribute('aria-valuenow'));
  ok(afterKeyboardResize < beforeResize, 'ArrowUp resizes the upper pane from the keyboard');
  await splitter.press('Tab');
  const tabFocus = await page.evaluate(() => `${document.activeElement?.tagName}.${document.activeElement?.className}`);
  ok(await splitter.evaluate(el => el !== document.activeElement
    && document.activeElement?.closest('.veldoptimalisatie-wedstrijden-paneel') !== null),
  `Tab leaves the splitter and reaches the match-list controls (focus=${tabFocus})`);

  const beforePointerResize = Number(await splitter.getAttribute('aria-valuenow'));
  const splitBox = await splitter.boundingBox();
  if (splitBox) {
    await page.mouse.move(splitBox.x + splitBox.width / 2, splitBox.y + splitBox.height / 2);
    await page.mouse.down();
    await page.mouse.move(splitBox.x + splitBox.width / 2, splitBox.y - 54, { steps: 4 });
    await page.mouse.up();
  }
  ok(Number(await splitter.getAttribute('aria-valuenow')) !== beforePointerResize,
    'pointer resizing adjusts the pane split');

  await top.evaluate(el => { el.scrollTop = 0; });
  const desktopScreenshot = path.join(artifactDir, 'veld-optimalisatie-1360x900-after-interactions.png');
  await page.screenshot({ path: desktopScreenshot });
  console.log(`INFO desktop screenshot saved to ${desktopScreenshot}`);

  await page.setViewportSize({ width: 360, height: 640 });
  await page.waitForTimeout(100);
  const narrowLayout = await layout.boundingBox();
  const narrowList = await list.boundingBox();
  const narrowDetails = await layout.evaluate(el => ({
    rows: getComputedStyle(el).gridTemplateRows,
    topHeight: getComputedStyle(el).getPropertyValue('--veld-boven-hoogte'),
    top: el.querySelector('.veldoptimalisatie-boven-paneel').getBoundingClientRect().toJSON(),
    splitter: el.querySelector('.veldoptimalisatie-splitter').getBoundingClientRect().toJSON(),
    bottom: el.querySelector('.veldoptimalisatie-wedstrijden-paneel').getBoundingClientRect().toJSON(),
    descendants: [...el.querySelectorAll('.veldoptimalisatie-wedstrijden-paneel .card, .veldoptimalisatie-wedstrijden-paneel .card-body, .veldoptimalisatie-tabel-scroll')]
      .map(node => `${node.className}:${JSON.stringify(node.getBoundingClientRect().toJSON())}:${getComputedStyle(node).display}/${getComputedStyle(node).minHeight}/${getComputedStyle(node).overflow}`)
  }));
  console.log(`INFO narrow panes layout=${JSON.stringify(narrowLayout)} list=${JSON.stringify(narrowList)} details=${JSON.stringify(narrowDetails)}`);
  ok(narrowLayout && narrowLayout.width <= 360 && narrowList && narrowList.height > 80,
    'narrow viewport retains an accessible match-list pane');
  await top.evaluate(el => { el.scrollTop = 0; });
  const screenshotPath = path.join(artifactDir, 'veld-optimalisatie-360x640.png');
  await page.screenshot({ path: screenshotPath });
  console.log(`INFO narrow viewport screenshot saved to ${screenshotPath}`);
  await page.locator('.veldoptimalisatie-boven-paneel').evaluate(el => { el.scrollTop = 0; });
  console.log(`INFO narrow optimize button visible=${await page.getByRole('button', { name: 'Optimaliseer' }).isVisible()} topScroll=${await top.evaluate(el => el.scrollTop)} topHeight=${await top.evaluate(el => el.clientHeight)}`);
  const heightBeforePlanRefresh = await top.evaluate(el => getComputedStyle(el.closest('.veldoptimalisatie-layout')).getPropertyValue('--veld-boven-hoogte').trim());
  await page.getByRole('button', { name: 'Optimaliseer' }).click();
  ok(await top.locator('button').filter({ hasText: /Optimaliseer|Bezig/ }).isVisible(),
    'top-pane controls remain reachable at a narrow viewport');
  await page.waitForFunction(() => [...document.querySelectorAll('button')]
    .some(button => button.textContent.trim() === 'Optimaliseer' && !button.disabled), { timeout: 60000 });
  await page.getByText('Synthetic wedstrijd 01', { exact: true }).waitFor({ timeout: 60000 });
  const heightAfterPlanRefresh = await top.evaluate(el => getComputedStyle(el.closest('.veldoptimalisatie-layout')).getPropertyValue('--veld-boven-hoogte').trim());
  ok(heightAfterPlanRefresh === heightBeforePlanRefresh,
    'a subsequent plan render preserves the user-adjusted pane height');

  ok(blockedExternal.length === 0, 'no external requests were attempted');
  ok(apiCalls.some(call => call.includes('/api/planner/auto-plan')), 'the page used intercepted synthetic planner data');
  ok(appErrors.length === 0, 'browser reports no application or console errors' + (appErrors.length ? `: ${appErrors.join(' | ')}` : ''));
} finally {
  await context.close();
  await browser.close();
}

if (failures) process.exit(1);
