// Club-thema: CSS-variabelen, licht/donker-modus en favicon (#325, uitgebreid in #1256 — epic #1249).
//
// Dit bestand wordt in index.html bewust vóór _framework/blazor.webassembly.js geladen. De IIFE
// hieronder zet het data-theme-attribuut daardoor synchroon, vóórdat Blazor ook maar begint te
// booten — zonder dat zou de gebruiker eerst het lichte laadscherm zien en daarna pas de omslag
// naar donker. Die volgorde in index.html dus niet wijzigen.
//
// Let op: dit MOET een los bestand blijven en mag nooit een inline <script> in index.html worden.
// De productie-CSP van Azure Static Web Apps staat 'script-src self wasm-unsafe-eval' toe, zonder
// 'unsafe-inline' — een inline script wordt daar geblokkeerd (#659).

(function () {
    // localStorage kan gooien of leeg terugkomen in een privévenster of met geblokkeerde
    // site-data. Nooit de pagina daarop laten stranden: dan maar de lichte modus.
    var mode = 'light';
    try {
        var stored = localStorage.getItem('theme-mode');
        if (stored === 'light' || stored === 'dark') {
            mode = stored;
        } else if (window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches) {
            mode = 'dark';
        }
    } catch (e) {
        // Geen opgeslagen voorkeur beschikbaar — 'light' blijft staan.
    }
    document.documentElement.setAttribute('data-theme', mode);
})();

window.themeHelper = (function () {
    // De server valideert sleutel en waarde al (#1254). Dit is de tweede laag: wat hier
    // binnenkomt belandt rechtstreeks in een CSS-property, dus een waarde die niet aan de
    // verwachte vorm voldoet wordt overgeslagen in plaats van doorgezet.
    var SLEUTEL = /^[a-z][a-zA-Z0-9-]{0,39}$/;
    var WAARDE = /^#[0-9a-fA-F]{6}([0-9a-fA-F]{2})?$/;

    // De DTO gebruikt camelCase ("cardBg"), app.css kebab-case ("--theme-card-bg-light").
    function naarCssNaam(sleutel) {
        return sleutel.replace(/[A-Z]/g, function (letter) {
            return '-' + letter.toLowerCase();
        });
    }

    function zetPalet(kleuren, achtervoegsel) {
        if (!kleuren) return;
        var root = document.documentElement;
        for (var sleutel in kleuren) {
            if (!Object.prototype.hasOwnProperty.call(kleuren, sleutel)) continue;
            var waarde = kleuren[sleutel];
            if (!SLEUTEL.test(sleutel) || typeof waarde !== 'string' || !WAARDE.test(waarde)) continue;
            root.style.setProperty('--theme-' + naarCssNaam(sleutel) + achtervoegsel, waarde);
        }
    }

    return {
        // Zet beide paletten tegelijk. Welke van de twee zichtbaar is bepaalt app.css aan de hand
        // van data-theme — niet deze functie. Daardoor is omschakelen daarna gratis: geen tweede
        // ronde JSInterop, geen opnieuw ophalen bij de server.
        applyMode: function (lightColors, darkColors) {
            zetPalet(lightColors, '-light');
            zetPalet(darkColors, '-dark');
        },

        getMode: function () {
            return document.documentElement.getAttribute('data-theme') || 'light';
        },

        setMode: function (mode) {
            if (mode !== 'light' && mode !== 'dark') return;
            document.documentElement.setAttribute('data-theme', mode);
            try {
                localStorage.setItem('theme-mode', mode);
            } catch (e) {
                // Voorkeur niet op te slaan (privévenster, geblokkeerde site-data): de modus geldt
                // dan alleen voor deze sessie. Geen reden om het omschakelen zelf te laten falen.
            }
        },

        setFavicon: function (url) {
            var link = document.querySelector("link[rel='icon']");
            if (!link) {
                link = document.createElement('link');
                link.rel = 'icon';
                document.head.appendChild(link);
            }
            link.href = url;
        }
    };
})();
