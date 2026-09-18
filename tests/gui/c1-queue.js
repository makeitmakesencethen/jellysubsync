/* C1: the run's own progress line - jobs left, percentage done, and the estimate.

   Picks three movie rows and starts the selection sync, then samples #ss-queue once a second and records
   every value it takes, so "estimating" and then a number can both be seen (the estimate needs two finished
   subtitles before it means anything).

   Usage: PLAYWRIGHT_BROWSERS_PATH=... node c1-queue.js [label]
*/
const fs = require('fs');
const { chromium } = require('/tmp/audit/pw/node_modules/playwright');
const BASE = 'http://127.0.0.1:8096';
const IDS = JSON.parse(fs.readFileSync(__dirname + '/ids.json', 'utf8'));
const LABEL = process.argv[2] || 'c1';
const SHOTS = __dirname + '/shots';
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const AUTH = 'MediaBrowser Token="' + IDS.admin_token + '"';

const runState = () => ({
  queue: (document.querySelector('#ss-queue') || {}).textContent || '',
  runLabel: (document.querySelector('#ss-run-label') || {}).textContent || '',
  phase: (document.querySelector('#ss-phase') || {}).textContent || '',
  progressWidth: (document.querySelector('#ss-progress') || {}).style ? document.querySelector('#ss-progress').style.width : null,
  button: (function () { const b = document.querySelector('#ss-syncsel-btn span'); return b ? b.textContent : null; })(),
  runBoxShown: (function () { const b = document.querySelector('#ss-runbox'); return !!b && getComputedStyle(b).display !== 'none'; })(),
  logLines: (document.querySelector('#ss-log') || {}).textContent
    ? document.querySelector('#ss-log').textContent.split('\n').filter(Boolean).slice(0, 4) : [],
  logTail: (function () {
    const box = document.querySelector('#ss-log');
    if (!box) return [];
    if (box.tagName === 'PRE') return box.textContent.split('\n').filter(Boolean).slice(-6);
    return Array.from(box.children).slice(-6).map((r) => (r.textContent || '').trim());
  })(),
  logShape: (function () {
    const box = document.querySelector('#ss-log');
    if (!box) return null;
    const cs = getComputedStyle(box);
    return { tag: box.tagName.toLowerCase(), cls: box.className, fontFamily: cs.fontFamily.slice(0, 40), childRows: box.children.length };
  })(),
});

(async () => {
  const user = await (await fetch(BASE + '/Users/New', {
    method: 'POST', headers: { 'Content-Type': 'application/json', Authorization: AUTH },
    body: JSON.stringify({ Name: 'gui-c1-' + Date.now().toString(36) }),
  })).json();
  const current = await (await fetch(BASE + '/Users/' + user.Id, { headers: { Authorization: AUTH } })).json();
  await fetch(BASE + '/Users/' + user.Id + '/Policy', {
    method: 'POST', headers: { 'Content-Type': 'application/json', Authorization: AUTH },
    body: JSON.stringify(Object.assign({}, current.Policy, { IsAdministrator: true, EnableAllFolders: true })),
  });

  const browser = await chromium.launch({ args: ['--no-sandbox'], executablePath: '/opt/data/.playwright/chromium-1234/chrome-linux64/chrome' });
  const page = await (await browser.newContext({ viewport: { width: 1400, height: 1000 } })).newPage();
  const calls = [];
  page.on('request', (r) => { if (/SubSync\//.test(r.url())) calls.push(r.method() + ' ' + r.url().replace(BASE, '').split('?')[0]); });
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
  // three rows that actually carry subtitle tracks
  await page.evaluate(() => {
    const rows = Array.from(document.querySelectorAll('#ss-list .ss-row'))
      .filter((r) => /Movie/.test((r.querySelector('.ss-meta') || {}).textContent || ''));
    rows.slice(0, 3).forEach((r) => r.dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true })));
  });
  await wait(9000);
  const beforeStart = await page.evaluate(runState);
  await page.evaluate(() => { const b = document.querySelector('#ss-syncsel-btn'); if (b) b.click(); });

  const samples = [];
  for (let i = 0; i < 180; i++) {
    await wait(1000);
    const s = await page.evaluate(runState);
    if (!samples.length || samples[samples.length - 1].queue !== s.queue || samples[samples.length - 1].runLabel !== s.runLabel) {
      samples.push(Object.assign({ at: i + 1 }, s));
    }
    if (s.queue && /0 subtitles? left/.test(s.queue)) break;
  }
  const clip = await page.evaluate(() => {
    const el = document.querySelector('#ss-runbox').getBoundingClientRect();
    return { x: Math.max(0, el.x - 8), y: Math.max(0, el.y - 8), width: Math.min(1330, el.width + 16), height: Math.min(700, el.height + 16) };
  });
  await page.screenshot({ path: SHOTS + '/' + LABEL + '-runbox.png', clip });

  const out = { label: LABEL, beforeStart, samples, calls: calls.slice(0, 12) };
  fs.writeFileSync(__dirname + '/' + LABEL + '-queue.json', JSON.stringify(out, null, 1));
  console.log(JSON.stringify(out, null, 1));
  await browser.close();
  await fetch(BASE + '/Users/' + user.Id, { method: 'DELETE', headers: { Authorization: AUTH } });
})();
