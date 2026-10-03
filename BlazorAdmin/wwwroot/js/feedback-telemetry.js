// Technische context voor de feedbackwidget (#764).
//
// Houdt de laatste console-fouten bij in een ringbuffer van 5, zodat de widget ze bij een melding
// kan tonen en (als de gebruiker dat laat staan) meesturen. Bewust geen eval(), geen externe
// library en geen inline script: de productie-CSP van Azure Static Web Apps staat alleen
// 'script-src self wasm-unsafe-eval' toe (#659). Wordt vóór blazor.webassembly.js geladen, zodat ook
// opstartfouten worden vastgelegd.
//
// Wat hier NIET wordt bewaard: formulierinhoud, schermafbeeldingen, localStorage of cookies. Alleen
// de tekst van de fout, afgekapt; de server en de browser (FeedbackRedactie) redigeren e-mailadressen,
// ID's, tokens en querystrings voordat er iets wordt getoond of verstuurd.
(function () {
    'use strict';

    var MAX = 5;
    var MAX_TEKENS = 300;
    var fouten = [];

    function voegToe(tekst) {
        try {
            var t = String(tekst == null ? '' : tekst).replace(/\s+/g, ' ').trim();
            if (!t) return;
            if (t.length > MAX_TEKENS) t = t.substring(0, MAX_TEKENS) + '…';
            fouten.push(t);
            while (fouten.length > MAX) fouten.shift();
        } catch (e) { /* de ringbuffer mag de pagina nooit breken */ }
    }

    function bestandsnaam(url) {
        // Alleen de bestandsnaam, nooit het volledige pad of de querystring.
        try { return String(url || '').split('?')[0].split('#')[0].split('/').pop(); } catch (e) { return ''; }
    }

    window.addEventListener('error', function (e) {
        voegToe((e && e.message ? e.message : 'Fout') + (e && e.filename ? ' (' + bestandsnaam(e.filename) + ')' : ''));
    });

    window.addEventListener('unhandledrejection', function (e) {
        var reden = e && e.reason;
        voegToe('Onverwerkte belofte: ' + (reden && reden.message ? reden.message : reden));
    });

    // Blazor meldt onverwerkte uitzonderingen via console.error; die wil de beheerder zien.
    var origineel = console.error;
    console.error = function () {
        try { voegToe(Array.prototype.slice.call(arguments).map(String).join(' ')); } catch (e) { }
        return origineel.apply(console, arguments);
    };

    window.feedbackTelemetry = {
        consoleFouten: function () { return fouten.slice(); },
        schermbreedte: function () { return window.innerWidth || 0; }
    };
})();
