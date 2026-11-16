// Input checks for #1978: browser emulation, not a real screenreader or touch device.
export async function checkActivation({ page, block, wedstrijden, ok }) {
  await block(1).click();
  ok((await block(1).getAttribute('aria-pressed')) === 'false',
    'a single mouse click keeps double-click selection semantics');
  await block(1).evaluate(el => el.click());
  await page.waitForFunction(code => document.querySelector(`.gantt-blok[data-wedstrijd-code="${code}"]`)
    ?.getAttribute('aria-pressed') === 'true', wedstrijden[1].wedstrijdCode);
  ok((await block(1).getAttribute('aria-pressed')) === 'true',
    'assistive synthesized click selects the draggable Optimaal block');
  await page.getByRole('button', { name: /Huidig/ }).click();
  await block(2).evaluate(el => el.click());
  await page.waitForFunction(code => document.querySelector(`.gantt-blok[data-wedstrijd-code="${code}"]`)
    ?.getAttribute('aria-pressed') === 'true', wedstrijden[2].wedstrijdCode);
  ok((await block(2).getAttribute('aria-pressed')) === 'true',
    'assistive synthesized click selects the Huidig block');
  await page.getByRole('button', { name: /Optimaal/ }).click();

 }

export async function checkSkipShortcutFocus({ browser, base, stubRoute, ok, artifactDir }) {
  const scenarios = [
    { name: 'mobile 360x640', width: 360, height: 640, resize: false },
    { name: 'desktop after manual resize', width: 1360, height: 900, resize: true }
  ];
  for (const scenario of scenarios) {
    const context = await browser.newContext({ viewport: { width: scenario.width, height: scenario.height } });
    await context.addInitScript(() => localStorage.setItem('selectedClubCode', 'ALLSTARS'));
    await context.route('**/*', stubRoute);
    const page = await context.newPage();
    try {
      await page.goto(`${base}/veld-optimalisatie`, { waitUntil: 'domcontentloaded', timeout: 60000 });
      await page.getByText('Synthetic wedstrijd 01', { exact: true }).waitFor({ timeout: 60000 });
      const pane = page.locator('.veldoptimalisatie-boven-paneel');
      const splitter = page.locator('.veldoptimalisatie-splitter');
      const shortcut = page.locator('[data-veld-skip-splitter]');
      let resize = null;
      if (scenario.resize) {
        const before = Number(await splitter.getAttribute('aria-valuenow'));
        const box = await splitter.boundingBox();
        if (!box) throw new Error(`${scenario.name}: splitter has no bounding box`);
        const x = box.x + box.width / 2;
        const y = box.y + box.height / 2;
        await page.mouse.move(x, y);
        await page.mouse.down();
        await page.mouse.move(x, y - 210, { steps: 12 });
        await page.mouse.up();
        const after = Number(await splitter.getAttribute('aria-valuenow'));
        resize = { before, after };
        ok(after < before, `${scenario.name}: pointer drag manually shrinks the upper pane (${before} -> ${after})`);
      }
      await pane.evaluate(el => { el.scrollTop = el.scrollHeight; });
      await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
      const before = await pane.evaluate(el => ({ scrollTop: el.scrollTop, scrollHeight: el.scrollHeight, clientHeight: el.clientHeight }));
      ok(before.scrollTop > 0, `${scenario.name}: upper pane is scrolled before keyboard navigation (${JSON.stringify(before)})`);
      let tabs = 0;
      let focused = false;
      while (tabs < 40 && !focused) {
        await page.keyboard.press('Tab');
        tabs++;
        focused = await shortcut.evaluate(el => el === document.activeElement);
      }
      const after = await page.evaluate(() => {
        const pane = document.querySelector('.veldoptimalisatie-boven-paneel');
        const button = document.querySelector('[data-veld-skip-splitter]');
        const rect = button.getBoundingClientRect();
        const paneRect = pane.getBoundingClientRect();
        const inViewport = rect.left >= 0 && rect.top >= 0 && rect.right <= innerWidth && rect.bottom <= innerHeight;
        const inPane = rect.left >= paneRect.left && rect.top >= paneRect.top
          && rect.right <= paneRect.right && rect.bottom <= paneRect.bottom;
        const centerInsideViewport = rect.width > 0 && rect.height > 0
          && rect.left + rect.width / 2 >= 0 && rect.left + rect.width / 2 < innerWidth
          && rect.top + rect.height / 2 >= 0 && rect.top + rect.height / 2 < innerHeight;
        const hit = centerInsideViewport ? document.elementFromPoint(rect.left + rect.width / 2, rect.top + rect.height / 2) : null;
        return { viewport: { width: innerWidth, height: innerHeight }, pane: paneRect.toJSON(), button: rect.toJSON(),
          scrollTop: pane.scrollTop, focused: button === document.activeElement, focusVisible: button.matches(':focus-visible'),
          inViewport, inPane, hitTestShortcut: hit === button || button.contains(hit), hitTag: hit?.tagName ?? null };
      });
      const artifactName = scenario.resize ? 'shortcut-focus-desktop-resized.png' : 'shortcut-focus-mobile-360x640.png';
      await page.screenshot({ path: `${artifactDir}/${artifactName}` });
      console.log(`INFO skip-shortcut focus ${JSON.stringify({ scenario: scenario.name, resize, before, tabs, ...after })}`);
      ok(focused && after.focusVisible, `${scenario.name}: Tab reaches the skip shortcut with keyboard focus visible`);
      ok(after.inViewport && after.inPane && after.hitTestShortcut,
        `${scenario.name}: focused skip shortcut is inside the viewport and pane and receives the center hit-test (${JSON.stringify({ button: after.button, pane: after.pane, hitTag: after.hitTag })})`);
    } finally {
      await context.close();
    }
  }
}

