// JS-helpers voor Blazor interop.
//
// Dit stond eerder als inline <script> in index.html. De productie-CSP van Azure Static Web Apps
// staat 'script-src self wasm-unsafe-eval' toe — géén 'unsafe-inline'. Inline scripts worden daar
// dus geblokkeerd, waardoor window.blazorHelpers niet bestond en clipboard, downloadHtml en
// getUserAgent stil faalden. Een extern bestand van dezelfde origin valt onder 'self' en mag wel.
// Zie #659. Lokaal was dit onzichtbaar: de dev-server past staticwebapp.config.json niet toe.
var veldOptimalisatieLayouts = new WeakMap();

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
    downloadBytes: function (filename, base64, mimeType) {
        var bin = atob(base64);
        var bytes = new Uint8Array(bin.length);
        for (var i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
        var blob = new Blob([bytes], { type: mimeType || 'application/octet-stream' });
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
    },
    // Resizable two-pane layout for Veld optimalisatie. Keyboard handling is scoped to the
    // separator and selectable Gantt blocks so Tab navigation keeps its native behavior.
    initializeVeldOptimalisatie: function (layout, separator) {
        if (!layout || !separator || !layout.isConnected || !separator.isConnected
            || veldOptimalisatieLayouts.has(layout)) return;
        var upperPane = layout.querySelector('.veldoptimalisatie-boven-paneel');
        var userAdjusted = false;
        var lastDocumentTop = null;
        var layoutCheckFrame = null;
        var destroyed = false;

        function bounds() {
            var usable = Math.max(280, layout.clientHeight - separator.offsetHeight);
            var minBottom = Math.min(window.innerWidth <= 640 ? 220 : 180, usable * 0.55);
            var max = usable - minBottom;
            var min = Math.min(max, Math.max(160, usable * 0.2));
            return { usable: usable, min: min, max: max };
        }

        function setHeight(value) {
            if (destroyed) return;
            var limits = bounds();
            var height = Math.max(limits.min, Math.min(limits.max, value));
            layout.style.setProperty('--veld-boven-hoogte', height + 'px');
            separator.setAttribute('aria-valuemin', String(Math.round(limits.min / limits.usable * 100)));
            separator.setAttribute('aria-valuemax', String(Math.round(limits.max / limits.usable * 100)));
            separator.setAttribute('aria-valuenow', String(Math.round(height / limits.usable * 100)));
            return height;
        }

        function fitViewport() {
            if (destroyed) return;
            var top = layout.getBoundingClientRect().top + window.scrollY;
            lastDocumentTop = top;
            layout.style.setProperty('--veld-layout-height', Math.max(280, window.innerHeight - top - 8) + 'px');
            var current = Number.parseFloat(getComputedStyle(layout).getPropertyValue('--veld-boven-hoogte'));
            setHeight(Number.isFinite(current) ? current : bounds().usable * (window.innerWidth <= 640 ? 0.44 : 0.62));
        }

        function fitPlanContent() {
            if (destroyed || !upperPane) return;
            if (!upperPane.querySelector('.veldoptimalisatie-gantt-kaart')) {
                return;
            }
            if (userAdjusted) return;
            var paneTop = upperPane.getBoundingClientRect().top;
            var contentBottom = Array.from(upperPane.children).reduce(function (bottom, child) {
                var margin = Number.parseFloat(getComputedStyle(child).marginBottom) || 0;
                return Math.max(bottom, child.getBoundingClientRect().bottom - paneTop + upperPane.scrollTop + margin);
            }, 0);
            setHeight(contentBottom + 4);
        }

        function checkDocumentTop() {
            if (!layout.isConnected) {
                window.blazorHelpers.destroyVeldOptimalisatie(layout);
                return;
            }
            if (destroyed || layoutCheckFrame !== null) return;
            layoutCheckFrame = requestAnimationFrame(function () {
                layoutCheckFrame = null;
                if (destroyed || !layout.isConnected) return;
                var documentTop = layout.getBoundingClientRect().top + window.scrollY;
                if (Math.abs(documentTop - lastDocumentTop) > 1) fitViewport();
            });
        }

        var dragging = false;
        var pendingTouchActivation = null;
        var touchActivationWindowMs = 750;
        var touchMovementTolerance = 12;

        function blockForTarget(target) {
            return target && target.closest ? target.closest('.gantt-blok[role="button"]') : null;
        }

        function clearTouchActivation() {
            pendingTouchActivation = null;
        }

        function touchPointerDown(event) {
            // A new pointer invalidates an unfinished tap, including a second touch or mouse.
            clearTouchActivation();
            var block = blockForTarget(event.target);
            if (event.pointerType !== 'touch' || event.isPrimary === false || !block) return;
            pendingTouchActivation = {
                pointerId: event.pointerId,
                block: block,
                x: event.clientX,
                y: event.clientY,
                ended: false,
                startedAt: performance.now()
            };
        }

        function touchPointerMove(event) {
            var touch = pendingTouchActivation;
            if (!touch) return;
            if (event.pointerId !== touch.pointerId || blockForTarget(event.target) !== touch.block
                || Math.hypot(event.clientX - touch.x, event.clientY - touch.y) > touchMovementTolerance) {
                clearTouchActivation();
            }
        }

        function touchPointerUp(event) {
            var touch = pendingTouchActivation;
            if (!touch) return;
            if (event.pointerId !== touch.pointerId || blockForTarget(event.target) !== touch.block
                || Math.hypot(event.clientX - touch.x, event.clientY - touch.y) > touchMovementTolerance
                || performance.now() - touch.startedAt > touchActivationWindowMs) {
                clearTouchActivation();
                return;
            }
            touch.ended = true;
            touch.endedAt = performance.now();
        }

        function touchPointerCancel() {
            clearTouchActivation();
        }

        function pointerDown(event) {
            if (event.button !== 0) return;
            userAdjusted = true;
            dragging = true;
            separator.classList.add('is-resizing');
            separator.setPointerCapture(event.pointerId);
            event.preventDefault();
        }
        function pointerMove(event) {
            if (dragging) setHeight(event.clientY - layout.getBoundingClientRect().top);
        }
        function pointerEnd(event) {
            if (!dragging) return;
            dragging = false;
            separator.classList.remove('is-resizing');
            if (separator.hasPointerCapture(event.pointerId)) separator.releasePointerCapture(event.pointerId);
        }
        function keyDown(event) {
            if (event.target === separator) {
                var limits = bounds();
                var current = Number.parseFloat(getComputedStyle(layout).getPropertyValue('--veld-boven-hoogte')) || limits.usable * 0.48;
                var step = event.shiftKey ? 64 : 24;
                var next;
                switch (event.key) {
                    case 'ArrowUp': case 'PageUp': next = current - step; break;
                    case 'ArrowDown': case 'PageDown': next = current + step; break;
                    case 'Home': next = limits.min; break;
                    case 'End': next = limits.max; break;
                    default: return;
                }
                event.preventDefault();
                userAdjusted = true;
                setHeight(next);
                return;
            }
            if (event.target.closest && event.target.closest('.gantt-blok[role="button"]')
                && (event.key === ' ' || event.key === 'Enter')) {
                event.preventDefault();
            }
        }

        function layoutClick(event) {
            if (event.target.closest('[data-veld-skip-splitter]')) separator.focus({ preventScroll: true });
            var block = blockForTarget(event.target);
            var touch = pendingTouchActivation;
            if (event.detail === 0) {
                clearTouchActivation();
                return;
            }
            if (!touch) return;
            if (!block) {
                clearTouchActivation();
                return;
            }
            var isCompletedTap = touch.ended && touch.block === block
                && performance.now() - touch.endedAt <= touchActivationWindowMs;
            clearTouchActivation();
            // Safari may label a touch-origin click as "mouse", and older browsers may emit a
            // MouseEvent without pointerType. The preceding same-block touch sequence is the
            // source of truth; never promote an arbitrary mouse or unknown click.
            if (isCompletedTap) block.click();
        }
        layout.addEventListener('click', layoutClick);
        layout.addEventListener('pointerdown', touchPointerDown);
        layout.addEventListener('pointermove', touchPointerMove);
        layout.addEventListener('pointerup', touchPointerUp);
        layout.addEventListener('pointercancel', touchPointerCancel);
        separator.addEventListener('pointerdown', pointerDown);
        separator.addEventListener('pointermove', pointerMove);
        separator.addEventListener('pointerup', pointerEnd);
        separator.addEventListener('pointercancel', pointerEnd);
        layout.addEventListener('keydown', keyDown, true);
        window.addEventListener('resize', fitViewport);
        fitViewport();
        // Blazor banners and the responsive navigation above this view can change without a
        // window resize. Observe structural and class changes, but write the height only if the
        // layout's document position actually moved.
        var pageObserver = new MutationObserver(checkDocumentTop);
        pageObserver.observe(document.body, {
            attributes: true,
            attributeFilter: ['class'],
            childList: true,
            subtree: true,
            characterData: true
        });
        var planObserver = upperPane ? new MutationObserver(fitPlanContent) : null;
        if (planObserver) {
            planObserver.observe(upperPane, { childList: true, subtree: true });
            fitPlanContent();
        }
        veldOptimalisatieLayouts.set(layout, function () {
            if (destroyed) return;
            destroyed = true;
            if (layoutCheckFrame !== null) cancelAnimationFrame(layoutCheckFrame);
            layoutCheckFrame = null;
            layout.removeEventListener('click', layoutClick);
            layout.removeEventListener('pointerdown', touchPointerDown);
            layout.removeEventListener('pointermove', touchPointerMove);
            layout.removeEventListener('pointerup', touchPointerUp);
            layout.removeEventListener('pointercancel', touchPointerCancel);
            separator.removeEventListener('pointerdown', pointerDown);
            separator.removeEventListener('pointermove', pointerMove);
            separator.removeEventListener('pointerup', pointerEnd);
            separator.removeEventListener('pointercancel', pointerEnd);
            layout.removeEventListener('keydown', keyDown, true);
            window.removeEventListener('resize', fitViewport);
            pageObserver.disconnect();
            if (planObserver) planObserver.disconnect();
        });
    },
    destroyVeldOptimalisatie: function (layout) {
        var cleanup = layout && veldOptimalisatieLayouts.get(layout);
        if (cleanup) {
            cleanup();
            veldOptimalisatieLayouts.delete(layout);
        }
    },
    // Adjusts only the table's own scroll offset. scrollIntoView would also move the page and
    // could hide the controls, so calculate the relative offset and write to this container only.
    scrollWedstrijdInLijst: function (container, rowId) {
        if (!container) return;
        var row = document.getElementById(rowId);
        if (!row || !container.contains(row)) return;
        var containerRect = container.getBoundingClientRect();
        var rowRect = row.getBoundingClientRect();
        // The individual cells are sticky; the thead itself scrolls out of view.
        var visibleTop = Array.from(container.querySelectorAll('thead th')).reduce(function (bottom, cell) {
            return Math.max(bottom, cell.getBoundingClientRect().bottom);
        }, containerRect.top);
        if (rowRect.top < visibleTop) container.scrollTop += rowRect.top - visibleTop;
        else if (rowRect.bottom > containerRect.bottom) container.scrollTop += rowRect.bottom - containerRect.bottom;
    }
};
