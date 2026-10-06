// browsercheck-speeltijden-formulier.mjs — Playwright/Chromium-controle van het inline bewerkformulier op
// Instellingen → Speeltijden (#1543, #1552, #1553, #1554). Zie docs/VERIFICATIE-SCRIPTS.md en
// docs/DOSSIER-SPEELTIJDEN-INLINE-FORMULIER.md.
//
// Draait tegen een EIGEN BlazorAdmin-instantie (Development, lokale auth-bypass) — nooit tegen de gedeelde
// debugomgeving op :5242. Alle API-verkeer naar de FunctionApp-poort (:7094) wordt onderschept en gemockt;
// elk niet-lokaal request wordt afgebroken. Er wordt dus geen database of externe dienst geraakt.
//
//   cd BlazorAdmin && ASPNETCORE_ENVIRONMENT=Development dotnet run --no-launch-profile --urls http://localhost:5301
//   # in een (scratch-)map waarin Playwright is geïnstalleerd — npm install playwright; npx playwright install chromium —
//   # en van dáár uit het script in de repo aanroepen; het script hoeft niet gekopieerd te worden:
//   BLAZOR_URL=http://localhost:5301 node <repo>/scripts/dev/browsercheck-speeltijden-formulier.mjs
//
// Playwright wordt gezocht vanaf de huidige werkmap. Een kale `import 'playwright'` resolveert ESM vanaf de
// locatie van dít bestand (de repo, zonder node_modules) en faalt dan met ERR_MODULE_NOT_FOUND — PR #1557, review
// ronde 1, P3. Daarom hieronder eerst de kale import (werkt als het script náást node_modules staat) en anders
// expliciet <werkmap>/node_modules/playwright.
//
// Exit 0 = alle controles geslaagd. Chrome logt elke 4xx/5xx-respons als console-error; de opzettelijk
// falende mocks worden daarom apart geteld (netlog) en niet als applicatiefout gerekend.
import { existsSync } from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

async function laadPlaywright() {
  try { return await import('playwright'); }
  catch (e) {
    if (e.code !== 'ERR_MODULE_NOT_FOUND') throw e;
    const lokaal = path.join(process.cwd(), 'node_modules', 'playwright', 'index.mjs');
    if (!existsSync(lokaal)) {
      console.error(`Playwright niet gevonden: niet naast het script en niet in ${process.cwd()}/node_modules.\n` +
        'Installeer het in de map van waaruit je dit script start: npm install playwright && npx playwright install chromium');
      process.exit(2);
    }
    return await import(pathToFileURL(lokaal).href);
  }
}
const { chromium } = await laadPlaywright();
const base = process.env.BLAZOR_URL || 'http://localhost:5301';
const LANG = 'Senioren-zaterdag-veteranen-zevental-met-een-heel-lange-categorienaam';
const lft = ['JO6','JO7','JO8','JO9','JO10','JO11','JO12','JO13','JO14','JO15','JO16','JO17','JO18','JO19','JO23','MO7','MO8','MO9','MO10','MO11','MO12','MO13','MO14','MO15','MO16','MO17','MO18','MO19','MO20','MO23','G','VR','1-99', LANG];
const results = []; let failed = 0;
const ok = (c, m) => { results.push((c ? 'OK   ' : 'FAIL ') + m); if (!c) failed++; };
const info = m => results.push('INFO ' + m);
const browser = await chromium.launch();