export async function checkMobileMenuLayout({ browser, base, stubRoute, ok, artifactDir }) {
  const context = await browser.newContext({ viewport: { width: 390, height: 844 } });
  await context.addInitScript(() => localStorage.setItem('selectedClubCode', 'ALLSTARS'));
  await context.route('**/*', stubRoute);
  const page = await context.newPage();
  try {
    await page.goto(`${base}/veld-optimalisatie`, { waitUntil: 'domcontentloaded', timeout: 60000 });
    await page.getByText('Synthetic wedstrijd 01', { exact: true }).waitFor({ timeout: 60000 });
    const rows = page.locator('.veldoptimalisatie-tabel-scroll tbody tr');
    const rowCount = await rows.count();
    ok(rowCount === 36, `mobile menu height fixture renders all 36 synthetic rows (found ${rowCount})`);

    const menuToggle = page.getByRole('button', { name: 'Navigation menu' });
    await menuToggle.click();
    const adminGroup = page.getByRole('button', { name: /Admin/i }).first();
    await adminGroup.waitFor({ state: 'visible' });
    await adminGroup.click();
    const expanded = await adminGroup.getAttribute('aria-expanded');
    await page.waitForFunction(() => {
      const layout = document.querySelector('.veldoptimalisatie-layout');
      const top = layout.getBoundingClientRect().top + window.scrollY;
      const expected = Math.max(280, window.innerHeight - top - 8);
      const actual = Number.parseFloat(getComputedStyle(layout).getPropertyValue('--veld-layout-height'));
      return top > window.innerHeight && Math.abs(actual - expected) <= 2;
    }, null, { timeout: 10000 });
    const expandedMenuSizing = await page.evaluate(() => {
      const layout = document.querySelector('.veldoptimalisatie-layout');
      const top = layout.getBoundingClientRect().top + window.scrollY;
      return {
        documentTop: top,
        expectedHeight: Math.max(280, window.innerHeight - top - 8),
        layoutHeightVariable: Number.parseFloat(getComputedStyle(layout).getPropertyValue('--veld-layout-height'))
      };
    });
    await menuToggle.click();
    await page.waitForTimeout(500);

    const afterClose = await page.evaluate(() => {
      const layout = document.querySelector('.veldoptimalisatie-layout');
      const list = document.querySelector('.veldoptimalisatie-tabel-scroll');
      const top = layout.getBoundingClientRect().top + window.scrollY;
      return {
        viewport: { width: window.innerWidth, height: window.innerHeight },
        documentTop: top,
        expectedHeight: Math.max(280, window.innerHeight - top - 8),
        layoutHeight: layout.getBoundingClientRect().height,
        layoutHeightVariable: Number.parseFloat(getComputedStyle(layout).getPropertyValue('--veld-layout-height')),
        listHeight: list.getBoundingClientRect().height,
        listClientHeight: list.clientHeight,
        listScrollHeight: list.scrollHeight,
        unusedBottom: window.innerHeight - layout.getBoundingClientRect().bottom
      };
    });
    await page.screenshot({ path: `${artifactDir}/mobile-menu-height-390x844.png` });
    ok(expanded === 'true', 'mobile navigation Admin group expands before menu close');
    ok(Math.abs(expandedMenuSizing.layoutHeightVariable - expandedMenuSizing.expectedHeight) <= 2,
      `expanded mobile menu settles before closing (${JSON.stringify(expandedMenuSizing)})`);
    ok(Math.abs(afterClose.layoutHeightVariable - afterClose.expectedHeight) <= 2,
      `closing mobile navigation recalculates available layout height without resize (${JSON.stringify(afterClose)})`);
    ok(Math.abs(afterClose.layoutHeight - afterClose.expectedHeight) <= 2,
      `mobile layout fills the available height after menu close (${JSON.stringify(afterClose)})`);
    ok(afterClose.listClientHeight > 300,
      `mobile match list retains its usable height after menu close (${afterClose.listClientHeight}px)`);

    const lastRow = page.locator('#wedstrijd-1978035');
    const lastRowGeometry = await page.evaluate(() => {
      const list = document.querySelector('.veldoptimalisatie-tabel-scroll');
      const row = document.querySelector('#wedstrijd-1978035');
      list.scrollTop = list.scrollHeight;
      const pane = list.getBoundingClientRect();
      const rect = row.getBoundingClientRect();
      const hit = document.elementFromPoint((rect.left + rect.right) / 2, (rect.top + rect.bottom) / 2);
      return {
        scrollTop: list.scrollTop,
        scrollHeight: list.scrollHeight,
        pane: { top: pane.top, bottom: pane.bottom },
        row: { top: rect.top, bottom: rect.bottom },
        rowHitTest: hit?.closest('tr')?.id === row.id
      };
    });
    ok(await lastRow.count() === 1 && lastRowGeometry.rowHitTest
      && lastRowGeometry.row.top >= lastRowGeometry.pane.top - 1
      && lastRowGeometry.row.bottom <= lastRowGeometry.pane.bottom + 1,
    `last synthetic row remains reachable by scrolling the match list (${JSON.stringify(lastRowGeometry)})`);
  } finally {
    await context.close();
  }
}

