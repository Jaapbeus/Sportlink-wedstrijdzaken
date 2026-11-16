// Isolated browser regression check for #1978. It starts no API, database, or shared debug service:
// every request to the configured Function API is synthetic and every other external request is blocked.
// Run BlazorAdmin on an owner-reserved isolated port, then:
//   BLAZOR_URL=http://localhost:<port> node scripts/dev/browsercheck-veldoptimalisatie-layout.mjs

import { existsSync, mkdirSync } from 'node:fs';
import os from 'node:os';
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
const baseUrl = new URL(base);
if (baseUrl.protocol !== 'http:' || !['localhost', '127.0.0.1'].includes(baseUrl.hostname)) {
  throw new Error('BLAZOR_URL must be an HTTP loopback address');
}
const apiPort = process.env.FUNCTION_API_PORT || '7094';
const artifactDir = process.env.BROWSER_ARTIFACT_DIR || path.join(os.tmpdir(), 'sportlink-1978-browser');
mkdirSync(artifactDir, { recursive: true });
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
let planSize = wedstrijden.length;

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
await context.route('**/*', async route => {
  const request = route.request();
  const url = new URL(request.url());
  if (url.hostname !== baseUrl.hostname && url.hostname !== 'localhost' && url.hostname !== '127.0.0.1') {
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
      datum: '2026-11-21', totaalWedstrijden: planSize, zonderVeld: 0, zonderTijd: 0,
      teWijzigen: Math.max(0, planSize - 1), nietInplanbaar: 0, geschatteEindTijd: '18:00',
      wedstrijden: wedstrijden.slice(0, planSize), huidigeHtml: '', optimaleHtml: ''
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
    const paneTop = el.getBoundingClientRect().top;
    const contentBottom = Math.max(...[...el.children].map(child =>
      child.getBoundingClientRect().bottom - paneTop + el.scrollTop
      + (parseFloat(getComputedStyle(child).marginBottom) || 0)));
    return { contentBottom, visible: el.clientHeight, listHeight: el.nextElementSibling.nextElementSibling.clientHeight };
  });
  ok(measuredSizing.visible <= measuredSizing.contentBottom + 8 && measuredSizing.listHeight >= 180,
    `large delayed plan caps the upper pane and keeps list space (${JSON.stringify(measuredSizing)})`);
  const desktopInitial = path.join(artifactDir, 'veld-optimalisatie-1360x900-initial.png');
  await page.screenshot({ path: desktopInitial });
  console.log(`INFO initial desktop screenshot saved to ${desktopInitial}`);

  await page.getByRole('button', { name: /Wijzigingen/ }).click();
  const filterBefore = await page.getByRole('button', { name: /Wijzigingen/ }).getAttribute('class');
  const pageScrollBefore = await page.evaluate(() => window.scrollY);
  await list.evaluate(el => { el.scrollTop = el.scrollHeight; });
  const listBottomBeforeSelection = await list.evaluate(el => el.scrollTop);
  await block(0).dblclick();
  const selectedRow = page.locator(`#wedstrijd-${wedstrijden[0].wedstrijdCode}`);
  await selectedRow.waitFor({ state: 'visible' });
  await page.waitForFunction((id) => {
    const row = document.getElementById(id);
    return row?.getAttribute('aria-current') === 'true';
  }, `wedstrijd-${wedstrijden[0].wedstrijdCode}`);
  const rowBox = await selectedRow.boundingBox();
  const afterScrollTop = await list.evaluate(el => el.scrollTop);
  const headerBox = await list.locator('thead th').first().boundingBox();
  ok((await selectedRow.textContent()).includes('Buiten filter'),
    'double-click reveals the selected out-of-filter row with an explicit indication');
  ok((await page.getByRole('button', { name: /Wijzigingen/ }).getAttribute('class')) === filterBefore,
    'active filter remains unchanged after revealing the selected row');
  ok(afterScrollTop < listBottomBeforeSelection && Math.abs(await page.evaluate(() => window.scrollY) - pageScrollBefore) < 2,
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

  for (const theme of ['light', 'dark']) {
    await page.evaluate(value => document.documentElement.setAttribute('data-bs-theme', value), theme);
    await block(0).focus();
    const marker = await block(0).evaluate(el => ({
      manual: getComputedStyle(el, '::after').borderTopStyle,
      selected: el.getAttribute('aria-pressed'),
      check: getComputedStyle(el, '::before').content,
      focus: getComputedStyle(el).outlineWidth
    }));
    ok(marker.manual === 'dashed' && marker.selected === 'true' && marker.check.includes('✓')
      && parseFloat(marker.focus) >= 3, `manual, selection and focus indicators coexist in ${theme}: ${JSON.stringify(marker)}`);
    await page.screenshot({ path: path.join(artifactDir, `selected-manual-${theme}.png`) });
  }
  await page.getByRole('button', { name: 'Optimaliseer', exact: true }).focus();
  let tabsToSkip = 0;
  while (tabsToSkip < 5) {
    await page.keyboard.press('Tab');
    tabsToSkip++;
    if (await page.locator('[data-veld-skip-splitter]').evaluate(el => el === document.activeElement)) break;
  }
  ok(tabsToSkip < 5, `keyboard shortcut reached before the 36 blocks (${tabsToSkip} Tabs)`);
  await page.keyboard.press('Enter');
  ok(await splitter.evaluate(el => el === document.activeElement), 'keyboard shortcut focuses splitter');

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

  // A new page keeps userAdjusted false. One field must shrink from the provisional
  // viewport percentage to its actual content, and a date change must fit again.
  planSize = 1;
  const smallPage = await context.newPage();
  smallPage.on('pageerror', error => appErrors.push(error.message));
  smallPage.on('console', message => { if (message.type() === 'error') appErrors.push(message.text()); });
  await smallPage.goto(`${base}/veld-optimalisatie`, { waitUntil: 'domcontentloaded', timeout: 60000 });
  await smallPage.getByText('Synthetic wedstrijd 01', { exact: true }).waitFor({ timeout: 60000 });
  const smallMeasure = () => smallPage.locator('.veldoptimalisatie-boven-paneel').evaluate(el => {
    const paneTop = el.getBoundingClientRect().top;
    const contentBottom = Math.max(...[...el.children].map(child =>
      child.getBoundingClientRect().bottom - paneTop + el.scrollTop
      + (parseFloat(getComputedStyle(child).marginBottom) || 0)));
    return { contentBottom, visible: el.clientHeight, listHeight: el.nextElementSibling.nextElementSibling.clientHeight };
  });
  await smallPage.setViewportSize({ width: 1360, height: 1200 });
  await smallPage.waitForTimeout(100);
  const smallInitial = await smallMeasure();
  ok(smallInitial.visible >= smallInitial.contentBottom - 2
    && smallInitial.visible <= smallInitial.contentBottom + 12
    && smallInitial.listHeight >= 180,
  `one-field plan shrinks to measured content (${JSON.stringify(smallInitial)})`);
  planSize = 3;
  const date = smallPage.locator('input[type="date"]');
  await date.fill('2026-11-28');
  await date.dispatchEvent('change');
  await smallPage.getByText('Synthetic wedstrijd 01', { exact: true }).waitFor({ timeout: 60000 });
  await smallPage.waitForTimeout(100);
  const afterDateChange = await smallMeasure();
  ok(afterDateChange.contentBottom > smallInitial.contentBottom + 100
    && afterDateChange.visible > smallInitial.visible + 100
    && afterDateChange.visible >= afterDateChange.contentBottom - 2
    && afterDateChange.visible <= afterDateChange.contentBottom + 12,
  `date-change plan remount refits without user resize (${JSON.stringify(afterDateChange)})`);
  const focusCheck = async (tabName) => {
    await smallPage.getByRole('button', { name: new RegExp(tabName) }).click();
    const firstBlock = smallPage.locator('.gantt-blok').first();
    await firstBlock.focus();
    await smallPage.keyboard.press('Tab');
    await smallPage.keyboard.press('Shift+Tab');
    return firstBlock.evaluate(el => ({
      focused: el === document.activeElement,
      outline: getComputedStyle(el).outlineStyle,
      width: getComputedStyle(el).outlineWidth
    }));
  };
  const currentFocus = await focusCheck('Huidig');
  const optimalFocus = await focusCheck('Optimaal');
  ok(currentFocus.focused && currentFocus.outline !== 'none' && parseFloat(currentFocus.width) >= 2,
    `Huidig Gantt block has visible keyboard focus (${JSON.stringify(currentFocus)})`);
  ok(optimalFocus.focused && optimalFocus.outline !== 'none' && parseFloat(optimalFocus.width) >= 2,
    `Optimaal Gantt block has visible keyboard focus (${JSON.stringify(optimalFocus)})`);
  await smallPage.close();

  await page.setViewportSize({ width: 360, height: 400 });
  const viewportHeight = () => layout.evaluate(el => ({
    cssHeight: parseFloat(getComputedStyle(el).getPropertyValue('--veld-layout-height')),
    documentTop: el.getBoundingClientRect().top + window.scrollY,
    scrollY: window.scrollY,
    viewport: window.innerHeight
  }));
  const beforeScrollResize = await viewportHeight();
  await page.evaluate(() => window.scrollTo(0, 10000));
  await page.evaluate(() => window.dispatchEvent(new Event('resize')));
  await page.waitForTimeout(100);
  const afterScrollResize = await viewportHeight();
  ok(afterScrollResize.scrollY > 0
    && Math.abs(afterScrollResize.cssHeight - beforeScrollResize.cssHeight) <= 2,
  `scroll position does not alter viewport sizing (${JSON.stringify({ beforeScrollResize, afterScrollResize })})`);
  const splitterBox = await splitter.boundingBox();
  ok(splitterBox && splitterBox.height >= 24,
    `mobile splitter has at least 24px pointer height (${splitterBox?.height})`);
  await splitter.focus();
  await page.keyboard.press('Tab');
  await page.keyboard.press('Shift+Tab');
  const focusedSplitter = await splitter.evaluate(el => getComputedStyle(el).outlineStyle);
  ok(focusedSplitter !== 'none', `splitter has visible keyboard focus (${focusedSplitter})`);

  await page.setViewportSize({ width: 360, height: 700 });
  await page.waitForTimeout(100);
  const beforeBanner = await viewportHeight();
  await page.evaluate(() => {
    const banner = document.createElement('div');
    banner.id = 'synthetic-layout-banner';
    banner.style.height = '40px';
    document.querySelector('.veldoptimalisatie-layout').before(banner);
  });
  await page.waitForTimeout(100);
  const afterBanner = await viewportHeight();
  ok(afterBanner.documentTop > beforeBanner.documentTop + 35
    && afterBanner.cssHeight < beforeBanner.cssHeight - 35,
  `above-layout banner recalculates available height (${JSON.stringify(afterBanner)})`);
  await page.evaluate(() => document.getElementById('synthetic-layout-banner').remove());

  // Null codes must remain inert even when the component has a selection callback.
  planSize = 36;
  wedstrijden[0].wedstrijdCode = null;
  const nullPage = await context.newPage();
  await nullPage.goto(`${base}/veld-optimalisatie`, { waitUntil: 'domcontentloaded' });
  await nullPage.getByText('Synthetic wedstrijd 01', { exact: true }).waitFor({ timeout: 60000 });
  const nullBlock = nullPage.locator('.gantt-blok').filter({ hasText: 'Synthetic wedstrijd 01' }).first();
  ok(await nullBlock.getAttribute('role') === null && await nullBlock.getAttribute('tabindex') === null
    && await nullBlock.getAttribute('aria-pressed') === null, 'null-code block has no interactive semantics or tab stop');
  await nullPage.close();
  wedstrijden[0].wedstrijdCode = 1978000;

  for (const match of wedstrijden) {
    match.heeftVeld = false; match.heeftTijd = false;
    match.huidigeVeld = null; match.huidigeTijd = null;
  }
  const emptyPage = await context.newPage();
  await emptyPage.setViewportSize({ width: 1360, height: 1200 });
  await emptyPage.goto(`${base}/veld-optimalisatie`, { waitUntil: 'domcontentloaded' });
  await emptyPage.getByText('Synthetic wedstrijd 01', { exact: true }).waitFor({ timeout: 60000 });
  const emptyTop = emptyPage.locator('.veldoptimalisatie-boven-paneel');
  const measureEmpty = () => emptyTop.evaluate(el => ({ height: el.clientHeight, content: el.scrollHeight,
    blocks: el.querySelectorAll('.gantt-blok').length }));
  const emptyCurrent = await measureEmpty();
  await emptyPage.getByRole('button', { name: /Optimaal/ }).click();
  await emptyPage.waitForTimeout(100);
  const fullOptimal = await measureEmpty();
  ok(emptyCurrent.blocks === 0 && fullOptimal.blocks === 36 && fullOptimal.height > emptyCurrent.height + 100
    && fullOptimal.height >= fullOptimal.content - 2, `empty Huidig to Optimaal refits (${JSON.stringify({emptyCurrent, fullOptimal})})`);
  await emptyPage.getByRole('button', { name: /Huidig/ }).click();
  await emptyPage.waitForTimeout(100);
  const emptyAgain = await measureEmpty();
  ok(Math.abs(emptyAgain.height - emptyCurrent.height) <= 2, 'Optimaal to empty Huidig shrinks again');
  const emptySplitter = emptyPage.locator('.veldoptimalisatie-splitter');
  await emptySplitter.focus();
  await emptySplitter.press('ArrowUp');
  const manualHeight = (await measureEmpty()).height;
  await emptyPage.getByRole('button', { name: /Optimaal/ }).click();
  await emptyPage.waitForTimeout(100);
  ok((await measureEmpty()).height === manualHeight, 'content changes preserve explicit user pane resize');
  await emptyPage.close();

  ok(blockedExternal.length === 0, 'no external requests were attempted');
  ok(apiCalls.some(call => call.includes('/api/planner/auto-plan')), 'the page used intercepted synthetic planner data');
  ok(appErrors.length === 0, 'browser reports no application or console errors' + (appErrors.length ? `: ${appErrors.join(' | ')}` : ''));
} finally {
  await context.close();
  await browser.close();
}

if (failures) process.exit(1);