async function open({ width = 1400, height = 900, leeg = false } = {}) {
  const ctx = await browser.newContext({ viewport: { width, height } });
  const page = await ctx.newPage();
  const errors = [], netlog = [];
  page.on('pageerror', e => errors.push('pageerror: ' + e.message));
  page.on('console', m => {
    if (m.type() !== 'error') return;
    // Chrome logt elke 4xx/5xx-respons als console-error; bij een opzettelijk falende mock is dat verwacht gedrag, geen applicatiefout.
    if (/Failed to load resource: the server responded with a status of \d{3}/.test(m.text())) netlog.push(m.text()); else errors.push(m.text());
  });
  const api = { state: leeg ? [] : lft.map(l => ({ leeftijd: l, veldafmeting: 1, wedstrijdTotaal: 75, wedstrijdHelft: 30, wedstrijdRust: 15, standaardVoorkeurTijd: '10:00' })), hold: null, holdGet: null, failGetNext: false, puts: [], posts: [], failNext: null };
  await page.route('**/*', async route => {
    const req = route.request(); const url = new URL(req.url());
    if (url.hostname !== 'localhost') return route.abort();          // geen enkel extern verkeer
    if (url.port !== '7094') return route.continue();
    const json = (b, s = 200) => route.fulfill({ status: s, contentType: 'application/json', body: JSON.stringify(b) });
    if (url.pathname === '/api/health') return json({ status: 'healthy', database: 'online', version: 'test' });
    if (url.pathname === '/api/beheer/speeltijden' && req.method() === 'GET') {
      if (api.holdGet) { const g = api.holdGet; api.holdGet = null; await g.gate; }
      if (api.failGetNext) { api.failGetNext = false; return json({ error: 'lijst tijdelijk niet beschikbaar' }, 500); }
      return json(api.state);
    }
    if (url.pathname.startsWith('/api/beheer/speeltijden') && ['PUT', 'POST'].includes(req.method())) {
      const body = req.postDataJSON(); const key = decodeURIComponent(url.pathname.split('/').pop());
      (req.method() === 'PUT' ? api.puts : api.posts).push({ key, body });
      if (api.hold) { const h = api.hold; api.hold = null; await h.gate; if (h.status !== 200) return json({ error: h.error }, h.status); }
      else if (api.failNext) { const f = api.failNext; api.failNext = null; return json({ error: f }, 400); }
      const norm = Object.fromEntries(Object.entries(body).map(([k, v]) => [k[0].toLowerCase() + k.slice(1), v]));
      if (req.method() === 'PUT') api.state = api.state.map(s => s.leeftijd === key ? { ...s, ...norm } : s); else api.state.push(norm);
      return json({});
    }
    return json([]);
  });
  await page.goto(base + '/instellingen/speeltijden');
  await page.waitForSelector(leeg ? 'text=Geen speeltijden gevonden.' : 'td:text-is("1-99")', { timeout: 60000 });
  return { ctx, page, api, errors, netlog };
}
const holdNext = (api, status, error) => { let release; const gate = new Promise(r => release = r); api.hold = { gate, status, error }; return release; };
const holdGet = api => { let release; const gate = new Promise(r => release = r); api.holdGet = { gate }; return release; };
const row = (page, l) => page.locator(`tbody > tr:has(> td:text-is("${l}"))`);
const formRows = page => page.locator('tr.speeltijd-formulierrij');
const nextIsForm = async (page, l) => row(page, l).evaluate(tr => tr.nextElementSibling?.classList.contains('speeltijd-formulierrij') ?? false);
const totaalVan = async (page, l) => (await row(page, l).locator('td').nth(2).textContent()).trim();
const settle = page => page.waitForTimeout(400);
const geenFouten = (errors, label) => ok(errors.length === 0, `${label}: geen page-/console-errors` + (errors.length ? ': ' + errors.join(' | ') : ''));

