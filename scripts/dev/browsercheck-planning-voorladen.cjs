// #1994: eigen Development-Blazor, uitsluitend synthetische onderschepte API-antwoorden.
// NODE_PATH=<map met playwright> node scripts/dev/browsercheck-planning-voorladen.cjs http://localhost:55294
const { chromium } = require('playwright');
const assert = require('node:assert/strict');
const base = process.argv[2];
if (!base || new URL(base).hostname !== 'localhost') throw new Error('Een eigen localhost-Blazor is verplicht.');
(async () => {
  const browser = await chromium.launch({ headless: true, executablePath: process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH });
  const context = await browser.newContext();
  await context.addInitScript(() => localStorage.setItem('selectedClubCode', 'ALLSTARS'));
  const page = await context.newPage();
  const errors = [], calls = new Map();
  let holdDate, release, refreshStarted, refreshed = false, mutated = false;
  const next = new Date(); next.setDate(next.getDate() + ((6 - next.getDay() + 7) % 7));
  const dateKey = d => `${d.getFullYear()}-${String(d.getMonth()+1).padStart(2,'0')}-${String(d.getDate()).padStart(2,'0')}`;
  if (next.getDay() === new Date().getDay() && new Date().getDay() === 6) next.setDate(next.getDate()+7);
  const first = dateKey(next); next.setDate(next.getDate()+7); const second = dateKey(next);
  const payload = (datum, newer = false) => ({
    sportlink: { geraadpleegd: true, reden: 'Ok', geraadpleegdOpUtc: new Date().toISOString() },
    wedstrijden: [{ wedstrijdCode: 1994001, wedstrijd: `${newer ? 'Bijgewerkt' : 'Voorbereid'} ${datum}`,
      teamNaam: 'AllStars', aanvangsTijd: '10:00', veld: mutated ? 'veld 2' : 'veld 1', duurMinuten: 75, veldafmeting: 1,
      tegenstander: 'Tegenstander', bron: 'Sportlink' }]
  });
  page.on('pageerror', e => errors.push(e.message));
  page.on('console', m => { if (m.type() === 'error') errors.push(m.text()); });
  await page.route('**/*', async route => {
    const url = new URL(route.request().url());
    if (!['localhost','127.0.0.1'].includes(url.hostname)) return route.abort();
    if (url.port !== '7094') return route.continue();
    const json = body => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
    if (url.pathname === '/api/health') return json({ status: 'healthy', database: 'online', version: 'synthetisch' });
    if (url.pathname === '/api/planner/veldbezetting') {
      const datum = url.searchParams.get('datum'); calls.set(datum, (calls.get(datum) || 0)+1);
      if (datum === holdDate) { refreshStarted?.(); await new Promise(r => { release = r; }); return json(payload(datum, refreshed)); }
      await new Promise(r => setTimeout(r, 150));
      return json(payload(datum));
    }
    if (url.pathname === '/api/sportlink/match/1994001/field') { mutated = true; return json({ isSuccess: true, isDryRun: false, isForcedDryRun: false }); }
    if (url.pathname === '/api/sportlink/match/1994001') return json({ publicMatchId: 'synthetisch', isEditFieldAllowed: true, veldFeatureToegestaan: true, fieldId: '1', fieldSize: '1/1' });
    if (url.pathname.endsWith('/clubs')) return json([{ clubCode: 'ALLSTARS', clubName: 'AllStars', syncEnabled: true }]);
    if (url.pathname.endsWith('/menu-toegang')) return json({ sleutels: ['planning'], sportlinkExtensionEnabled: true });
    if (url.pathname.endsWith('/settings')) return json({ clubName: 'AllStars', sportlinkExtensionEnabled: true });
    if (url.pathname.endsWith('/pdf-export')) return json({ pdfExportIngeschakeld: false });
    return json([]);
  });
  try {
    await page.goto(base + '/planning');
    await page.getByText(`Voorbereid ${first}`).first().waitFor({ timeout: 60000 });
    // Alle vier antwoorden moeten verwerkt zijn voor de datumwissel.
    await page.waitForFunction(() => document.querySelector('input[type=date]'));
    for (let n = 0; n < 100 && calls.size < 4; n++) await new Promise(r => setTimeout(r, 50));
    assert.equal(calls.size, 4, 'vier zaterdagen opgehaald');
    await new Promise(r => setTimeout(r, 250));
    holdDate = second;
    const started = new Promise(r => { refreshStarted = r; });
    const begin = performance.now();
    await page.locator('input[type=date]').fill(second);
    await page.locator('input[type=date]').dispatchEvent('change');
    await Promise.race([started, new Promise((_, reject) => setTimeout(() => reject(new Error("Hercontrole niet gestart")), 10000))]);
    await page.getByText(`Voorbereid ${second}`).first().waitFor();
    assert(await page.getByText('Actuele stand controleren…').isVisible());
    console.log(`Vooraf geladen datum zichtbaar vóór vrijgave netwerk: ${Math.round(performance.now()-begin)} ms`);
    refreshed = true; release();
    await page.getByText(`Bijgewerkt ${second}`).first().waitFor();
    assert.equal(calls.get(second), 2, 'één voorlaadactie en één actualiteitscontrole');
    holdDate = undefined;
    await page.getByRole('button', { name: 'Toon', exact: true }).click();
    await page.getByRole('button', { name: 'Veld wijzigen', exact: true }).click();
    await page.getByText('Veld gewijzigd in Sportlink Club.', { exact: true }).waitFor();
    await page.getByRole('cell', { name: 'veld 2', exact: true }).waitFor();
    assert.equal(mutated, true);
    assert.equal(calls.get(second), 3, 'veldmutatie haalt dezelfde dag opnieuw op');
    await page.goto(base + '/instellingen');
    await page.locator('h1').waitFor();
    assert.equal(await page.evaluate(() => typeof window.Blazor), 'object');
    assert.equal(await page.getByText('An unhandled error has occurred').isVisible(), false);
    assert.deepEqual(errors, []);
    console.log('Geslaagd: vooruitladen, onmiddellijk tonen, actualiteitscontrole, veldmutatie met zichtbare bevestiging, instellingen en browserconsole.');
  } catch (e) { console.error("Browserdiagnose", { errors, aanvragen: [...calls.entries()], pagina: (await page.locator("body").innerText()).slice(0, 1200) }); throw e; }
  finally { release?.(); await context.close(); await browser.close(); }
})().catch(e => { console.error(e); process.exitCode = 1; });
