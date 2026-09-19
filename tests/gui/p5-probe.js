/* Pass 5 probe: (1) what a single right-click pick looks like, against two picks, and
   (2) the run box's two lines - their order, their sizes and their text.

   Phase 1 (selection): snapshots the row list's own rendering - each row's class, its computed
   background, inset shadow and name colour - after nothing, after one right-click pick, after a
   second one, and after a plain left-click. A picked row must be visually distinct from an
   unpicked one at ONE pick exactly as it is at two.

   Phase 2 (run box): picks three movies, starts the run through the page's own button, and samples
   the run box: the counts line and the workers line, their computed font sizes and their vertical
   positions, plus a screenshot of the box.

   Usage: PLAYWRIGHT_BROWSERS_PATH=/opt/data/.playwright node p5-probe.js <label> [phase]
*/
const fs = require('fs');
const { chromium } = require('/tmp/pw/node_modules/playwright');
const BASE = 'http://127.0.0.1:8096';
const USER = 'gui-p5';
const LABEL = process.argv[2] || 'before';
const PHASE = process.argv[3] || 'both';
const SHOTS = __dirname + '/shots';
const wait = (ms) => new Promise((r) => setTimeout(r, ms));

const selState = () => ({
  dataline: (document.querySelector('#ss-dataline') || {}).textContent || '',
  picked: Array.from(document.querySelectorAll('#ss-list .ss-row'))
    .filter((r) => r.classList.contains('selected'))
    .map((r) => (r.querySelector('.ss-name') || {}).textContent || ''),
  rows: Array.from(document.querySelectorAll('#ss-list .ss-row')).map((r, i) => {
    const cs = getComputedStyle(r);
    const nm = r.querySelector('.ss-name');
    return {
      i: i,
      name: nm ? nm.textContent : '',
      cls: r.className,
      bg: cs.backgroundColor,
      shadow: cs.boxShadow,
      borderLeft: cs.borderLeftWidth + ' ' + cs.borderLeftColor,
      nameColor: nm ? getComputedStyle(nm).color : null,
      nameWeight: nm ? getComputedStyle(nm).fontWeight : null,
      hasOptions: !!r.querySelector('.ss-row-actions'),
    };
  }),
});

const runState = () => {
  const txt = (sel) => ((document.querySelector(sel) || {}).textContent || '').trim();
  const box = (sel) => {
    const el = document.querySelector(sel);
    if (!el) return null;
    const r = el.getBoundingClientRect();
    const cs = getComputedStyle(el);
    return {
      text: (el.textContent || '').trim(),
      top: Math.round(r.top),
      height: Math.round(r.height),
      fontSize: cs.fontSize,
      fontWeight: cs.fontWeight,
      color: cs.color,
      display: cs.display,
    };
  };
  const rb = document.querySelector('#ss-runbox');
  return {
    runBoxShown: !!rb && rb.getBoundingClientRect().height > 0,
    order: rb ? Array.from(rb.children).map((c) => (c.id || c.className || c.tagName).toString()) : [],
    runLabel: box('#ss-run-label'),
    queue: box('#ss-queue'),
    phase: box('#ss-phase'),
    spinner: box('#ss-spinner'),
    bar: box('#ss-progress'),
    workers: box('#ss-workers'),
    workerRows: document.querySelectorAll('#ss-workers .ss-worker').length,
    cancelTop: (function () { const el = document.querySelector('#ss-cancel'); return el ? Math.round(el.getBoundingClientRect().top) : null; })(),
    clip: (function () {
      if (!rb || !rb.getBoundingClientRect().height) return null;
      const r = rb.getBoundingClientRect();
      return { x: Math.max(0, r.x - 10), y: Math.max(0, r.y - 10), width: Math.min(1340, r.width + 20), height: Math.min(700, r.height + 20) };
    })(),
  };
};

const rc = (idx) => {
  const rows = Array.from(document.querySelectorAll('#ss-list .ss-row'));
  const row = rows[idx];
  if (!row) return null;
  row.dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true }));
  return (row.querySelector('.ss-name') || {}).textContent || '';
};

const lc = (idx) => {
  const rows = Array.from(document.querySelectorAll('#ss-list .ss-row'));
  const row = rows[idx];
  if (!row) return null;
  row.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }));
  return (row.querySelector('.ss-name') || {}).textContent || '';
};

