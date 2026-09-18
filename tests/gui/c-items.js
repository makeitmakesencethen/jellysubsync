/* The Sync tab's selection behaviour, measured for C3 (a search must not cost the picks), C4 (picks sort to
   the top) and C5 (every sync button states the count in subtitles).

   The page keeps its selection internally, so each step reads what the user can see: the count line, the
   sync button's own label, and which rows are marked picked - plus whether a row is visible at all after a
   search narrows the list.

   Usage: PLAYWRIGHT_BROWSERS_PATH=... node c-items.js [label]
*/
const fs = require('fs');
const { chromium } = require('/tmp/audit/pw/node_modules/playwright');
const BASE = 'http://127.0.0.1:8096';
const IDS = JSON.parse(fs.readFileSync(__dirname + '/ids.json', 'utf8'));
const LABEL = process.argv[2] || 'c';
const SHOTS = __dirname + '/shots';
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const AUTH = 'MediaBrowser Token="' + IDS.admin_token + '"';

const state = () => ({
  dataline: (document.querySelector('#ss-dataline') || {}).textContent,
  syncButton: (function () { const b = document.querySelector('#ss-syncsel-btn span'); return b ? b.textContent : null; })(),
  selectionShown: (function () { const b = document.querySelector('#ss-selactions'); return !!b && getComputedStyle(b).display !== 'none'; })(),
  rows: Array.from(document.querySelectorAll('#ss-list .ss-row')).map((r, i) => ({
    i, name: (r.querySelector('.ss-name') || {}).textContent, picked: r.classList.contains('selected'),
  })),
  languageBox: (function () {
    const s = document.querySelector('#ss-sellang');
    return s ? { text: s.options[s.selectedIndex] ? s.options[s.selectedIndex].textContent : null, options: s.options.length, disabled: s.disabled } : null;
  })(),
});

const clickRow = (name) => {
  const rows = Array.from(document.querySelectorAll('#ss-list .ss-row'));
  const row = name ? rows.find((r) => (r.querySelector('.ss-name') || {}).textContent === name) : rows[0];
  if (!row) return null;
  row.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }));
  return (row.querySelector('.ss-name') || {}).textContent;
};

// Picking rows from the middle of the list, so "picks sort to the top" is visible in the unfiltered list
// and not only after a search.
const rightClickRows = (offsets) => {
  const all = Array.from(document.querySelectorAll('#ss-list .ss-row'));
  const rows = offsets.map((i) => all[i]).filter(Boolean);
  rows.forEach((r) => r.dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true })));
  return rows.map((r) => (r.querySelector('.ss-name') || {}).textContent);
};

(async () => {
  const user = await (await fetch(BASE + '/Users/New', {
    method: 'POST', headers: { 'Content-Type': 'application/json', Authorization: AUTH },
    body: JSON.stringify({ Name: 'gui-c-' + Date.now().toString(36) }),
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
  await page.evaluate(() => { const el = document.querySelector('#ss-list'); if (el) el.scrollIntoView({ block: 'start' }); });
  await wait(500);

  const steps = [];
  steps.push({ step: 'as loaded', state: await page.evaluate(state) });

  const picked = await page.evaluate(rightClickRows, [4, 5, 6]);
  await wait(6000);
  steps.push({ step: 'three rows picked by right-click', picked, state: await page.evaluate(state) });
  await page.screenshot({ path: SHOTS + '/' + LABEL + '-picked.png' });

  // C3/C4: a search that hides one of the picks
  await page.fill('#ss-search', 'helicopter');
  await wait(2500);
  steps.push({ step: 'searched (one pick hidden)', search: 'helicopter', state: await page.evaluate(state) });
  await page.screenshot({ path: SHOTS + '/' + LABEL + '-searched.png' });

  // C3: pick one more file found by that search - does the earlier selection survive?
  const added = await page.evaluate(clickRow, null);
  await wait(2500);
  steps.push({ step: 'one more row clicked while searching', added, state: await page.evaluate(state) });
  await page.screenshot({ path: SHOTS + '/' + LABEL + '-clicked-in-search.png' });

  await page.fill('#ss-search', '');
  await wait(2500);
  steps.push({ step: 'search cleared', state: await page.evaluate(state) });

  const out = { label: LABEL, steps };
  fs.writeFileSync(__dirname + '/' + LABEL + '-items.json', JSON.stringify(out, null, 1));
  console.log(JSON.stringify(out, null, 1));
  await browser.close();
  await fetch(BASE + '/Users/' + user.Id, { method: 'DELETE', headers: { Authorization: AUTH } });
})();
