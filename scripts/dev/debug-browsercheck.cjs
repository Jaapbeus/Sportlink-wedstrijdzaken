// debug-browsercheck.cjs (#1576)
// Echte browsercontrole van de lokale debugomgeving: opent de belangrijkste schermen in headless
// Chromium en faalt op alles waar een beheerder tegenaan zou lopen — een foutbanner, een mislukte
// of 5xx-aanroep naar de FunctionApp ("Failed to fetch"), een console-fout of een ontbrekend of
// verkeerd versienummer. HTTP 200 op index.html bewijst niets: Blazor WASM rendert client-side.
//
// Aanroep (door Test-DebugGo.ps1, met NODE_PATH naar de map waar 'playwright' is geïnstalleerd):
//   node debug-browsercheck.cjs <blazorUrl> <apiUrl> <verwachteVersie> [uitvoerMap]
// Exit 0 = alle schermen schoon, 1 = minstens één bevinding. Het JSON-resultaat staat op stdout.

const fs = require('fs');
const os = require('os');
const path = require('path');
const { chromium } = require('playwright');

const [blazorUrl, apiUrl, verwachteVersie, uitvoerMap] = process.argv.slice(2);

// De schermen die in de praktijk stuk gingen toen de FunctionApp wegviel (#1576): alles wat data
// van de API haalt. Pad -> korte naam voor de rapportage.
const SCHERMEN = [
  ['/', 'Start'],
  ['/instellingen', 'Instellingen'],
  ['/planning', 'Planning'],
  ['/email-tester', 'E-mailtester'],
  ['/teamaliassen', 'Teamaliassen'],
  ['/sportlink-extension-settings', 'Sportlink-extensie'],
  ['/instellingen/speeltijden', 'Speeltijden'],
];

// Eigen Chromium-cache gebruiken als de gepinde Playwright-versie geen passende browser vindt:
// de browser staat vaak al lokaal en een download is hier nooit de bedoeling.
function gecachteBrowser() {
  const bases = [
    process.env.PLAYWRIGHT_BROWSERS_PATH,
    path.join(os.homedir(), 'Library', 'Caches', 'ms-playwright'),
    path.join(os.homedir(), '.cache', 'ms-playwright'),
    path.join(process.env.LOCALAPPDATA || '', 'ms-playwright'),
  ].filter(Boolean);
  for (const base of bases) {
    if (!fs.existsSync(base)) continue;
    const mappen = fs.readdirSync(base).filter((n) => n.startsWith('chromium_headless_shell-')).sort().reverse();
    for (const m of mappen) {
      const sub = fs.readdirSync(path.join(base, m)).find((n) => n.startsWith('chrome-headless-shell'));
      if (!sub) continue;
      for (const exe of ['chrome-headless-shell', 'chrome-headless-shell.exe']) {
        const p = path.join(base, m, sub, exe);
        if (fs.existsSync(p)) return p;
      }
    }
  }
  return null;
}

async function start() {
  try {
    return await chromium.launch({ headless: true });
  } catch (e) {
    const p = gecachteBrowser();
    if (!p) throw e;
    return await chromium.launch({ headless: true, executablePath: p });
  }
}