(async () => {
  const browser = await chromium.launch({
    args: ['--no-sandbox'],
    executablePath: '/opt/data/.playwright/chromium-1234/chrome-linux64/chrome',
  });
  const page = await (await browser.newContext({ viewport: { width: 1400, height: 1100 } })).newPage();
  const out = { label: LABEL, phase: PHASE };

  await page.goto(BASE + '/web/', { waitUntil: 'domcontentloaded', timeout: 60000 });
  await wait(3500);
  await page.fill('input[name="username"], input[type="text"]', USER);
  const submit = await page.$('button[type="submit"], .btnSubmit');
  if (submit) await submit.click();
  await wait(9000);
  await page.goto(BASE + '/web/#/configurationpage?name=subsync-main', { waitUntil: 'domcontentloaded', timeout: 60000 });
  for (let i = 0; i < 40; i++) {
    await wait(1000);
    if (await page.evaluate(() => document.querySelectorAll('#ss-list .ss-row').length > 5)) break;
  }

  if (PHASE === 'both' || PHASE === 'select') {
    const steps = [];
    steps.push({ step: 'nothing picked', state: await page.evaluate(selState) });
    out.firstRightClickOn = await page.evaluate(rc, 3);
    await wait(2500);
    steps.push({ step: 'after one right-click', state: await page.evaluate(selState) });
    const list = await page.evaluate(() => {
      const el = document.querySelector('#ss-list');
      const r = el.getBoundingClientRect();
      return { x: Math.max(0, r.x - 6), y: Math.max(0, r.y - 6), width: Math.min(1340, r.width + 12), height: Math.min(560, r.height + 12) };
    });
    await page.screenshot({ path: SHOTS + '/p5-' + LABEL + '-select-one-pick.png', clip: list });
    out.secondRightClickOn = await page.evaluate(rc, 6);
    await wait(2500);
    steps.push({ step: 'after two right-clicks', state: await page.evaluate(selState) });
    await page.screenshot({ path: SHOTS + '/p5-' + LABEL + '-select-two-picks.png', clip: list });
    out.leftClickOn = await page.evaluate(lc, 9);
    await wait(2500);
    steps.push({ step: 'after a plain left-click', state: await page.evaluate(selState) });
    out.selectSteps = steps;
  }

  if (PHASE === 'both' || PHASE === 'run') {
    await page.goto(BASE + '/web/#/configurationpage?name=subsync-main', { waitUntil: 'domcontentloaded', timeout: 60000 });
    for (let i = 0; i < 30; i++) {
      await wait(1000);
      if (await page.evaluate(() => document.querySelectorAll('#ss-list .ss-row').length > 5)) break;
    }
    await page.evaluate(() => {
      const rows = Array.from(document.querySelectorAll('#ss-list .ss-row'))
        .filter((r) => /Movie/.test(((r.querySelector('.ss-meta') || {}).textContent) || ''));
      rows.slice(0, 3).forEach((r) => r.dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true })));
    });
    await wait(6000);
    await page.evaluate(() => { const b = document.querySelector('#ss-syncsel-btn'); if (b) b.click(); });

    const samples = [];
    let shot = false;
    for (let i = 0; i < 70; i++) {
      await wait(1200);
      const s = await page.evaluate(runState);
      const key = (s.runLabel && s.runLabel.text) + ' || ' + (s.queue && s.queue.text);
      if (!samples.length || samples[samples.length - 1].key !== key) samples.push(Object.assign({ at: i + 1, key: key }, s));
      if (i > 1 && !shot && s.runBoxShown && ((s.runLabel && s.runLabel.text) || s.workerRows)) {
        if (s.clip) await page.screenshot({ path: SHOTS + '/p5-' + LABEL + '-runbox.png', clip: s.clip });
        shot = true;
      }
      const q = (s.queue && s.queue.text) || '';
      if (q && /^\s*\d+\/\d+ done \(100%\)/.test(q) && !s.workerRows) break;
    }
    out.runSamples = samples;
    out.order = samples.length ? samples[0].order : null;
  }

  fs.writeFileSync(__dirname + '/p5-' + LABEL + '-probe.json', JSON.stringify(out, null, 1));
  const first = (out.selectSteps || []).find((s) => s.step === 'after one right-click');
  const two = (out.selectSteps || []).find((s) => s.step === 'after two right-clicks');
  const pickedRow = (st, name) => (st && st.state.rows.find((r) => r.name === name)) || null;
  console.log(JSON.stringify({
    label: LABEL,
    phase: PHASE,
    onePick: pickedRow(first, out.firstRightClickOn),
    twoPicksFirst: pickedRow(two, out.firstRightClickOn),
    twoPicksSecond: pickedRow(two, out.secondRightClickOn),
    datalineOne: first && first.state.dataline,
    datalineTwo: two && two.state.dataline,
    pickedNames: (two && two.state.picked) || null,
    runOrder: out.order,
    runLines: (out.runSamples || []).slice(0, 3).map((s) => ({ run: s.runLabel, queue: s.queue, rows: s.workerRows })),
  }, null, 1));

  await browser.close();
})();
