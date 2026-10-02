// theme-css-pad.js (#1442) — voor het thema-beheerscherm: welke CSS-selectors gebruiken een
// --theme-*-variabele?
//
// Bewust live uit document.styleSheets en niet als handgeschreven lijst: zo'n lijst loopt uit de
// pas zodra iemand een selector toevoegt, en dan toont het scherm precies de verkeerde informatie.
// De browser weet wat er daadwerkelijk geladen is — app.css, Bootstrap én de gebundelde scoped
// CSS van elke component (BlazorAdmin.styles.css).
//
// Niet gevonden wordt: kleur die C#-code inline zet (bijv. de Gantt-blokken van Planning). Het
// scherm meldt dat expliciet in plaats van "niet gebruikt".
window.themeCssPad = (function () {
    function escape(tekst) {
        return tekst.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    }

    // Scoped CSS krijgt een attribuut als [b-abc123xyz]; dat is ruis voor de lezer.
    function schoonSelector(selector) {
        return selector.replace(/\[b-[a-z0-9]+\]/g, '').replace(/\s+/g, ' ').trim();
    }

    return {
        // namen: ["--theme-primary", ...]. Resultaat: { "--theme-primary": ["a", ".btn-primary"], ... }
        zoekSelectors: function (namen) {
            var resultaat = {};
            var patronen = namen.map(function (naam) {
                resultaat[naam] = [];
                // Precies deze naam: --theme-primary mag niet matchen op --theme-primary-light.
                return [naam, new RegExp('var\\(\\s*' + escape(naam) + '\\s*[,)]')];
            });

            function verwerk(regels) {
                for (var i = 0; i < regels.length; i++) {
                    var regel = regels[i];
                    if (regel.selectorText && regel.style && regel.selectorText.indexOf(':root') !== 0) {
                        // :root-blokken zijn de definities van de variabelen zelf, geen gebruik.
                        var tekst = regel.style.cssText;
                        for (var j = 0; j < patronen.length; j++) {
                            if (patronen[j][1].test(tekst)) {
                                var selector = schoonSelector(regel.selectorText);
                                var lijst = resultaat[patronen[j][0]];
                                if (lijst.indexOf(selector) === -1) lijst.push(selector);
                            }
                        }
                    }
                    // @media/@supports en geneste regels.
                    if (regel.cssRules) verwerk(regel.cssRules);
                }
            }

            for (var k = 0; k < document.styleSheets.length; k++) {
                var regels;
                try { regels = document.styleSheets[k].cssRules; } catch (e) { continue; } // cross-origin
                if (regels) verwerk(regels);
            }
            return resultaat;
        }
    };
})();