(async () => {
  const browser = await start();
  const resultaten = [];
  let bevindingen = 0;

  for (const [pad, naam] of SCHERMEN) {
    const ctx = await browser.newContext({ viewport: { width: 1280, height: 900 } });
    const page = await ctx.newPage();
    const fouten = [];
    const apiOrigin = new URL(apiUrl).origin;

    page.on('console', (m) => { if (m.type() === 'error') fouten.push('console: ' + m.text().slice(0, 200)); });
    page.on('pageerror', (e) => fouten.push('pagina-fout: ' + String(e.message).slice(0, 200)));
    // Lopende API-aanroepen bijhouden: een aanroep die nog loopt als de pagina sluit eindigt met
    // ERR_ABORTED en is dan een meetartefact (bijv. een trage live Sportlink-aanroep), geen bevinding.
    let lopend = 0;
    let laatsteActiviteit = Date.now();
    page.on('request', (r) => { if (r.url().startsWith(apiOrigin)) { lopend++; laatsteActiviteit = Date.now(); } });
    page.on('requestfinished', (r) => { if (r.url().startsWith(apiOrigin)) { lopend--; laatsteActiviteit = Date.now(); } });
    // Een door de pagina zelf afgebroken aanroep (ERR_ABORTED) die daarna wél slaagt is geen fout: de
    // beheerschermen laden bij het openen meerdere keren (clubkiezer, herlaadronde) en een nieuwe
    // laadronde annuleert de lopende aanroep van de vorige. Zonder latere geslaagde aanroep blijft het
    // wél een bevinding.
    const afgebroken = new Map();
    const geslaagd = new Set();
    const sleutel = (r) => r.method() + ' ' + new URL(r.url()).pathname;
    page.on('requestfailed', (r) => {
      if (!r.url().startsWith(apiOrigin)) return;
      lopend--; laatsteActiviteit = Date.now();
      const reden = (r.failure() && r.failure().errorText) || '';
      if (reden.includes('ERR_ABORTED')) afgebroken.set(sleutel(r), reden);
      else fouten.push('aanroep mislukt: ' + sleutel(r) + ' (' + reden + ')');
    });
    page.on('response', (r) => {
      if (!r.url().startsWith(apiOrigin)) return;
      if (r.status() >= 500) fouten.push('API ' + r.status() + ': ' + new URL(r.url()).pathname);
      else geslaagd.add(r.request().method() + ' ' + new URL(r.url()).pathname);
    });

    let versieZichtbaar = null;
    try {
      await page.goto(blazorUrl + pad, { waitUntil: 'load', timeout: 60000 });
      // Blazor is gestart zodra het laadscherm weg is en de app inhoud heeft gerenderd.
      await page.waitForFunction(() => window.Blazor && document.querySelector('app, #app')
        && document.querySelector('#app').children.length > 0
        && !document.querySelector('.loading-progress'), null, { timeout: 60000 });
      await page.waitForLoadState('networkidle', { timeout: 20000 }).catch(() => {});
      await page.waitForTimeout(1500); // late API-aanroepen na de eerste render
      // Een scherm laadt zijn data vaak in een reeks opeenvolgende aanroepen: pas klaar als er niets
      // loopt én 3 s lang niets nieuws begon (max 40 s).
      for (let i = 0; i < 80 && (lopend > 0 || Date.now() - laatsteActiviteit < 3000); i++) await page.waitForTimeout(500);
      if (lopend > 0) fouten.push(lopend + ' API-aanroep(en) kwamen niet binnen 40 s terug');

      const banner = await page.evaluate(() => {
        const el = document.getElementById('blazor-error-ui');
        return !!el && getComputedStyle(el).display !== 'none';
      });
      if (banner) fouten.push('foutbanner "An unhandled error has occurred" zichtbaar');

      const tekst = await page.evaluate(() => document.body.innerText);
      const m = tekst.match(/v(\d+\.\d+\.\d+\.\d+)/);
      versieZichtbaar = m ? m[1] : null;
      if (!versieZichtbaar) fouten.push('geen versienummer zichtbaar in de header');
      else if (verwachteVersie && versieZichtbaar !== verwachteVersie) fouten.push('versie ' + versieZichtbaar + ' i.p.v. verwacht ' + verwachteVersie);
      if (/failed to fetch/i.test(tekst)) fouten.push('"Failed to fetch" staat op het scherm');
    } catch (e) {
      fouten.push('scherm laadde niet: ' + String(e.message).split('\n')[0].slice(0, 200));
    }

    if (uitvoerMap) {
      try {
        fs.mkdirSync(uitvoerMap, { recursive: true });
        await page.screenshot({ path: path.join(uitvoerMap, naam.replace(/[^a-z0-9]+/gi, '-') + '.png') });
      } catch (_) { /* een screenshot is bewijs, geen voorwaarde */ }
    }
    for (const [k, reden] of afgebroken) if (!geslaagd.has(k)) fouten.push('aanroep afgebroken zonder latere geslaagde aanroep: ' + k + ' (' + reden + ')');
    const uniek = [...new Set(fouten)];
    bevindingen += uniek.length;
    resultaten.push({ scherm: naam, pad, versie: versieZichtbaar, fouten: uniek });
    await ctx.close();
  }

  await browser.close();
  console.log(JSON.stringify({ ok: bevindingen === 0, schermen: resultaten }, null, 2));
  process.exit(bevindingen === 0 ? 0 : 1);
})().catch((e) => {
  console.log(JSON.stringify({ ok: false, fout: String(e.message).slice(0, 400) }));
  process.exit(2);
});
