/* The run box, measured: the two lines, their order, and where the box is visible.

   Starts a three-file selection sync and samples the run box once a second. Records the workers line, the
   counts line, the vertical order of the live rows against the counts line, and every "left" / "min left"
   phrase the lines ever show (the ETA and the "N subtitles left" head are what this is here to catch). Then,
   while the run is still going, switches to the History tab and photographs what is on screen there - the run
   box used to follow the user into every tab.

   Usage: PLAYWRIGHT_BROWSERS_PATH=... node runbox-lines.js [label]
*/
const fs = require('fs');
const { chromium } = require('/tmp/audit/pw/node_modules/playwright');
const BASE = 'http://127.0.0.1:8096';
const IDS = JSON.parse(fs.readFileSync(__dirname + '/ids.json', 'utf8'));
const LABEL = process.argv[2] || 'runbox';
const SHOTS = __dirname + '/shots';
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const AUTH = 'MediaBrowser Token="' + IDS.admin_token + '"';

const runState = () => {
  const txt = (sel) => (document.querySelector(sel) || {}).textContent || '';
  const y = (sel) => {
    const el = document.querySelector(sel);
    if (!el) return null;
    const r = el.getBoundingClientRect();
    return (r.height === 0) ? null : Math.round(r.top);
  };
  const box = document.querySelector('#ss-runbox');
  return {
    runLabel: txt('#ss-run-label').trim(),
    queue: txt('#ss-queue').trim(),
    phase: txt('#ss-phase').trim(),
    bar: y('#ss-progress'),
    workersY: y('#ss-workers'),
    phaseY: y('#ss-phase'),
    queueY: y('#ss-queue'),
    workerRows: document.querySelectorAll('#ss-workers .ss-worker').length,
    resultsArea: !!document.querySelector('#ss-log'),
    // Own display is not enough: an ancestor with .ss-hidden still leaves this element's computed
    // display at 'block'. Visibility is whether the box actually occupies space on screen.
    runBoxShown: !!box && box.offsetHeight > 0 && box.getBoundingClientRect().height > 0,
    runBoxInSyncPanel: !!document.querySelector('#panel-sync #ss-runbox'),
    visibleText: (document.body.innerText || ''),
    tab: (function () {
      const a = document.querySelector('.ss-nav-item.ss-nav-active');
      return a ? a.getAttribute('data-panel') : null;
    })(),
    clip: (function () {
      const el = document.querySelector('#ss-runbox');
      if (!el || !el.offsetHeight) return null;
      const r = el.getBoundingClientRect();
      return { x: Math.max(0, r.x - 8), y: Math.max(0, r.y - 8), width: Math.min(1330, r.width + 16), height: Math.min(720, r.height + 16) };
    })(),
  };
};