export async function checkTouch({ browser, base, stubRoute, ok }) {
  const touchContext = await browser.newContext({ viewport: { width: 900, height: 700 }, hasTouch: true });
  await touchContext.addInitScript(() => localStorage.setItem('selectedClubCode', 'ALLSTARS'));
  await touchContext.route('**/*', stubRoute);
  const touchPage = await touchContext.newPage();
  await touchPage.goto(`${base}/veld-optimalisatie`, { waitUntil: 'domcontentloaded' });
  await touchPage.getByText('Synthetic wedstrijd 01', { exact: true }).waitFor({ timeout: 60000 });
  const blockCount = await touchPage.locator('.gantt-blok').count();
  if (blockCount < 13) throw new Error(`Touch fixture needs at least 13 Gantt blocks; found ${blockCount}`);
  for (const [index, tab] of ['Huidig', 'Optimaal'].entries()) {
    await touchPage.getByRole('button', { name: new RegExp(tab) }).tap();
    const touchBlock = touchPage.locator('.gantt-blok').nth(index);
    await touchBlock.tap();
    await touchPage.waitForFunction(index => document.querySelectorAll('.gantt-blok')[index]?.getAttribute('aria-pressed') === 'true', index);
    ok(await touchBlock.getAttribute('aria-pressed') === 'true', `emulated touch tap selects the ${tab} block`);
  }
  // A touch pan produces pointercancel instead of click. Use actual Chromium touch input.
  const panBlock = touchPage.locator('.gantt-blok').nth(2);
  await touchPage.evaluate(() => {
    window.__touchPan = { cancelled: 0, clicked: 0 };
    document.addEventListener('pointercancel', () => window.__touchPan.cancelled++);
    document.addEventListener('click', () => window.__touchPan.clicked++);
  });
  await panBlock.scrollIntoViewIfNeeded();
  const panBox = await panBlock.boundingBox();
  const session = await touchContext.newCDPSession(touchPage);
  const x = panBox.x + panBox.width / 2, y = panBox.y + panBox.height / 2;
  await session.send('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: [{ x, y }] });
  await session.send('Input.dispatchTouchEvent', { type: 'touchMove', touchPoints: [{ x, y: y - 80 }] });
  await session.send('Input.dispatchTouchEvent', { type: 'touchEnd', touchPoints: [] });
  const panEvents = await touchPage.evaluate(() => window.__touchPan);
  ok(panEvents.cancelled > 0 && panEvents.clicked === 0 && await panBlock.getAttribute('aria-pressed') === 'false',
    `emulated touch pan cancels pointer activation without selecting another block (${JSON.stringify(panEvents)})`);

  const dispatchSequence = async (index, events) => touchPage.locator('.gantt-blok').nth(index).evaluate((block, sequence) => {
    const delivered = [];
    const dispatch = (type, pointerType, x = 10, y = 10, pointerId = 77) => {
      const init = { bubbles: true, cancelable: true, pointerId, pointerType, isPrimary: true,
        button: 0, clientX: x, clientY: y, detail: 1 };
      const event = type.startsWith('pointer') || pointerType !== null
        ? new PointerEvent(type, init) : new MouseEvent(type, init);
      delivered.push({ type, constructor: event.constructor.name, pointerType: event.pointerType ?? null });
      block.dispatchEvent(event);
    };
    for (const [type, pointerType, x, y, pointerId] of sequence) dispatch(type, pointerType, x, y, pointerId);
    return delivered;
  }, events);
  const pressed = index => touchPage.locator('.gantt-blok').nth(index).getAttribute('aria-pressed');

  // A MouseEvent click has no pointerType in older engines. A prior completed same-block
  // touch sequence must still forward it as detail=0 for the Blazor selection handler.
  await dispatchSequence(3, [['pointerdown', 'touch'], ['pointerup', 'touch'], ['click', null]]);
  await touchPage.waitForFunction(() => document.querySelectorAll('.gantt-blok')[3]?.getAttribute('aria-pressed') === 'true');
  ok(await pressed(3) === 'true', 'same-block touch sequence accepts a MouseEvent click without pointerType');

  // WebKit can expose the click as PointerEvent but report pointerType=mouse.
  const mislabeled = await dispatchSequence(4, [['pointerdown', 'touch'], ['pointerup', 'touch'], ['click', 'mouse']]);
  await touchPage.waitForFunction(() => document.querySelectorAll('.gantt-blok')[4]?.getAttribute('aria-pressed') === 'true');
  ok(await pressed(4) === 'true' && mislabeled[2].constructor === 'PointerEvent'
    && mislabeled[2].pointerType === 'mouse',
  `same-block touch sequence accepts a PointerEvent click mislabeled as mouse (${JSON.stringify(mislabeled)})`);

  await dispatchSequence(5, [['pointerdown', 'mouse'], ['pointerup', 'mouse'], ['click', null]]);
  ok(await pressed(5) === 'false', 'ordinary mouse down/up and single click remain inert');
  await dispatchSequence(5, [['click', null]]);
  ok(await pressed(5) === 'false', 'detail-one click without a touch sequence remains inert');
  await touchPage.locator('.gantt-blok').nth(5).evaluate(el => el.dispatchEvent(new MouseEvent('click', { bubbles: true, detail: 0 })));
  await touchPage.waitForFunction(() => document.querySelectorAll('.gantt-blok')[5]?.getAttribute('aria-pressed') === 'true');
  ok(await pressed(5) === 'true', 'detail-zero assistive click remains active');

  await dispatchSequence(6, [['pointerdown', 'touch'], ['pointercancel', 'touch'], ['click', null]]);
  ok(await pressed(6) === 'false', 'pointercancel clears the tap before a stray click');
  await dispatchSequence(6, [['pointerdown', 'touch'], ['pointermove', 'touch', 40, 10],
    ['pointerup', 'touch', 40, 10], ['click', null, 40, 10]]);
  ok(await pressed(6) === 'false', 'touch movement beyond tolerance cannot activate after pointerup');
  await dispatchSequence(6, [['pointerdown', 'touch'], ['pointerup', 'touch'], ['click', null]]);
  await touchPage.waitForFunction(() => document.querySelectorAll('.gantt-blok')[6]?.getAttribute('aria-pressed') === 'true');
  await dispatchSequence(7, [['pointerdown', 'touch']]);
  await touchPage.locator('.gantt-blok').nth(8).evaluate(el => el.dispatchEvent(new PointerEvent('pointerup', {
    bubbles: true, pointerId: 77, pointerType: 'touch', isPrimary: true, clientX: 10, clientY: 10
  })));
  await dispatchSequence(7, [['click', null]]);
  ok(await pressed(7) === 'false', 'pointerup on a different block clears the original tap');

  await dispatchSequence(8, [['pointerdown', 'touch'], ['pointerdown', 'touch', 10, 10, 78],
    ['pointerup', 'touch'], ['click', null]]);
  ok(await pressed(8) === 'false', 'a second pointer invalidates the first tap');

  await dispatchSequence(9, [['pointerdown', 'touch'], ['pointerup', 'touch']]);
  await touchPage.waitForTimeout(800);
  await dispatchSequence(9, [['click', null]]);
  ok(await pressed(9) === 'false', 'a delayed click cannot reuse an expired touch sequence');

  await touchPage.locator('.gantt-blok').nth(10).evaluate(block => {
    block.dataset.forwardedClicks = '0';
    block.addEventListener('click', event => {
      if (event.detail === 0) block.dataset.forwardedClicks = String(Number(block.dataset.forwardedClicks) + 1);
    });
  });
  await dispatchSequence(10, [['pointerdown', 'touch'], ['pointerup', 'touch'], ['click', null]]);
  await touchPage.waitForFunction(() => document.querySelectorAll('.gantt-blok')[10]?.getAttribute('aria-pressed') === 'true');
  ok(await touchPage.locator('.gantt-blok').nth(10).getAttribute('data-forwarded-clicks') === '1',
    'completed touch forwards exactly one detail-zero activation');
  await dispatchSequence(10, [['click', null]]);
  ok(await touchPage.locator('.gantt-blok').nth(10).getAttribute('data-forwarded-clicks') === '1',
    'a second detail-one click on the same block cannot reuse the completed touch');
  await dispatchSequence(11, [['click', null]]);
  ok(await pressed(11) === 'false', 'a completed touch sequence does not transfer to another block');

  const assistiveFollowUp = await touchPage.locator('.gantt-blok').nth(12).evaluate(block => {
    let detailZeroClicks = 0;
    block.addEventListener('click', event => { if (event.detail === 0) detailZeroClicks++; });
    const pointer = (type, pointerType) => block.dispatchEvent(new PointerEvent(type, {
      bubbles: true, pointerId: 77, pointerType, isPrimary: true, clientX: 10, clientY: 10
    }));
    pointer('pointerdown', 'touch');
    pointer('pointerup', 'touch');
    block.dispatchEvent(new MouseEvent('click', { bubbles: true, detail: 0 }));
    block.dispatchEvent(new MouseEvent('click', { bubbles: true, detail: 1 }));
    return detailZeroClicks;
  });
  ok(assistiveFollowUp === 1, 'assistive activation consumes any pending touch before a later detail-one click');
  await session.detach();
  await touchContext.close();

 }

export async function checkTheme({ page, theme, ok }) {
  const currentTheme = await page.locator('html').getAttribute('data-theme');
  if (currentTheme !== theme) {
    await page.getByRole('button', { name: currentTheme === 'dark'
      ? 'Overschakelen naar lichte weergave' : 'Overschakelen naar donkere weergave' }).click();
  }
  await page.waitForFunction(value => document.documentElement.getAttribute('data-theme') === value
    && document.documentElement.getAttribute('data-bs-theme') === value, theme);
  const snapshot = await page.locator('.kaart-thema-bg').first().evaluate(el => {
    const color = document.createElement('span');
    color.style.backgroundColor = getComputedStyle(document.documentElement).getPropertyValue('--theme-card-bg').trim();
    document.body.append(color);
    const expectedBackground = getComputedStyle(color).backgroundColor;
    color.remove();
    return { mode: document.documentElement.getAttribute('data-theme'),
      bootstrapMode: document.documentElement.getAttribute('data-bs-theme'),
      cardBackground: getComputedStyle(el).backgroundColor, cardText: getComputedStyle(el).color,
      expectedBackground };
  });
  ok(snapshot.mode === theme && snapshot.bootstrapMode === theme
    && snapshot.cardBackground === snapshot.expectedBackground,
  `real theme control applies both attributes and card background in ${theme} (${JSON.stringify(snapshot)})`);
}
