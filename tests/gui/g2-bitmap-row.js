/* G2: the file whose only subtitle is an image format (DVDSUB), driven through the page.

   The fixture "Bitmap" (9977d98d54e48c801cc91279dd1d4b8a) carries exactly one subtitle track, index 2,
   "English - Default - DVDSUB" - no text track at all. Before the fix the page offered it, counted it and
   queued it, and the queue refused it at enqueue: one failed task per click (five of them in the user's own
   test session, per the Jellyfin log). This probe drives the row the way a user does and records what the
   page offers, what it counts, and what reaches the server - then does the same for a file that does have
   text subtitles, because a filter that is too wide is as wrong as one that is too narrow.

   Usage: PLAYWRIGHT_BROWSERS_PATH=... node g2-bitmap-row.js [label]
*/
const fs = require('fs');
const { chromium } = require('/tmp/audit/pw/node_modules/playwright');
const BASE = 'http://127.0.0.1:8096';
const IDS = JSON.parse(fs.readFileSync(__dirname + '/ids.json', 'utf8'));
const LABEL = process.argv[2] || 'g2';
const SHOTS = __dirname + '/shots';
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const AUTH = 'MediaBrowser Token="' + IDS.admin_token + '"';
const BITMAP_NAME = 'Bitmap';
const CONTROL_NAME = 'Embedded Test';

const search = (name) => {
  const s = document.querySelector('#ss-search');
  if (!s) return false;
  s.value = name;
  s.dispatchEvent(new Event('input', { bubbles: true }));
  return true;
};

const rowState = (name) => {
  const rows = Array.from(document.querySelectorAll('#ss-list .ss-row'));
  const target = rows.find((r) => ((r.querySelector('.ss-name') || {}).textContent || '').trim() === name);
  const picker = target ? target.querySelector('select') : null;
  return {
    found: !!target,
    rows: rows.length,
    viewed: name,
    meta: target ? ((target.querySelector('.ss-meta') || {}).textContent || '') : '',
    options: picker ? Array.from(picker.options).map((o) => ({
      value: o.value, text: o.textContent.trim(), disabled: o.disabled, title: o.getAttribute('title') || '',
    })) : null,
    button: (function () {
      const b = target ? target.querySelector('#ss-syncbtn') : null;
      return b ? { label: (b.querySelector('span') || {}).textContent || '', disabled: !!b.disabled, title: b.getAttribute('title') || '' } : null;
    })(),
    diag: (function () { const d = document.querySelector('#ss-diag'); return d && !d.classList.contains('ss-hidden') ? d.textContent : ''; })(),
    runLabel: (document.querySelector('#ss-run-label') || {}).textContent || '',
    queue: (document.querySelector('#ss-queue') || {}).textContent || '',
    clip: (function () {
      const el = target;
      if (!el) return null;
      const r = el.getBoundingClientRect();
      return { x: Math.max(0, r.x - 10), y: Math.max(0, r.y - 10), width: Math.min(1330, r.width + 20), height: Math.min(520, r.height + 20) };
    })(),
  };
};

const batchesCount = async () => ((await (await fetch(BASE + '/SubSync/Batches', { headers: { Authorization: AUTH } })).json()) || []).length;

async function openRow(page, name) {
  await page.evaluate(search, name);
  for (let i = 0; i < 20; i++) {
    await wait(1000);
    const there = await page.evaluate((n) => Array.from(document.querySelectorAll('#ss-list .ss-row'))
      .some((r) => ((r.querySelector('.ss-name') || {}).textContent || '').trim() === n), name);
    if (there) break;
  }
  await page.evaluate((n) => {
    const row = Array.from(document.querySelectorAll('#ss-list .ss-row'))
      .find((r) => ((r.querySelector('.ss-name') || {}).textContent || '').trim() === n);
    if (row) row.click();
  }, name);
  await wait(6000);
}

