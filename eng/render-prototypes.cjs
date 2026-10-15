/* Temporary visual-review utility. Not a product test or build. Only local HTML resources. */
'use strict';
const { chromium } = require('playwright');
const { pathToFileURL } = require('node:url');
const fs = require('node:fs/promises');
const path = require('node:path');
const assert = require('node:assert/strict');
const root = process.cwd();
const out = path.join(root, 'artifacts/prototype-review');
const errors = [];
const results = [];
const settingsURL = pathToFileURL(path.join(root, 'docs/design/TypeLay_Tray_Settings_Prototype.html')).href;
const overlayURL = pathToFileURL(path.join(root, 'docs/design/TypeLay_UIUX_Prototype.html')).href;
const wait = ms => new Promise(resolve => setTimeout(resolve, ms));
let browser;
async function check(name, action) {
  try {
    await action();
    results.push({ name, result: 'passed' });
    process.stdout.write(`PASS ${name}\n`);
  } catch (error) {
    results.push({ name, result: 'failed', error: error.message });
    process.stderr.write(`FAIL ${name}: ${error.message}\n`);
  }
}
async function pageFor(viewport, extra = {}) {
  const context = await browser.newContext({ viewport, deviceScaleFactor: 1, ...extra });
  const page = await context.newPage();
  page.on('pageerror', e => errors.push(e.message));
  await page.route('**/*', route => {
    const url = route.request().url();
    if (url.startsWith('file:') || url.startsWith('data:')) return route.continue();
    errors.push('Unexpected external resource: ' + url);
    return route.abort();
  });
  return { page, context };
}
async function load(page, url) {
  await page.goto(url);
  await page.evaluate(() => document.fonts.ready);
  await page.waitForFunction(() => typeof window.prototypeState === 'function');
}
async function state(page) { return page.evaluate(() => window.prototypeState()); }
async function shot(page, filename) {
  await page.screenshot({ path: path.join(out, filename), animations: 'disabled' });
}
async function withinViewport(page, selector) {
  const rect = await page.locator(selector).first().boundingBox();
  const view = page.viewportSize();
  assert(rect && rect.x >= -0.5 && rect.y >= -0.5 && rect.x + rect.width <= view.width + 0.5 && rect.y + rect.height <= view.height + 0.5, `${selector} outside ${JSON.stringify(view)}: ${JSON.stringify(rect)}`);
}
async function withinScroll(page, selector, scrollSelector = '#pageScroll') {
  const rect = await page.locator(selector).first().boundingBox();
  const clip = await page.locator(scrollSelector).boundingBox();
  assert(rect && clip && rect.x >= clip.x - 0.5 && rect.y >= clip.y - 0.5 && rect.x + rect.width <= clip.x + clip.width + 0.5 && rect.y + rect.height <= clip.y + clip.height + 0.5, `${selector} clipped inside ${scrollSelector}: ${JSON.stringify({ rect, clip })}`);
}
async function normalContrast(page, selectors) {
  const values = await page.evaluate(selectors => {
    function rgb(value) { const a = value.match(/[\d.]+/g); return a && a.slice(0, 3).map(Number); }
    function luminance(c) { return c.map(x => x / 255).map(x => x <= .04045 ? x / 12.92 : ((x + .055) / 1.055) ** 2.4).reduce((s, x, i) => s + x * [.2126, .7152, .0722][i], 0); }
    return selectors.flatMap(selector => [...document.querySelectorAll(selector)].filter(el => el.getClientRects().length).map(el => {
      let parent = el, background;
      while (parent) { const color = getComputedStyle(parent).backgroundColor; if (color !== 'rgba(0, 0, 0, 0)' && color !== 'transparent') { background = rgb(color); break; } parent = parent.parentElement; }
      const front = rgb(getComputedStyle(el).color);
      const a = luminance(front), b = luminance(background || [255, 255, 255]);
      return { selector, text: el.textContent.trim().slice(0, 40), ratio: (Math.max(a, b) + .05) / (Math.min(a, b) + .05) };
    }));
  }, selectors);
  for (const value of values) assert(value.ratio >= 4.5, `Contrast ${JSON.stringify(value)}`);
  return values;
}
(async () => {
  await fs.mkdir(out, { recursive: true });
  browser = await chromium.launch({ executablePath: process.env.CHROME_BIN || undefined, headless: true, args: ['--no-sandbox'] });
  // Capture the six agreed surfaces first. A later assertion still fails the job, but never
  // prevents the visual reviewer from seeing the other surfaces from this exact draft.
  const captures = [
    [settingsURL, { width: 830, height: 700 }, 'settings-daily-light-790.png'],
    [settingsURL + '?theme=dark', { width: 790, height: 790 }, 'settings-daily-dark-790.png'],
    [settingsURL + '?page=support&setup=failed&connection=conflict', { width: 790, height: 790 }, 'settings-support-recovery-790.png'],
    [settingsURL + '?tray=error', { width: 520, height: 790 }, 'tray-save-error.png'],
    [overlayURL + '?width=320', { width: 520, height: 790 }, 'overlay-preview-320.png'],
    [overlayURL + '?scenario=editing', { width: 520, height: 790 }, 'overlay-editing-520.png']
  ];
  const captureGeometry = [];
  for (const [url, viewport, filename] of captures) {
    const capture = await pageFor(viewport);
    await load(capture.page, url);
    await wait(350);
    await shot(capture.page, filename);
    const selector = filename.startsWith('settings') ? '#settingsWindow' : filename.startsWith('tray') ? '#quickPanel' : '#overlay';
    captureGeometry.push({ filename, viewport, productSurface: await capture.page.locator(selector).boundingBox() });
    await capture.context.close();
  }
  await fs.writeFile(path.join(out, 'capture-geometry.json'), JSON.stringify(captureGeometry, null, 2));
  const settings = await pageFor({ width: 830, height: 700 });
  const p = settings.page;
  await check('settings daily light / no unnecessary save footer', async () => {
    await load(p, settingsURL);
    assert.equal(await p.locator('#excludedApplications').inputValue(), '');
    assert.equal((await state(p)).draft, false);
    assert.equal(await p.locator('#draftBar').isVisible(), false);
    const productWindow = await p.locator('#settingsWindow').boundingBox();
    assert.equal(Math.round(productWindow.width), 790);
    assert.equal(Math.round(productWindow.height), 560);
    await withinViewport(p, '#settingsWindow');
    await withinViewport(p, '#settingsEnable');
    await withinScroll(p, '#settingsEnable');
    await withinScroll(p, '[data-mode=Automatic]');
    await withinScroll(p, '.theme-options');
    await normalContrast(p, ['.setting-name', '.setting-description', '.mode-note', '.nav-button', '.sidebar-bottom']);
  });
  await check('settings daily dark / readable normal text', async () => {
    await load(p, settingsURL + '?theme=dark');
    await normalContrast(p, ['.setting-name', '.setting-description', '.mode-note', '.nav-button', '.sidebar-bottom']);
  });
  await check('support separates model, service, connection and manual conflict confirmation', async () => {
    await load(p, settingsURL + '?page=support&setup=failed&connection=conflict');
    assert.match(await p.locator('#modelFact').textContent(), /准备失败/);
    assert.match(await p.locator('#serviceFact').textContent(), /运行中/);
    assert.match(await p.locator('#connectionFact').textContent(), /冲突/);
    await p.locator('#connectionAction').click();
    assert.match(await p.locator('#dialogBody').textContent(), /手动退出/);
    assert.match(await p.locator('#dialogBody').textContent(), /记录/);
    await p.locator('#dialogConfirm').click();
    assert.equal((await state(p)).connection, 'conflict');
  });
  await check('cross-page drafts and close keeps editing by default', async () => {
    await load(p, settingsURL);
    await p.locator('[data-page=shortcuts]').click();
    await p.locator('#wakeShortcut').fill('Ctrl+Alt+K');
    await p.locator('[data-page=language]').click();
    assert.equal(await p.locator('#draftBar').isVisible(), true);
    await p.locator('#closeSettings').click();
    assert.equal(await p.locator('#dialogCancel').evaluate(e => e === document.activeElement), true);
    await p.locator('#dialogCancel').click();
    await p.locator('[data-page=shortcuts]').click();
    assert.equal(await p.locator('#wakeShortcut').inputValue(), 'Ctrl+Alt+K');
    await p.locator('#saveDraft').click();
    assert.equal((await state(p)).saved.wakeShortcut, 'Ctrl+Alt+K');
    await p.locator('[data-page=general]').click();
    assert.equal(await p.locator('#draftBar').isVisible(), false);
    await p.locator('#closeSettings').click();
    assert.equal((await state(p)).running, true);
  });
  await check('unknown save result never claims unchanged settings or retries automatically', async () => {
    await load(p, settingsURL);
    await p.locator('#demoControls summary').first().click();
    await p.locator('#demoUnknownSave').click();
    await p.locator('#demoControls summary').first().click();
    await p.locator('#settingsEnable').click();
    assert.equal((await state(p)).saveOutcome, 'unknown');
    assert.equal((await state(p)).lastSave, null);
    await p.locator('#trayButton').click();
    assert.match(await p.locator('#traySaveTitle').textContent(), /未确认/);
    assert.doesNotMatch(await p.locator('#traySaveDescription').textContent(), /保持原值/);
    assert.equal(await p.locator('#retryTray').textContent(), '检查设置');
    await p.locator('#retryTray').click();
    assert.equal((await state(p)).saveOutcome, 'unknown');
    await p.locator('#settingsEnable').click();
    assert.equal((await state(p)).saveError, false);
  });
  await settings.context.close();
  const tray = await pageFor({ width: 520, height: 790 });
  await check('tray save failure survives connection updates until an explicit successful retry', async () => {
    await load(tray.page, settingsURL + '?tray=error');
    await withinViewport(tray.page, '#quickPanel');
    assert.equal((await state(tray.page)).mode, 'Automatic');
    await tray.page.locator('#demoControls summary').first().click();
    await tray.page.locator('#demoConnection').selectOption('disconnected');
    assert.equal((await state(tray.page)).saveError, true);
    await tray.page.locator('#demoControls summary').first().click();
    await tray.page.locator('#trayButton').click();
    await tray.page.locator('#retryTray').click();
    assert.equal((await state(tray.page)).saveError, false);
    assert.equal((await state(tray.page)).mode, 'OnDemand');
  });
  await tray.context.close();
  const narrow = await pageFor({ width: 520, height: 790 });
  await check('preview width 320 / translation first / readable text', async () => {
    await load(narrow.page, overlayURL + '?width=320');
    assert.equal(Math.round((await narrow.page.locator('#overlay').boundingBox()).width), 320);
    await withinViewport(narrow.page, '#overlay');
    await normalContrast(narrow.page, ['.candidate', '.language-tag', '.key-hint', '.exit-button']);
  });
  await check('editing at viewport 520 stays in the same anchored shell', async () => {
    await narrow.page.locator('#candidate').click();
    await wait(350);
    assert.equal((await state(narrow.page)).phase, 'editing');
    await withinViewport(narrow.page, '#overlay');
  });
  await check('viewport 320 does not clip the overlay', async () => {
    await narrow.page.setViewportSize({ width: 320, height: 790 });
    await withinViewport(narrow.page, '#overlay');
  });
  await check('Escape during a shell transition immediately removes content and interaction', async () => {
    await load(narrow.page, overlayURL);
    await narrow.page.locator('#candidate').click();
    assert.equal((await state(narrow.page)).phase, 'editing');
    assert((await state(narrow.page)).animations > 0);
    await narrow.page.keyboard.press('Escape');
    assert.equal((await state(narrow.page)).phase, 'hidden');
    assert.equal((await state(narrow.page)).candidate, '');
    assert.equal((await state(narrow.page)).canAccept, false);
    assert.equal(await narrow.page.locator('#overlay').isVisible(), false);
    assert.equal(await narrow.page.locator('.motion-snapshot, .exit-ghost').count(), 0);
  });
  await narrow.context.close();
  const movie = await pageFor({ width: 940, height: 760 }, { recordVideo: { dir: path.join(out, 'raw-video'), size: { width: 940, height: 760 } } });
  const m = movie.page;
  await check('real interaction video / edit return and independent acceptance / escape', async () => {
    await load(m, overlayURL);
    const initial = (await state(m)).source;
    await wait(1200);
    await m.locator('#candidate').hover();
    await wait(800);
    const anchor = await m.locator('#overlay').boundingBox();
    await m.evaluate(() => { window.motionSamples = []; const start = performance.now(); function sample(t) { const b = document.querySelector('#overlay').getBoundingClientRect(); window.motionSamples.push({ t: t - start, width: b.width, height: b.height, right: b.right, bottom: b.bottom }); if (t - start < 650) requestAnimationFrame(sample); } requestAnimationFrame(sample); });
    await m.locator('#candidate').click();
    await wait(700);
    const samples = await m.evaluate(() => window.motionSamples);
    assert(new Set(samples.map(s => Math.round(s.width))).size > 3, 'No real intermediate width frames were observed');
    for (const sample of samples) { assert(Math.abs(sample.right - (anchor.x + anchor.width)) < 1); assert(Math.abs(sample.bottom - (anchor.y + anchor.height)) < 1); }
    await fs.writeFile(path.join(out, 'motion-samples.json'), JSON.stringify(samples, null, 2));
    await m.locator('#candidate').press('End');
    await m.keyboard.type(' I’m flexible on the time.', { delay: 38 });
    await wait(500);
    const edited = await m.locator('#candidate').textContent();
    await m.keyboard.down('Tab');
    await wait(120);
    assert.equal((await state(m)).phase, 'returning');
    assert.equal((await state(m)).source, initial);
    assert.equal((await state(m)).canAccept, false);
    await m.keyboard.up('Tab');
    await wait(650);
    assert.equal((await state(m)).phase, 'pending');
    assert.equal((await state(m)).source, initial);
    assert.equal((await state(m)).canAccept, true);
    await wait(700);
    await m.keyboard.press('Tab');
    assert.equal((await state(m)).source, edited);
    assert.equal((await state(m)).sendCount, 0);
    await wait(1200);
    await m.locator('#sourceInput').fill('你好，我想确认一下明天下午的会议时间。');
    // This scenario exercises stable user exit, not Escape during an unfinished transition.
    await m.waitForFunction(() => {
      const stillAnimating = ['overlay', 'overlayInner'].some(id =>
        document.getElementById(id).getAnimations().some(animation => animation.playState === 'running'));
      return prototypeState().phase === 'preview' && !stillAnimating;
    }, undefined, { timeout: 2500 });
    assert.equal((await state(m)).phase, 'preview');
    await m.keyboard.press('Escape');
    const exit = await m.evaluate(() => ({
      state: prototypeState(), editable: document.querySelector('#candidate').contentEditable,
      ghosts: [...document.querySelectorAll('.exit-ghost')].map(node => ({ inert: node.inert, pointerEvents: getComputedStyle(node).pointerEvents }))
    }));
    assert.equal(exit.state.canAccept, false);
    assert.equal(exit.state.candidate, '');
    assert.equal(exit.editable, 'false');
    assert.equal(exit.ghosts.length, 1);
    assert.equal(exit.ghosts[0].inert, true);
    assert.equal(exit.ghosts[0].pointerEvents, 'none');
    await m.waitForFunction(() => !document.querySelector('.exit-ghost'), undefined, { timeout: 600 });
    assert.equal(await m.locator('#overlay').isVisible(), false);
    await wait(1000);
  });
  const video = m.video();
  await movie.context.close();
  await video.saveAs(path.join(out, 'overlay-motion.webm'));
  await fs.rm(path.join(out, 'raw-video'), { recursive: true, force: true });
  const safety = await pageFor({ width: 790, height: 820 }, { reducedMotion: 'reduce' });
  const s = safety.page;
  await check('reduced motion disables shell animation', async () => {
    await load(s, overlayURL);
    await s.locator('#candidate').click();
    assert.equal((await state(s)).phase, 'editing');
    assert.equal((await state(s)).animations, 0);
    assert.equal(await s.evaluate(() => matchMedia('(prefers-reduced-motion: reduce)').matches), true);
  });
  await check('unconfirmed, partial, updating and returning states never expose editing or acceptance', async () => {
    for (const scenario of ['partial', 'unknown-ime', 'locked', 'updating', 'returning']) {
      await load(s, overlayURL + '?scenario=' + scenario);
      const actual = await state(s);
      assert.equal(actual.canAccept, false, scenario);
      assert.equal(actual.canEdit, false, scenario);
      assert.equal(await s.locator('#candidate').getAttribute('contenteditable'), 'false', scenario);
    }
  });
  await check('pause clears draft and on-demand resume stays asleep', async () => {
    await load(s, overlayURL + '?scenario=editing');
    await s.locator('#candidate').fill('Private draft that must be cleared');
    await s.locator('#demoControls summary').first().click();
    await s.locator('#pauseAction').click();
    assert.equal((await state(s)).candidate, '');
    assert.equal((await state(s)).manual, '');
    await load(s, overlayURL + '?scenario=ondemand');
    await s.locator('#demoControls summary').first().click();
    await s.locator('#pauseAction').click();
    await s.locator('#pauseAction').click();
    assert.equal((await state(s)).phase, 'hidden');
    assert.equal((await state(s)).candidate, '');
  });
  await check('automatic refocus starts fresh but Escape does not reopen itself', async () => {
    await load(s, overlayURL);
    await s.locator('#sendMessage').focus();
    assert.equal((await state(s)).phase, 'hidden');
    await s.locator('#sourceInput').focus();
    assert.equal((await state(s)).candidate, '');
    assert.equal((await state(s)).canAccept, false);
    await s.waitForFunction(() => prototypeState().phase === 'preview');
    await s.keyboard.press('Escape');
    await wait(750);
    assert.equal((await state(s)).phase, 'hidden');
    assert.equal((await state(s)).focused, 'sourceInput');
    await s.locator('#sendMessage').focus();
    await s.locator('#sourceInput').focus();
    await s.waitForFunction(() => prototypeState().phase === 'preview');
  });
  await check('real textarea selection revokes the old candidate and only replaces the new range', async () => {
    await load(s, overlayURL);
    const original = 'Hello Alex, 明天下午见, thanks.';
    await s.locator('#sourceInput').fill(original);
    await s.waitForFunction(() => prototypeState().phase === 'preview');
    await s.locator('#sourceInput').evaluate(e => {
      const start = e.value.indexOf('明天下午见');
      e.setSelectionRange(start, start + '明天下午见'.length);
    });
    assert.equal((await state(s)).canAccept, false);
    assert.equal((await state(s)).canEdit, false);
    await s.waitForFunction(() => prototypeState().candidate === 'see you tomorrow afternoon');
    await s.keyboard.press('Tab');
    assert.equal((await state(s)).source, 'Hello Alex, see you tomorrow afternoon, thanks.');
    assert.equal((await state(s)).sendCount, 0);
  });
  await check('empty edited draft never pretends to be loading or becomes acceptable', async () => {
    await load(s, overlayURL + '?scenario=editing');
    const original = (await state(s)).source;
    await s.locator('#candidate').fill('');
    await s.keyboard.press('Tab');
    await s.waitForFunction(() => prototypeState().phase === 'pending');
    assert.equal((await state(s)).canAccept, false);
    assert.equal((await state(s)).source, original);
    assert.equal(await s.locator('#candidate').textContent(), '译文为空');
    await s.locator('#regenerateAction').click();
    await s.waitForFunction(() => prototypeState().phase === 'preview');
  });
  await check('paused entry immediately hides on focus loss without resuming', async () => {
    await load(s, overlayURL + '?scenario=paused');
    await s.locator('#sendMessage').focus();
    assert.equal(await s.locator('#overlay').isVisible(), false);
    assert.equal((await state(s)).enabled, false);
    assert.equal(await s.locator('.motion-snapshot, .exit-ghost').count(), 0);
    await s.locator('#sourceInput').focus();
    assert.equal((await state(s)).phase, 'paused');
    assert.equal((await state(s)).candidate, '');
  });
  await check('unknown write result asks to check the original and never retries', async () => {
    await load(s, overlayURL);
    await s.locator('#demoControls summary').first().click();
    await s.locator('#writeOutcome').selectOption('unknown');
    await s.locator('#demoControls summary').first().click();
    await s.locator('#sourceInput').focus();
    await s.keyboard.press('Tab');
    assert.equal((await state(s)).quarantined, true);
    assert.match(await s.locator('#sourceNotice').textContent(), /请检查原输入框/);
    assert.equal(await s.locator('#overlay').isVisible(), false);
    await wait(900);
    assert.equal((await state(s)).acceptCount, 0);
  });
  await check('direct replacement rejects a changed selection', async () => {
    await load(s, overlayURL + '?scenario=ondemand');
    await s.locator('#sourceInput').evaluate(e => e.setSelectionRange(0, 4));
    await s.keyboard.press('Control+Shift+F10');
    await s.locator('#sourceInput').evaluate(e => e.setSelectionRange(0, 2));
    await wait(900);
    assert.equal((await state(s)).source, '我们明天下午再聊细节，可以吗？');
    assert.match(await s.locator('#sourceNotice').textContent(), /没有替换/);
  });
  await safety.context.close();
  await fs.writeFile(path.join(out, 'review-checks.json'), JSON.stringify({ scope: 'Standalone HTML prototypes only. Not production or desktop acceptance.', playwright: require('playwright/package.json').version, results, pageErrors: errors }, null, 2));
  assert.deepEqual(errors, []);
  assert.equal(results.filter(result => result.result === 'failed').length, 0, 'One or more prototype checks failed; see review-checks.json');
  await browser.close();
  process.stdout.write(`Review evidence: ${out}\n`);
})().catch(async error => {
  process.stderr.write(error.stack + '\n');
  try { await fs.mkdir(out, { recursive: true }); await fs.writeFile(path.join(out, 'review-failure.json'), JSON.stringify({ error: error.message, pageErrors: errors, results }, null, 2)); } catch {}
  await browser?.close();
  process.exitCode = 1;
});
