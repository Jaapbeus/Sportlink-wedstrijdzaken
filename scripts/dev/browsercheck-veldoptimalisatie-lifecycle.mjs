// Resource checks observe additions before removals, so zero counts cannot pass vacuously.
export async function checkLifecycle({ browser, base, stubRoute, ok }) {
  const context = await browser.newContext();
  await context.route('**/*', stubRoute);
  const errors = [];
  const negativeControl = process.env.LIFECYCLE_NEGATIVE_CONTROL === 'disable-destroy';
  await context.addInitScript(({ negativeControl }) => {
    localStorage.setItem('selectedClubCode', 'ALLSTARS');
    const probe = window.__layoutLifetime = { resize: [], observers: [], delayed: 0, mode: 'ordinary' };
    probe.layoutListeners = [];
    const addEvent = EventTarget.prototype.addEventListener;
    const removeEvent = EventTarget.prototype.removeEventListener;
    EventTarget.prototype.addEventListener = function (type, listener, options) {
      if (this instanceof Element && this.matches('.veldoptimalisatie-layout')) {
        probe.layoutListeners.push({ target: this, type, listener, removed: false });
      }
      return addEvent.call(this, type, listener, options);
    };
    EventTarget.prototype.removeEventListener = function (type, listener, options) {
      const item = probe.layoutListeners.find(item => item.target === this && item.type === type
        && item.listener === listener && !item.removed);
      if (this instanceof Element && this.matches('.veldoptimalisatie-layout') && item) item.removed = true;
      return removeEvent.call(this, type, listener, options);
    };
    const add = window.addEventListener.bind(window), remove = window.removeEventListener.bind(window);
    window.addEventListener = (type, listener, options) => {
      if (type === 'resize') probe.resize.push({ listener, removed: false });
      return add(type, listener, options);
    };
    window.removeEventListener = (type, listener, options) => {
      const item = probe.resize.find(item => item.listener === listener && !item.removed);
      if (type === 'resize' && item) item.removed = true;
      return remove(type, listener, options);
    };
    const NativeObserver = window.MutationObserver;
    window.MutationObserver = function (callback) {
      const relevant = ['checkDocumentTop', 'fitPlanContent'].includes(callback.name);
      const item = { relevant, disconnected: false, observed: false };
      const observer = new NativeObserver(callback);
      const observe = observer.observe.bind(observer), disconnect = observer.disconnect.bind(observer);
      observer.observe = (...args) => { item.observed = true; return observe(...args); };
      observer.disconnect = () => { item.disconnected = true; return disconnect(); };
      probe.observers.push(item);
      return observer;
    };
    window.MutationObserver.prototype = NativeObserver.prototype;
    // Delay the .NET continuation AFTER JS has registered its resources, matching the race.
    let helpers;
    Object.defineProperty(window, 'blazorHelpers', {
      configurable: true,
      get: () => helpers,
      set: value => {
        helpers = value;
        const initialize = helpers.initializeVeldOptimalisatie;
        helpers.initializeVeldOptimalisatie = (...args) => {
          const result = initialize(...args);
          if (probe.mode !== 'delayed') return result;
          probe.delayed++;
          return new Promise(resolve => setTimeout(resolve, 1000));
        };
        if (negativeControl) helpers.destroyVeldOptimalisatie = () => {};
      }
    });
  }, { negativeControl });
  const page = await context.newPage();
  page.on('pageerror', error => errors.push(error.message));
  const snapshot = () => page.evaluate(() => {
    const probe = window.__layoutLifetime;
    const relevant = probe.observers.filter(item => item.relevant && item.observed);
    return { added: probe.resize.length, active: probe.resize.filter(item => !item.removed).length,
      observers: relevant.length, observing: relevant.filter(item => !item.disconnected).length,
      layoutListeners: probe.layoutListeners.length,
      activeLayoutListeners: probe.layoutListeners.filter(item => !item.removed).length,
      removedLayoutListeners: probe.layoutListeners.filter(item => item.removed).length,
      touchListeners: probe.layoutListeners.filter(item => item.type.startsWith('pointer')).length,
      activeTouchListeners: probe.layoutListeners.filter(item => item.type.startsWith('pointer') && !item.removed).length,
      removedTouchListeners: probe.layoutListeners.filter(item => item.type.startsWith('pointer') && item.removed).length,
      delayed: probe.delayed };
  });
  try {
    await page.goto(`${base}/veld-optimalisatie`, { waitUntil: 'domcontentloaded' });
    for (const [index, mode] of ['ordinary', 'delayed'].entries()) {
      if (index) {
        await page.evaluate(() => { window.__layoutLifetime.mode = 'delayed'; });
        await page.getByRole('link', { name: 'Veld optimalisatie', exact: true }).click();
      }
      await page.waitForFunction(count => window.__layoutLifetime.resize.length === count
        && window.__layoutLifetime.observers.filter(item => item.relevant && item.observed).length === count * 2,
      index + 1);
      const entered = await snapshot();
      ok(entered.added === index + 1 && entered.active === 1 && entered.observers === (index + 1) * 2
        && entered.observing === 2 && entered.layoutListeners === (index + 1) * 6
        && entered.activeLayoutListeners === 6 && entered.removedLayoutListeners === index * 6
        && entered.touchListeners === (index + 1) * 4 && entered.activeTouchListeners === 4
        && entered.removedTouchListeners === index * 4
        && (mode !== 'delayed' || entered.delayed === 1),
      `${mode} initialization registers one resize listener, four touch listeners, two other layout listeners and two observers (${JSON.stringify(entered)})`);
      await page.getByRole('link', { name: /Dashboard/ }).click();
      await page.waitForFunction(() => !document.querySelector('.veldoptimalisatie-layout'));
      // This executes within the injected 1s continuation delay.
      await page.waitForTimeout(100);
      const away = await snapshot();
      ok(away.added === index + 1 && away.active === 0 && away.observers === (index + 1) * 2
        && away.observing === 0 && away.activeTouchListeners === 0 && away.activeLayoutListeners === 0
        && away.removedLayoutListeners === (index + 1) * 6
        && away.removedTouchListeners === (index + 1) * 4,
      `${mode} navigation releases all observed layout resources (${JSON.stringify(away)})`);
      if (mode === 'delayed') await page.waitForTimeout(1000);
      const settled = await snapshot();
      ok(settled.active === 0 && settled.observing === 0 && settled.activeTouchListeners === 0
        && settled.activeLayoutListeners === 0,
        `${mode} late continuation does not restore resources`);
    }
    const queued = await page.evaluate(async () => {
      const layout = document.createElement('div');
      layout.innerHTML = '<div class="veldoptimalisatie-boven-paneel"></div><div role="separator"></div>';
      document.body.append(layout);
      let reads = 0, writes = 0, cancelled = false, callback;
      const rect = layout.getBoundingClientRect.bind(layout), set = layout.style.setProperty.bind(layout.style);
      layout.getBoundingClientRect = () => { reads++; return rect(); };
      layout.style.setProperty = (...args) => { writes++; return set(...args); };
      const request = window.requestAnimationFrame, cancel = window.cancelAnimationFrame;
      window.requestAnimationFrame = fn => { callback = fn; return 1978; };
      window.cancelAnimationFrame = id => { if (id === 1978) cancelled = true; };
      try {
        window.__layoutLifetime.mode = 'ordinary';
        window.blazorHelpers.initializeVeldOptimalisatie(layout, layout.lastElementChild);
        const initialReads = reads, initialWrites = writes;
        layout.append(document.createElement('span'));
        await Promise.resolve();
        const wasQueued = typeof callback === 'function';
        layout.remove();
        await Promise.resolve();
        await Promise.resolve();
        const before = { reads, writes };
        // Even an already dispatched callback must be harmless after destruction.
        if (callback) callback();
        window.blazorHelpers.destroyVeldOptimalisatie(layout);
        window.blazorHelpers.destroyVeldOptimalisatie(layout);
        const resourceCount = window.__layoutLifetime.resize.length;
        window.blazorHelpers.initializeVeldOptimalisatie(layout, layout.lastElementChild);
        return { initialReads, initialWrites, wasQueued, cancelled, before, after: { reads, writes },
          detachedRejected: window.__layoutLifetime.resize.length === resourceCount };
      } finally {
        window.requestAnimationFrame = request;
        window.cancelAnimationFrame = cancel;
      }
    });
    ok(queued.initialReads > 0 && queued.initialWrites > 0 && queued.wasQueued && queued.cancelled
      && JSON.stringify(queued.before) === JSON.stringify(queued.after),
    `removal cancels queued frame; forced late callback and repeated destroy do no layout work (${JSON.stringify(queued)})`);
    ok(queued.detachedRejected, 'initialization rejects a detached layout without adding resources');
    ok(errors.length === 0, `ordinary and delayed lifecycle checks have no page errors (${errors.length})`);
  } finally {
    await context.close();
  }
}
