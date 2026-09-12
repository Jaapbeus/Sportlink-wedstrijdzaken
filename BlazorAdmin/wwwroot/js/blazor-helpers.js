// JS-helpers voor Blazor interop.
//
// Dit stond eerder als inline <script> in index.html. De productie-CSP van Azure Static Web Apps
// staat 'script-src self wasm-unsafe-eval' toe — géén 'unsafe-inline'. Inline scripts worden daar
// dus geblokkeerd, waardoor window.blazorHelpers niet bestond en clipboard, downloadHtml en
// getUserAgent stil faalden. Een extern bestand van dezelfde origin valt onder 'self' en mag wel.
// Zie #659. Lokaal was dit onzichtbaar: de dev-server past staticwebapp.config.json niet toe.
window.blazorHelpers = {
    copyToClipboard: function (text) {
        return navigator.clipboard.writeText(text);
    },
    downloadHtml: function (filename, content) {
        var blob = new Blob([content], { type: 'text/html' });
        var url = URL.createObjectURL(blob);
        var a = document.createElement('a');
        a.href = url;
        a.download = filename;
        document.body.appendChild(a);
        a.click();
        document.body.removeChild(a);
        URL.revokeObjectURL(url);
    },
    // Dedicated helpers i.p.v. JS eval(): de productie-CSP staat alleen 'wasm-unsafe-eval' toe,
    // niet 'unsafe-eval'. eval() gooit daar een EvalError. (#597)
    getUserAgent: function () {
        return navigator.userAgent || '';
    },
    // Breedte en linkerrand van een element in pixels (#666). Nodig om een sleepactie in de
    // dagplanning-tijdlijn om te rekenen naar minuten: de tijdlijn is procentueel opgemaakt, dus
    // zonder de werkelijke pixelbreedte valt een drop-positie niet naar een tijdstip te herleiden.
    getElementRect: function (id) {
        var el = document.getElementById(id);
        if (!el) return null;
        var r = el.getBoundingClientRect();
        // top/height erbij: de verticale droppositie bepaalt op welke kwartbaan van het veld
        // (A1/A2/B1/B2) een wedstrijd terechtkomt.
        return { left: r.left, width: r.width, top: r.top, height: r.height };
    },
    // #989: deep-link naar club.sportlink.com — 'noopener' i.p.v. rel="noopener" op een <a>,
    // want de URL is pas bekend na een async API-call, niet vooraf in de markup beschikbaar.
    openInNewTab: function (url) {
        window.open(url, '_blank', 'noopener');
    }
};
