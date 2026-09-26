// check-theme-js-contract.js — bewaakt dat theme.js en app.css het over dezelfde variabelen hebben
// (#1256, epic #1249).
//
// Het probleem dat dit dicht: ThemeService stuurt camelCase-sleutels ("cardBg"), theme.js maakt
// daar een CSS-propertynaam van ("--theme-card-bg-light"), en app.css moet die naam definiëren.
// Klopt de vertaling niet, dan zet de browser een property die niemand leest. Dat geeft geen
// foutmelding en geen console-waarschuwing — de kleur blijft simpelweg op de standaardwaarde
// staan, alleen zichtbaar voor wie precies die pagina in precies die modus opent.
//
// Draait zonder browser: theme.js wordt geladen met een minimale DOM-stub, applyMode wordt
// aangeroepen met alle sleutels die app.css kent, en we vergelijken de gezette propertynamen met
// de variabelen die app.css daadwerkelijk definieert. Node draait al in CI (zie
// .github/scripts/issue-status.test.js).

const fs = require('fs');
const path = require('path');
const vm = require('vm');

const wortel = path.resolve(__dirname, '..', '..');
const cssPad = path.join(wortel, 'BlazorAdmin/wwwroot/css/app.css');
const jsPad = path.join(wortel, 'BlazorAdmin/wwwroot/js/theme.js');

let fouten = 0;
const meld = (bericht) => { console.log(`::error::${bericht}`); fouten++; };

