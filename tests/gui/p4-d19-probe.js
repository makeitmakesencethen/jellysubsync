/*
 * D19 — does the golden-section checkbox really measure 1×1 px, and is the control usable?
 *
 * The row's claim is that the switch "measures 1×1 px (styled input — verify visually before calling it
 * broken)". jellyfin-web styles the native input of an `emby-checkbox` as hidden — measured here, from the
 * stylesheet the real page loads — so the question is whether the 1×1 px box is the control or only what the
 * visible one sits on top of.
 *
 * The page under measurement (web/p4-d19.html in the rig's jellyfin-web directory) carries the two checkbox
 * rows exactly as Jellyfin.Plugin.SubSync/Web/subsyncMain.html has them - the golden-section switch and the
 * framerate-correction switch as its control - and links the same `main.jellyfin.*.css` the plugin page gets.
 * That isolates the question to the markup plus jellyfin-web's own rules, which is what decides the size.
 *
 *   python3 tests/rig/run_scenario.py --scenario smoke --no-shim --keep-rig
 *   node tests/gui/p4-d19-probe.js
 */
const fs = require('fs');
const path = require('path');
const { chromium } = require(process.env.PW || '/tmp/audit/pw/node_modules/playwright');

const BASE = 'http://127.0.0.1:8096';
const HERE = __dirname;
const PAGE = BASE + '/web/p4-d19.html';

const measure = () => {
  const box = (el) => {
    if (!el) return null;
    const r = el.getBoundingClientRect();
    const cs = getComputedStyle(el);
    return { tag: el.tagName.toLowerCase(), width: +r.width.toFixed(2), height: +r.height.toFixed(2),
             display: cs.display, opacity: cs.opacity, position: cs.position, cursor: cs.cursor };
  };
  const gss = document.getElementById('ss-gss');
  const fps = document.getElementById('ss-fixfps');
  const row = gss ? gss.closest('.checkboxContainer') : null;
  const label = gss ? gss.closest('.emby-checkbox-label') : null;
  let hit = null;
  if (label) {
    const r = label.getBoundingClientRect();
    const el = document.elementFromPoint(r.left + 12, r.top + r.height / 2);
    hit = el ? el.tagName.toLowerCase() + (el.id ? '#' + el.id : '') : null;
  }
  return {
    gssInput: box(gss), gssLabel: box(label), gssOutline: box(row ? row.querySelector('.checkboxOutline') : null),
    fixfpsInput: box(fps), fixfpsLabel: box(fps ? fps.closest('.emby-checkbox-label') : null),
    rowWidth: row ? +row.getBoundingClientRect().width.toFixed(1) : null,
    labelPaddingLeft: label ? getComputedStyle(label).paddingLeft : null,
    elementAtLabelCentre: hit,
  };
};

(async () => {
  const browser = await chromium.launch({
    args: ['--no-sandbox'],
    executablePath: process.env.BROWSER_PATH || '/opt/data/.playwright/chromium-1234/chrome-linux64/chrome',
  });
  const page = await (await browser.newContext({ viewport: { width: 1600, height: 1000 } })).newPage();
  const out = { url: PAGE };
  try {
    await page.goto(PAGE, { waitUntil: 'load', timeout: 30000 });
    out.stylesheetLoaded = await page.evaluate(() =>
      Array.from(document.styleSheets).some((s) => (s.href || '').includes('main.jellyfin')));
    out.static = await page.evaluate(measure);

    // The same measurement with the control in the state the plugin puts it in while framerate correction is
    // off (the page's own syncGoldenSectionState adds ss-inert and disables the input).
    await page.evaluate(() => {
      const gss = document.getElementById('ss-gss');
      const row = gss.closest('.checkboxContainer');
      gss.disabled = true;
      row.classList.add('ss-inert');
    });
    out.inert = await page.evaluate(measure);
    out.inertOpacity = await page.evaluate(() =>
      getComputedStyle(document.getElementById('ss-gss').closest('.checkboxContainer')).opacity);
  } catch (err) {
    out.error = String(err && err.message ? err.message : err);
  }
  await browser.close();
  fs.writeFileSync(path.join(HERE, 'p4-d19.json'), JSON.stringify(out, null, 1));
  console.log(JSON.stringify(out, null, 1));
})();