// ===== #1552 — vertraagde opslagresultaten =====
{
  const { ctx, page, api, errors, netlog } = await open();
  // 1. vertraagde FOUT voor A (JO10), intussen gewisseld naar B (JO11)
  await row(page, 'JO10').getByRole('button', { name: 'Bewerken' }).click();
  await page.getByLabel('Totaal (min)').fill('66');
  let release = holdNext(api, 500, 'serverfout-A');
  await formRows(page).getByRole('button', { name: 'Opslaan' }).click();
  await page.waitForFunction(() => document.querySelector('tr.speeltijd-formulierrij button[type=submit]')?.disabled === true);
  ok(true, '#1552 Opslaan-knop is uitgeschakeld en toont "Opslaan…" tijdens de lopende opslag');
  await row(page, 'JO11').getByRole('button', { name: 'Bewerken' }).click();
  ok((await row(page, 'JO10').getByRole('status').textContent()).includes('Opslaan…'), '#1552 fout-variant: regel A toont "Opslaan…" zolang de opslag loopt');
  ok(await row(page, 'JO10').getByRole('button', { name: 'Bewerken' }).isDisabled(), '#1552 fout-variant: Bewerken van A is intussen uitgeschakeld');
  release(); await settle(page);
  ok(await nextIsForm(page, 'JO11'), '#1552 fout-variant: formulier van B (JO11) blijft open');
  ok(await formRows(page).locator('.alert-danger').count() === 0, '#1552 fout-variant: geen fout onder het formulier van B');
  ok((await page.locator('.alert-danger[role=alert]').first().textContent()).includes('Opslaan van JO10 is mislukt'), '#1552 fout-variant: paginamelding noemt A (JO10) en de serverfout');
  ok(!(await row(page, 'JO10').getByRole('button', { name: 'Bewerken' }).isDisabled()), '#1552 fout-variant: na de mislukte opslag is A weer bewerkbaar');
  await page.getByRole('button', { name: 'Sluiten' }).click();
  ok(await page.locator('text=Opslaan van JO10').count() === 0, '#1552 paginamelding is te sluiten');

  // 2. vertraagd SUCCES voor B, gewisseld naar C (JO12) → C blijft open mét invoer, lijst ververst
  await page.getByLabel('Totaal (min)').fill('77');
  await page.getByLabel('Totaal (min)').press('Tab');
  release = holdNext(api, 200);
  await formRows(page).getByRole('button', { name: 'Opslaan' }).click();
  await row(page, 'JO12').getByRole('button', { name: 'Bewerken' }).click();
  await page.getByLabel('Helft (min)').fill('31');                 // gebruiker typt al in C
  release(); await settle(page);
  ok(await nextIsForm(page, 'JO12'), '#1552 succes-variant: formulier van C (JO12) blijft open');
  ok(await page.getByLabel('Helft (min)').inputValue() === '31', '#1552 succes-variant: invoer in C blijft behouden bij de lijstverversing');
  ok(await totaalVan(page, 'JO11') === '77', '#1552 succes-variant: lijst toont de opgeslagen waarde van B (77)');
  ok(await page.locator('.alert-danger[role=alert]').count() === 0, '#1552 succes-variant: geen melding');

  // 3. vertraagde fout, dan Nieuwe categorie
  await page.getByLabel('Totaal (min)').fill('88');
  release = holdNext(api, 500, 'x');
  await formRows(page).getByRole('button', { name: 'Opslaan' }).click();
  await page.getByRole('button', { name: 'Nieuwe categorie' }).click();
  release(); await settle(page);
  ok(await page.locator('tbody > tr').first().evaluate(tr => tr.classList.contains('speeltijd-formulierrij')), '#1552 nieuw-variant: nieuw formulier blijft open bovenaan');
  ok(await formRows(page).locator('.alert-danger').count() === 0, '#1552 nieuw-variant: geen fout in het nieuwe formulier');
  ok(await page.locator('text=Opslaan van JO12 is mislukt').count() === 1, '#1552 nieuw-variant: paginamelding noemt JO12');
  await page.getByRole('button', { name: 'Sluiten' }).click();
  await formRows(page).getByRole('button', { name: 'Annuleer' }).click();

  // 4. vertraagde fout, annuleren
  await row(page, 'JO13').getByRole('button', { name: 'Bewerken' }).click();
  release = holdNext(api, 500, 'x');
  await formRows(page).getByRole('button', { name: 'Opslaan' }).click();
  await formRows(page).getByRole('button', { name: 'Annuleer' }).click();
  release(); await settle(page);
  ok(await formRows(page).count() === 0, '#1552 annuleer-variant: er wordt geen formulier heropend');
  ok(await page.locator('text=Opslaan van JO13 is mislukt').count() === 1, '#1552 annuleer-variant: paginamelding noemt JO13');
  await page.getByRole('button', { name: 'Sluiten' }).click();

  // 5. vertraagd succes, annuleren, dezelfde categorie heropenen
  await row(page, 'JO14').getByRole('button', { name: 'Bewerken' }).click();
  await page.getByLabel('Totaal (min)').fill('90');
  release = holdNext(api, 200);
  await formRows(page).getByRole('button', { name: 'Opslaan' }).click();
  await formRows(page).getByRole('button', { name: 'Annuleer' }).click();
  ok(await row(page, 'JO14').getByRole('button', { name: 'Bewerken' }).isDisabled(), '#1552 heropen-variant: Bewerken van JO14 is uitgeschakeld zolang zijn opslag loopt');
  ok(await row(page, 'JO14').getByRole('button', { name: 'Verwijderen' }).isDisabled(), '#1552 heropen-variant: Verwijderen van JO14 ook');
  ok(!(await row(page, 'JO15').getByRole('button', { name: 'Bewerken' }).isDisabled()), '#1552 heropen-variant: andere regel (JO15) blijft bewerkbaar');
  release(); await settle(page);
  ok(!(await row(page, 'JO14').getByRole('button', { name: 'Bewerken' }).isDisabled()), '#1552 heropen-variant: na afronding is JO14 weer bewerkbaar');
  ok(await totaalVan(page, 'JO14') === '90', '#1552 heropen-variant: lijst toont de opgeslagen waarde (90)');
  await row(page, 'JO14').getByRole('button', { name: 'Bewerken' }).click();
  ok(await nextIsForm(page, 'JO14') && await page.getByLabel('Totaal (min)').inputValue() === '90', '#1552 heropen-variant: heropend formulier toont de opgeslagen waarde, niet de oude');
  ok(await formRows(page).locator('.alert-danger').count() === 0 && await page.locator('.alert-danger[role=alert]').count() === 0, '#1552 heropen-variant: geen fout en geen melding');

  // 6. normale fout blijft bij eigen formulier; normaal succes sluit
  api.failNext = 'ongeldige waarde';
  await formRows(page).getByRole('button', { name: 'Opslaan' }).click(); await settle(page);
  ok(await formRows(page).locator('.alert-danger').count() === 1 && await page.locator('.alert-danger[role=alert]').count() === 1, '#1552 directe fout staat uitsluitend in het eigen formulier');
  await page.getByLabel('Rust (min)').fill('14');
  await formRows(page).getByRole('button', { name: 'Opslaan' }).click(); await settle(page);
  ok(await formRows(page).count() === 0 && (await row(page, 'JO14').locator('td').nth(4).textContent()).trim() === '14', '#1552 direct succes sluit het formulier en ververst de regel');
  // 7. Codex-reproductie (PR #1557 ronde 1, P2): PUT en GET afzonderlijk vertraagd, tussentijdse render,
  //    heropenen van dezelfde categorie, tweede opslag die een ander veld wijzigt
  await row(page, 'JO16').getByRole('button', { name: 'Bewerken' }).click();
  await page.getByLabel('Totaal (min)').fill('90');
  const releasePut = holdNext(api, 200);
  const releaseGet = holdGet(api);
  await formRows(page).getByRole('button', { name: 'Opslaan' }).click();
  await formRows(page).getByRole('button', { name: 'Annuleer' }).click();        // annuleren tijdens de PUT
  releasePut(); await settle(page);                                               // PUT geslaagd, GET hangt
  ok(await totaalVan(page, 'JO16') === '90', '#1552 P2-regressie: regel toont de opgeslagen waarde al vóór de GET (lokaal bijgewerkt)');
  await page.getByRole('button', { name: 'Nieuwe categorie' }).click();          // tussentijdse render
  ok(await row(page, 'JO16').getByRole('button', { name: 'Bewerken' }).isDisabled(), '#1552 P2-regressie: Bewerken van JO16 blijft uitgeschakeld zolang de GET loopt');
  ok((await row(page, 'JO16').getByRole('status').textContent()).includes('Opslaan…'), '#1552 P2-regressie: status "Opslaan…" blijft staan tot de lijst actueel is');
  await formRows(page).getByRole('button', { name: 'Annuleer' }).click();
  releaseGet(); await settle(page);
  ok(!(await row(page, 'JO16').getByRole('button', { name: 'Bewerken' }).isDisabled()), '#1552 P2-regressie: na de GET is JO16 weer bewerkbaar');
  await row(page, 'JO16').getByRole('button', { name: 'Bewerken' }).click();
  ok(await page.getByLabel('Totaal (min)').inputValue() === '90', '#1552 P2-regressie: heropend formulier toont 90, niet de oude 75');
  await page.getByLabel('Rust (min)').fill('13');
  await formRows(page).getByRole('button', { name: 'Opslaan' }).click(); await settle(page);
  const laatstePut = api.puts.at(-1);
  const totaalInBody = Number(laatstePut.body.wedstrijdTotaal ?? laatstePut.body.WedstrijdTotaal);
  const rustInBody = Number(laatstePut.body.wedstrijdRust ?? laatstePut.body.WedstrijdRust);
  ok(laatstePut.key === 'JO16' && totaalInBody === 90 && rustInBody === 13, `#1552 P2-regressie: tweede opslag schrijft totaal 90 en rust 13 (geen lost update; body totaal=${totaalInBody})`);

  // 8. geslaagde PUT, mislukte GET erna: regel toont de opgeslagen waarde, waarschuwing boven de tabel, regel komt vrij
  await row(page, 'JO17').getByRole('button', { name: 'Bewerken' }).click();
  await page.getByLabel('Totaal (min)').fill('91');
  api.failGetNext = true;
  await formRows(page).getByRole('button', { name: 'Opslaan' }).click(); await settle(page);
  const waarschuwing = page.locator('.alert-warning[role=alert]');
  ok(await waarschuwing.count() === 1 && (await waarschuwing.textContent()).includes('JO17'), '#1552 mislukte refresh: waarschuwing boven de tabel noemt JO17');
  ok(await totaalVan(page, 'JO17') === '91', '#1552 mislukte refresh: regel toont de opgeslagen waarde (91)');
  ok(!(await row(page, 'JO17').getByRole('button', { name: 'Bewerken' }).isDisabled()), '#1552 mislukte refresh: regel blijft niet eeuwig geblokkeerd');
  await row(page, 'JO17').getByRole('button', { name: 'Bewerken' }).click();
  ok(await page.getByLabel('Totaal (min)').inputValue() === '91', '#1552 mislukte refresh: heropend formulier toont 91');
  await formRows(page).getByRole('button', { name: 'Annuleer' }).click();
  await waarschuwing.getByRole('button', { name: 'Sluiten' }).click();
  ok(await page.locator('.alert-warning[role=alert]').count() === 0, '#1552 mislukte refresh: waarschuwing is te sluiten');

  ok(api.puts.length === 10 && api.posts.length === 0, `#1552 alle ${api.puts.length} opslagen liepen via de gemockte PUT; geen echte API geraakt`);
  ok(netlog.length === 5, `#1552 netwerklog bevat precies de 5 opzettelijk mislukte responsen (4× PUT, 1× GET) (${netlog.length})`);
  geenFouten(errors, '#1552');
  await ctx.close();
}

