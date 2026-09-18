/* B3: what the multi-select language filter box looks like while the subtitle lists are being read.

   The complaint: while many files are selected, the filter box at the top is replaced by plain "LOADING"
   text. This probe makes the load slow enough to photograph (the track requests are delayed by 4 s each),
   selects everything in the library, and records the box's own state - its text, how many options it holds,
   whether it is disabled, and its box on screen - every 250 ms until the scan finishes. A clip of the row is
   written while it is busy and once it has settled.

   Usage: PLAYWRIGHT_BROWSERS_PATH=... node b3-langbox.js [label]
*/
const fs = require('fs');
const { chromium } = require('/tmp/audit/pw/node_modules/playwright');
const BASE = 'http://127.0.0.1:8096';
const IDS = JSON.parse(fs.readFileSync(__dirname + '/ids.json', 'utf8'));
const LABEL = process.argv[2] || 'b3';
const SHOTS = __dirname + '/shots';
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const AUTH = 'MediaBrowser Token="' + IDS.admin_token + '"';
const DELAY_MS = Number(process.argv[3] || 4000);

const boxState = () => {
  const sel = document.querySelector('#ss-sellang');
  const row = document.querySelector('#ss-selactions');
  if (!sel) return { missing: true };
  const r = sel.getBoundingClientRect();
  return {
    text: sel.options[sel.selectedIndex] ? sel.options[sel.selectedIndex].textContent : null,
    firstOptionText: sel.options[0] ? sel.options[0].textContent : null,
    options: sel.options.length,
    optionTexts: Array.from(sel.options).map((o) => o.textContent).slice(0, 5),
    disabled: sel.disabled,
    opacity: getComputedStyle(sel).opacity,
    visibility: getComputedStyle(sel).visibility,
    display: getComputedStyle(sel).display,
    box: [Math.round(r.x), Math.round(r.y), Math.round(r.width), Math.round(r.height)],
    rowVisible: row ? getComputedStyle(row).display !== 'none' : false,
    note: (document.querySelector('#ss-selnote') || {}).textContent || '',
  };
};