(async () => {
  const user = await (await fetch(BASE + '/Users/New', {
    method: 'POST', headers: { 'Content-Type': 'application/json', Authorization: AUTH },
    body: JSON.stringify({ Name: 'gui-rb-' + Date.now().toString(36) }),
  })).json();
  const current = await (await fetch(BASE + '/Users/' + user.Id, { headers: { Authorization: AUTH } })).json();
  await fetch(BASE + '/Users/' + user.Id + '/Policy', {
    method: 'POST', headers: { 'Content-Type': 'application/json', Authorization: AUTH },
    body: JSON.stringify(Object.assign({}, current.Policy, { IsAdministrator: true, EnableAllFolders: true })),
  });

  const browser = await chromium.launch({ args: ['--no-sandbox'], executablePath: '/opt/data/.playwright/chromium-1234/chrome-linux64/chrome' });
  const page = await (await browser.newContext({ viewport: { width: 1400, height: 1000 } })).newPage();
  await page.goto(BASE + '/web/', { waitUntil: 'domcontentloaded', timeout: 60000 });
  await wait(3500);
  await page.fill('input[name="username"], input[type="text"]', user.Name);
  const submit = await page.$('button[type="submit"], .btnSubmit, button:has-text("Sign In")');
  if (submit) await submit.click();
  await wait(9000);
  await page.goto(BASE + '/web/#/configurationpage?name=subsync-main', { waitUntil: 'domcontentloaded', timeout: 60000 });
  for (let i = 0; i < 40; i++) {
    await wait(1000);
    if (await page.evaluate(() => document.querySelectorAll('#ss-list .ss-row').length > 5)) break;
  }

  await page.evaluate(() => {
    const rows = Array.from(document.querySelectorAll('#ss-list .ss-row'))
      .filter((r) => /Movie/.test((r.querySelector('.ss-meta') || {}).textContent || ''));
    rows.slice(0, 3).forEach((r) => r.dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true })));
  });
  await wait(9000);
  await page.evaluate(() => { const b = document.querySelector('#ss-syncsel-btn'); if (b) b.click(); });

  const samples = [];
  let historyShotTaken = false;
  for (let i = 0; i < 100; i++) {
    await wait(1000);
    const s = await page.evaluate(runState);
    const key = s.runLabel + '|' + s.queue;
    if (!samples.length || samples[samples.length - 1].key !== key) samples.push(Object.assign({ at: i + 1, key }, s));

    // once the run is properly under way: photograph the run box, then look at the History tab
    if (i > 1 && s.tab === 'sync' && !historyShotTaken && s.workerRows + (s.queue ? 1 : 0) > 0) {
      if (s.clip) await page.screenshot({ path: SHOTS + '/' + LABEL + '-sync-tab.png', clip: s.clip });
      await page.evaluate(() => { const b = document.querySelector('.ss-nav-item[data-panel="history"]'); if (b) b.click(); });
      await wait(2500);
      const onHistory = await page.evaluate(runState);
      samples.push({ step: 'history tab, run still going', state: onHistory });
      const area = await page.evaluate(() => {
        const el = document.querySelector('#panel-history');
        const r = el.getBoundingClientRect();
        return { x: 0, y: 0, width: 1330, height: Math.min(700, Math.max(300, r.height + 60)) };
      });
      await page.screenshot({ path: SHOTS + '/' + LABEL + '-history-tab.png', clip: area });
      await page.evaluate(() => { const b = document.querySelector('.ss-nav-item[data-panel="sync"]'); if (b) b.click(); });
      await wait(1500);
      historyShotTaken = true;
    }
    if (s.queue && /^(\d+)\/\1 done \(100%\)/.test(s.queue) && !s.workerRows) break;
  }

  // did any sample ever mention time left, or how many subtitles are left?
  const phrases = samples.map((s) => ((s.queue || '') + ' || ' + (s.runLabel || ''))).join(' \n ');
  const out = {
    label: LABEL,
    samples,
    sawTimeLeft: /min left|time left|estimating/i.test(phrases),
    sawSubtitlesLeft: /subtitles? left/i.test(phrases),
    historyTabRunBoxShown: (samples.find((s) => s.step) || {}).state ? samples.find((s) => s.step).state.runBoxShown : null,
    historyTabVisibleTextMentionsWorkers: (samples.find((s) => s.step) || {}).state
      ? /workers|subtitles? left|\d+\/\d+ done/i.test(samples.find((s) => s.step).state.visibleText) : null,
  };
  fs.writeFileSync(__dirname + '/' + LABEL + '-lines.json', JSON.stringify(out, null, 1));
  console.log(JSON.stringify({ label: LABEL, sawTimeLeft: out.sawTimeLeft, sawSubtitlesLeft: out.sawSubtitlesLeft, historyTabRunBoxShown: out.historyTabRunBoxShown, distinct: samples.filter((s) => !s.step).map((s) => ({ at: s.at, runLabel: s.runLabel, queue: s.queue })) }, null, 1));
  await browser.close();
  await fetch(BASE + '/Users/' + user.Id, { method: 'DELETE', headers: { Authorization: AUTH } });
})();