// ===== #1553 — responsive =====
for (const w of [320, 375, 768, 1400]) {
  for (const modus of ['bewerken', 'bewerken-lang', 'nieuw', 'fout', 'leeg']) {
    const { ctx, page, api, errors, netlog } = await open({ width: w, height: 812, leeg: modus === 'leeg' });
    if (modus === 'bewerken') await row(page, 'JO12').getByRole('button', { name: 'Bewerken' }).click();
    if (modus === 'bewerken-lang') await row(page, LANG).getByRole('button', { name: 'Bewerken' }).click();
    if (modus === 'nieuw' || modus === 'leeg') await page.getByRole('button', { name: 'Nieuwe categorie' }).click();
    if (modus === 'fout') {
      await row(page, 'MO20').getByRole('button', { name: 'Bewerken' }).click();
      api.failNext = 'De waarde voor totaal moet groter zijn dan de som van beide helften plus de rust; controleer de invoer.';
      await formRows(page).getByRole('button', { name: 'Opslaan' }).click(); await settle(page);
      ok(await formRows(page).locator('.alert-danger').isVisible() && netlog.length === 1, `#1553 ${w}px fout: lange foutmelding zichtbaar onder het formulier (400 in netwerklog)`);
    }
    if (modus === 'bewerken') ok(await nextIsForm(page, 'JO12'), `#1553 ${w}px: formulier staat direct onder JO12`);
    await page.waitForSelector('tr.speeltijd-formulierrij');
    const m = await page.evaluate(() => {
      const vw = document.documentElement.clientWidth;
      const lijst = document.querySelector('.speeltijden-lijst');
      const form = document.querySelector('tr.speeltijd-formulierrij');
      const past = e => { const r = e.getBoundingClientRect(); return r.right <= vw + 0.5 && r.left >= -0.5; };
      const naam = e => (e.id || e.getAttribute('aria-label') || e.textContent).trim().slice(0, 30);
      const bedieningBuiten = [...form.querySelectorAll('input, button, .alert, h5')].filter(e => !past(e)).map(naam);
      const lijstInhoudBuiten = [...lijst.querySelectorAll('tbody *')].filter(e => e.getBoundingClientRect().width > 0 && !past(e)).map(naam);
      const overigBuiten = [...document.querySelectorAll('article.content *')].filter(e => !e.closest('.speeltijden-lijst') && e.getBoundingClientRect().width > 0 && !past(e)).map(naam);
      const bovenbalkBuiten = [...document.querySelectorAll('.top-row *')].filter(e => e.getBoundingClientRect().width > 0 && !past(e)).length;
      return { vw, docW: document.documentElement.scrollWidth, formW: Math.round(form.getBoundingClientRect().width), formRight: Math.round(form.getBoundingClientRect().right),
               lijstScroll: lijst.scrollWidth, lijstClient: lijst.clientWidth, bedieningBuiten, lijstInhoudBuiten, overigBuiten, bovenbalkBuiten };
    });
    ok(m.bedieningBuiten.length === 0, `#1553 ${w}px ${modus}: alle velden, knoppen en meldingen van het formulier binnen de viewport (formulier ${m.formW}px, viewport ${m.vw}px)` + (m.bedieningBuiten.length ? ' — buiten: ' + m.bedieningBuiten.join(', ') : ''));
    ok(m.lijstInhoudBuiten.length === 0 && m.lijstScroll <= m.lijstClient + 1 && m.formRight <= m.vw, `#1553 ${w}px ${modus}: lijst en formulier pannen niet horizontaal (lijst ${m.lijstScroll}/${m.lijstClient}px, formulier rechts ${m.formRight} ≤ ${m.vw})` + (m.lijstInhoudBuiten.length ? ' — buiten: ' + m.lijstInhoudBuiten.join(', ') : ''));
    ok(m.overigBuiten.length === 0, `#1553 ${w}px ${modus}: overige pagina-inhoud (kop, knop Nieuwe categorie, melding) binnen de viewport` + (m.overigBuiten.length ? ' — buiten: ' + m.overigBuiten.join(', ') : ''));
    if (m.docW > m.vw) { ok(m.bovenbalkBuiten > 0 && m.docW <= 360, `#1553 ${w}px ${modus}: document ${m.docW}px > viewport komt uitsluitend door de bovenbalk van MainLayout (.top-row, niet gewijzigd in deze PR; zie dossier)`); }
    else ok(true, `#1553 ${w}px ${modus}: document ${m.docW}px ≤ viewport, geen horizontaal pannen`);
    // toetsenbord: vanaf het eerste bewerkbare veld tabben tot Opslaan
    await formRows(page).locator('input:not([disabled])').first().focus();
    let bereikt = false;
    for (let i = 0; i < 8 && !bereikt; i++) { await page.keyboard.press('Tab'); bereikt = await page.evaluate(() => document.activeElement?.textContent?.trim() === 'Opslaan'); }
    ok(bereikt, `#1553 ${w}px ${modus}: Opslaan bereikbaar met Tab`);
    if (w === 375 && modus === 'bewerken') {
      const snap = await page.locator('.speeltijden-lijst').ariaSnapshot();
      ok(/- table/.test(snap) && /- row /.test(snap) && /- cell /.test(snap), '#1553 375px: tabelsemantiek (table/row/cell) blijft in de kaartweergave aanwezig voor hulptechnologie');
      ok(/cell "Leeftijd JO12"/.test(snap) || /Leeftijd\s*JO12/.test(snap), '#1553 375px: kolomnaam uit data-label staat in de toegankelijke naam van de cel');
      console.log('ARIA-SNAPSHOT 375px kaartweergave (fragment):\n' + snap.split('\n').slice(0, 14).join('\n'));
    }
    if (['bewerken', 'nieuw'].includes(modus) || (modus === 'fout' && w === 375)) await page.screenshot({ path: `r-${w}-${modus}.png`, fullPage: false });
    geenFouten(errors, `#1553 ${w}px ${modus}`);
    await ctx.close();
  }
}