(async () => {
  const user = await (await fetch(BASE + '/Users/New', {
    method: 'POST', headers: { 'Content-Type': 'application/json', Authorization: AUTH },
    body: JSON.stringify({ Name: 'gui-b3-' + Date.now().toString(36) }),
  })).json();
  const current = await (await fetch(BASE + '/Users/' + user.Id, { headers: { Authorization: AUTH } })).json();
  await fetch(BASE + '/Users/' + user.Id + '/Policy', {
    method: 'POST', headers: { 'Content-Type': 'application/json', Authorization: AUTH },
    body: JSON.stringify(Object.assign({}, current.Policy, { IsAdministrator: true, EnableAllFolders: true })),
  });

  const browser = await chromium.launch({ args: ['--no-sandbox'], executablePath: '/opt/data/.playwright/chromium-1234/chrome-linux64/chrome' });
  const page = await (await browser.newContext({ viewport: { width: 1400, height: 1000 } })).newPage();
  // The lists are read through /SubSync/Subtitles/Batch; delaying each answer is what makes the busy state
  // observable at all (on this rig the whole scan otherwise finishes inside one poll interval).
  await page.route('**/SubSync/Subtitles/Batch', async (route) => {
    await wait(DELAY_MS);
    await route.continue();
  });

  await page.goto(BASE + '/web/', { waitUntil: 'domcontentloaded', timeout: 60000 });
  await wait(3500);
  await page.fill('input[name="username"], input[type="text"]', user.Name);
  const submit = await page.$('button[type="submit"], .btnSubmit, button:has-text("Sign In")');
  if (submit) await submit.click();
  await wait(9000);
  await page.goto(BASE + '/web/#/configurationpage?name=subsync-main', { waitUntil: 'domcontentloaded', timeout: 60000 });
  for (let i = 0; i < 40; i++) {
    await wait(1000);
    const ready = await page.evaluate(() => document.querySelectorAll('#ss-list .ss-row').length > 3);
    if (ready) break;
  }
  await page.scrollIntoViewIfNeeded ? null : null;
  await page.evaluate(() => { const el = document.querySelector('#ss-selactions'); if (el) el.scrollIntoView({ block: 'center' }); });
  await wait(500);

  const before = await page.evaluate(boxState);

  // Select everything in the library, which starts the language scan.
  await page.evaluate(() => { const a = document.querySelector('#ss-selall-link'); if (a) a.click(); });
  const observed = [];
  let shot = false;
  for (let i = 0; i < 80; i++) {
    await wait(250);
    const s = await page.evaluate(boxState);
    const key = JSON.stringify({ t: s.text, o: s.options, d: s.disabled, r: s.rowVisible });
    if (!observed.length || observed[observed.length - 1].key !== key) {
      observed.push(Object.assign({ key, at: i * 250 }, s));
    }
    if (s.text && /Sorting|LOADING/i.test(s.text) && !shot) {
      const row = await page.evaluate(() => {
        const el = document.querySelector('#ss-selactions').getBoundingClientRect();
        return { x: el.x, y: el.y, w: el.width, h: el.height };
      });
      await page.screenshot({ path: SHOTS + '/' + LABEL + '-loading.png',
        clip: { x: Math.max(0, row.x - 20), y: Math.max(0, row.y - 30), width: Math.min(1300, row.w + 60), height: row.h + 70 } });
      shot = true;
    }
    if (siteDone(s)) break;
  }
  function siteDone(s) {
    return !/Sorting|LOADING/i.test(s.text || '') && s.options > 1;
  }
  await wait(1500);
  const after = await page.evaluate(boxState);
  const row = await page.evaluate(() => {
    const el = document.querySelector('#ss-selactions').getBoundingClientRect();
    return { x: el.x, y: el.y, w: el.width, h: el.height };
  });
  await page.screenshot({ path: SHOTS + '/' + LABEL + '-settled.png',
    clip: { x: Math.max(0, row.x - 20), y: Math.max(0, row.y - 30), width: Math.min(1300, row.w + 60), height: row.h + 70 } });
  await page.screenshot({ path: SHOTS + '/' + LABEL + '-full.png' });

  // A second scan (picking one more row) while the box already holds a list: the languages it has found are
  // not invalidated by re-reading, so they must stay in the box and stay selectable while it works.
  await page.evaluate(() => {
    const rows = Array.from(document.querySelectorAll('#ss-list .ss-row'));
    const free = rows.find((r) => !r.classList.contains('selected'));
    // A plain click is what starts a scan (the right-click path only toggles the pick), so this is how a
    // user adds another file to the selection.
    if (free) free.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }));
  });
  const rescan = [];
  for (let i = 0; i < 80; i++) {
    await wait(250);
    const s = await page.evaluate(boxState);
    const key = JSON.stringify({ t: s.text, o: s.options, d: s.disabled });
    if (!rescan.length || rescan[rescan.length - 1].key !== key) {
      rescan.push(Object.assign({ key, at: i * 250 }, s));
    }
    if (i > 8 && /Sorting/i.test(s.text || '') && s.options > 1) break;
  }
  const rescanShot = await page.evaluate(() => {
    const el = document.querySelector('#ss-selactions').getBoundingClientRect();
    return { x: el.x, y: el.y, w: el.width, h: el.height };
  });
  await page.screenshot({ path: SHOTS + '/' + LABEL + '-rescan.png',
    clip: { x: Math.max(0, rescanShot.x - 20), y: Math.max(0, rescanShot.y - 30), width: Math.min(1300, rescanShot.w + 60), height: rescanShot.h + 70 } });

  const out = { label: LABEL, delayMs: DELAY_MS, before, observed, after, rescan };
  fs.writeFileSync(__dirname + '/' + LABEL + '-langbox.json', JSON.stringify(out, null, 1));
  console.log(JSON.stringify(out, null, 1));
  await browser.close();
  await fetch(BASE + '/Users/' + user.Id, { method: 'DELETE', headers: { Authorization: AUTH } });
})();
