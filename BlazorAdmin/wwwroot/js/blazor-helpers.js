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
    }
};