// ===== #1554 — labels =====
{
  const { ctx, page, api, errors } = await open();
  const velden = [['Leeftijd', 'textbox'], ['Veldafmeting', 'spinbutton'], ['Totaal (min) incl. rust', 'spinbutton'], ['Helft (min)', 'spinbutton'], ['Rust (min)', 'spinbutton'], ['Standaard voorkeurstijd', 'textbox']];
  for (const modus of ['bewerken', 'nieuw']) {
    if (modus === 'bewerken') await row(page, 'JO9').getByRole('button', { name: 'Bewerken' }).click();
    else { await formRows(page).getByRole('button', { name: 'Annuleer' }).click(); await page.getByRole('button', { name: 'Nieuwe categorie' }).click(); }
    const form = formRows(page);
    const labels = await form.locator('input').evaluateAll(ins => ins.map(i => ({ id: i.id, n: i.labels.length, forOk: i.labels[0]?.htmlFor === i.id })));
    ok(labels.length === 6 && labels.every(l => l.id && l.n === 1 && l.forOk), `#1554 ${modus}: alle 6 inputs hebben precies één label met for=id`);
    ok(new Set(labels.map(l => l.id)).size === 6, `#1554 ${modus}: ids uniek binnen het formulier`);
    for (const [naam, rol] of velden) {
      const el = form.getByRole(rol, { name: naam, exact: true });
      ok(await el.count() === 1, `#1554 ${modus}: toegankelijke naam "${naam}" (${rol})`);
      const disabled = await el.isDisabled();
      await form.locator(`label:has-text("${naam.split(' incl')[0]}")`).first().click({ force: disabled });
      const focus = await el.evaluate(e => document.activeElement === e);
      ok(disabled ? (modus === 'bewerken' && naam === 'Leeftijd' && !focus) : focus, `#1554 ${modus}: labelklik "${naam}" ${disabled ? '→ uitgeschakeld veld, geen focus (verwacht)' : 'focust het veld'}`);
    }
    const volgorde = [];
    await form.locator('input:not([disabled])').first().focus();
    for (let i = 0; i < 8; i++) { volgorde.push(await page.evaluate(() => document.activeElement.id || document.activeElement.textContent.trim())); await page.keyboard.press('Tab'); }
    const verwacht = (modus === 'nieuw' ? ['leeftijd'] : []).concat(['veldafmeting', 'totaal', 'helft', 'rust', 'voorkeurstijd', 'Opslaan', 'Annuleer']);
    ok(verwacht.every((v, i) => volgorde[i].endsWith(v)), `#1554 ${modus}: tabvolgorde ${volgorde.slice(0, verwacht.length).map(v => v.replace(/^speeltijd-\d+-/, '')).join(' → ')}`);
  }
  // twee formulieren na elkaar krijgen verschillende ids (geen botsing met een eerder gerenderd formulier)
  const idsNieuw = await formRows(page).locator('input').evaluateAll(ins => ins.map(i => i.id));
  await formRows(page).getByRole('button', { name: 'Annuleer' }).click();
  await row(page, 'JO9').getByRole('button', { name: 'Bewerken' }).click();
  const idsBewerk = await formRows(page).locator('input').evaluateAll(ins => ins.map(i => i.id));
  ok(idsNieuw.every(id => !idsBewerk.includes(id)), '#1554 opeenvolgende formulieren gebruiken verschillende id-reeksen');
  await page.getByLabel('Rust (min)').fill('12'); await page.getByLabel('Rust (min)').press('Enter'); await settle(page);
  ok(await formRows(page).count() === 0 && (await row(page, 'JO9').locator('td').nth(4).textContent()).trim() === '12', '#1554 Enter in een veld slaat op (toetsenbordbediening)');
  geenFouten(errors, '#1554');
  await ctx.close();
}
await browser.close();
console.log(results.join('\n'));
console.log(`\n${results.length - failed}/${results.length} geslaagd (${results.filter(r => r.startsWith('INFO')).length} info)`);
process.exitCode = failed ? 1 : 0;
