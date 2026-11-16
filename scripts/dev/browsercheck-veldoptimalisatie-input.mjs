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

export async function checkTouch({ browser, base, stubRoute, ok }) {
  const touchContext = await browser.newContext({ viewport: { width: 900, height: 700 }, hasTouch: true });
  await touchContext.addInitScript(() => localStorage.setItem('selectedClubCode', 'ALLSTARS'));
  await touchContext.route('**/*', stubRoute);
  const touchPage = await touchContext.newPage();
  await touchPage.goto(`${base}/veld-optimalisatie`, { waitUntil: 'domcontentloaded' });
  await touchPage.getByText('Synthetic wedstrijd 01', { exact: true }).waitFor({ timeout: 60000 });
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