(async () => {
  const user = await (await fetch(BASE + '/Users/New', {
    method: 'POST', headers: { 'Content-Type': 'application/json', Authorization: AUTH },
    body: JSON.stringify({ Name: 'gui-g2-' + Date.now().toString(36) }),
  })).json();
  const current = await (await fetch(BASE + '/Users/' + user.Id, { headers: { Authorization: AUTH } })).json();
  await fetch(BASE + '/Users/' + user.Id + '/Policy', {
    method: 'POST', headers: { 'Content-Type': 'application/json', Authorization: AUTH },
    body: JSON.stringify(Object.assign({}, current.Policy, { IsAdministrator: true, EnableAllFolders: true })),
  });

  const browser = await chromium.launch({ args: ['--no-sandbox'], executablePath: '/opt/data/.playwright/chromium-1234/chrome-linux64/chrome' });
  const page = await (await browser.newContext({ viewport: { width: 1400, height: 1000 } })).newPage();
  const calls = [];
  page.on('request', (r) => { if (/SubSync\/(Batch|Sync)/.test(r.url())) calls.push(r.method() + ' ' + r.url().replace(BASE, '')); });
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

  // --- the bitmap row
  await openRow(page, BITMAP_NAME);
  const bitmapRow = await page.evaluate(rowState, BITMAP_NAME);
  if (bitmapRow.clip) await page.screenshot({ path: SHOTS + '/' + LABEL + '-bitmap-row.png', clip: bitmapRow.clip });

  // what the page counts for that file as a selection
  await page.evaluate((n) => {
    const row = Array.from(document.querySelectorAll('#ss-list .ss-row'))
      .find((r) => ((r.querySelector('.ss-name') || {}).textContent || '').trim() === n);
    if (row) row.dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true }));
  }, BITMAP_NAME);
  await wait(4000);
  const bitmapSelected = await page.evaluate(() => {
    const sel = document.querySelector('#ss-syncsel-btn');
    return {
      button: sel ? (sel.querySelector('span') || {}).textContent || '' : null,
      disabled: sel ? !!sel.disabled : null,
      picked: (document.querySelector('#ss-picked') || {}).textContent || '',
    };
  });
  bitmapSelected.row = await page.evaluate(rowState, BITMAP_NAME);

  // press the row's own Sync: does anything reach the server?
  const before = await batchesCount();
  await page.evaluate((n) => {
    const row = Array.from(document.querySelectorAll('#ss-list .ss-row'))
      .find((r) => ((r.querySelector('.ss-name') || {}).textContent || '').trim() === n);
    const b = row ? row.querySelector('#ss-syncbtn') : null;
    if (b) b.click();
  }, BITMAP_NAME);
  await wait(7000);
  const after = await batchesCount();
  const afterPress = await page.evaluate(rowState, BITMAP_NAME);

  // --- the control: a file that does carry text subtitles
  await openRow(page, CONTROL_NAME);
  const controlRow = await page.evaluate(rowState, CONTROL_NAME);
  if (controlRow.clip) await page.screenshot({ path: SHOTS + '/' + LABEL + '-control-row.png', clip: controlRow.clip });

  const out = {
    label: LABEL,
    bitmapRow,
    bitmapSelected,
    afterPress,
    batchesBefore: before,
    batchesAfter: after,
    batchesCreatedOnClick: after - before,
    calls,
    controlRow,
  };
  fs.writeFileSync(__dirname + '/' + LABEL + '-row.json', JSON.stringify(out, null, 1));
  console.log(JSON.stringify({
    label: LABEL,
    bitmapOptions: bitmapRow.options,
    bitmapButton: bitmapRow.button,
    bitmapSelectionButton: { label: bitmapSelected.button, disabled: bitmapSelected.disabled },
    batchesCreatedOnClick: after - before,
    requests: calls,
    controlRow: { found: controlRow.found, options: controlRow.options, button: controlRow.button },
  }, null, 1));
  await browser.close();
  await fetch(BASE + '/Users/' + user.Id, { method: 'DELETE', headers: { Authorization: AUTH } });
})();