// --- app.css: welke -light/-dark-variabelen bestaan er? -------------------------------------
const css = fs.readFileSync(cssPad, 'utf8').replace(/\/\*[\s\S]*?\*\//g, '');
const gedefinieerd = new Set();
for (const m of css.matchAll(/^\s*(--theme-[a-z0-9-]+)\s*:/gm)) gedefinieerd.add(m[1]);

const basisNamen = [...gedefinieerd]
    .filter((n) => n.endsWith('-light'))
    .map((n) => n.slice('--theme-'.length, -'-light'.length));

if (basisNamen.length === 0) {
    meld('Geen enkele --theme-*-light-variabele gevonden in app.css. Klopt het pad nog?');
}

// --- theme.js laden met een minimale DOM-stub ------------------------------------------------
const gezet = {};
const documentElement = {
    _attrs: {},
    style: { setProperty: (naam, waarde) => { gezet[naam] = waarde; } },
    setAttribute(naam, waarde) { this._attrs[naam] = waarde; },
    getAttribute(naam) { return this._attrs[naam] ?? null; }
};
const sandbox = {
    document: { documentElement, querySelector: () => null, createElement: () => ({}), head: { appendChild() {} } },
    localStorage: { getItem: () => null, setItem() {} },
    console
};
sandbox.window = sandbox;
vm.createContext(sandbox);
vm.runInContext(fs.readFileSync(jsPad, 'utf8'), sandbox, { filename: 'theme.js' });

// De IIFE moet data-theme synchroon gezet hebben — dat is de hele reden dat theme.js vóór
// blazor.webassembly.js geladen wordt.
const modus = documentElement.getAttribute('data-theme');
if (modus !== 'light' && modus !== 'dark') {
    meld(`theme.js zet data-theme niet bij het laden (waarde: ${JSON.stringify(modus)}). Zonder dat flitst de pagina eerst in het verkeerde thema.`);
}

if (typeof sandbox.window.themeHelper?.applyMode !== 'function') {
    meld('window.themeHelper.applyMode bestaat niet. ThemeService.ApplyAsync roept precies die naam aan.');
    process.exit(1);
}

// --- De vertaling toetsen ---------------------------------------------------------------------
// kebab-case uit app.css terug naar de camelCase-sleutel die de DTO gebruikt.
const naarCamel = (kebab) => kebab.replace(/-([a-z])/g, (_, c) => c.toUpperCase());

const palet = {};
for (const basis of basisNamen) palet[naarCamel(basis)] = '#123456';

sandbox.window.themeHelper.applyMode(palet, palet);

for (const basis of basisNamen) {
    for (const achtervoegsel of ['-light', '-dark']) {
        const verwacht = `--theme-${basis}${achtervoegsel}`;
        if (gezet[verwacht] !== '#123456') {
            meld(`theme.js zet '${verwacht}' niet voor sleutel '${naarCamel(basis)}'. De camelCase-naar-kebab-vertaling en app.css lopen uit de pas.`);
        }
    }
}

// --- De waardevalidatie in theme.js moet echt filteren ----------------------------------------
const voor = Object.keys(gezet).length;
sandbox.window.themeHelper.applyMode({
    primary: 'rgba(0,0,0,.5)',              // vrije CSS-functie
    'kleur: red; --evil': '#112233',        // injectiepoging in de sleutel
    accent: 'javascript:alert(1)'
}, null);
const bijgekomen = Object.keys(gezet).filter((n) => !n.startsWith('--theme-') || gezet[n] !== '#123456');
if (gezet['--theme-primary-light'] !== '#123456') {
    meld('theme.js heeft een ongeldige waarde (rgba(...)) tóch doorgezet naar een CSS-property.');
}
if (Object.keys(gezet).length !== voor || bijgekomen.length > 0) {
    meld('theme.js heeft een ongeldige sleutel of waarde doorgelaten naar een CSS-property.');
}

// --- ThemePresets.cs: kan de beheerder élke kleur uit app.css ook instellen? ----------------
// Een kleur die wel in app.css staat maar niet in ThemePresets.Kleuren is onzichtbaar in het
// beheerscherm: hij bestaat, maar niemand kan hem zetten. Andersom levert een sleutel die app.css
// niet kent een CSS-property op die niets doet.
const presetsPad = path.join(wortel, 'BlazorAdmin/Models/ThemePresets.cs');
if (fs.existsSync(presetsPad)) {
    const presets = fs.readFileSync(presetsPad, 'utf8');
    const naarKebab = (camel) => camel.replace(/[A-Z]/g, (l) => '-' + l.toLowerCase());
    const presetBasis = [...presets.matchAll(/new ThemeKleurDefinitie\("([a-zA-Z0-9]+)"/g)].map((m) => naarKebab(m[1]));

    for (const basis of basisNamen) {
        if (!presetBasis.includes(basis)) {
            meld(`app.css definieert '--theme-${basis}-light' maar ThemePresets.Kleuren kent '${basis}' niet. Die kleur is dan niet in te stellen in het beheerscherm.`);
        }
    }
    for (const basis of presetBasis) {
        if (!basisNamen.includes(basis)) {
            meld(`ThemePresets.Kleuren kent '${basis}' maar app.css definieert '--theme-${basis}-light' niet. Die kleur wordt dan nergens gebruikt.`);
        }
    }

    // Elke preset moet compleet zijn: een half gevulde set laat kleuren op de vorige waarde staan.
    for (const m of presets.matchAll(/new ThemePreset\(\s*"([^"]+)"/g)) {
        // Volledigheid wordt in C# afgedwongen doordat elke preset op de standaardset wordt
        // samengesteld; hier alleen vastleggen dat die samenstelling er nog is.
        if (!/Samenstellen\(/.test(presets) && m[1] !== 'Standaard') {
            meld(`Preset '${m[1]}' wordt niet meer op de standaardset samengesteld — dan raakt hij stilzwijgend incompleet zodra er een kleur bij komt.`);
        }
    }
}

if (fouten > 0) {
    console.log(`Thema-JS-contractguard: ${fouten} probleem(en).`);
    process.exit(1);
}

console.log(`Thema-JS-contractguard: ${basisNamen.length} kleuren, elk correct vertaald door theme.js naar de -light- en -dark-variabele die app.css definieert. Ongeldige sleutels en waarden worden geweigerd.`);
