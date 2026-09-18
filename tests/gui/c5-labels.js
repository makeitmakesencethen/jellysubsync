/* C5: what every sync button says, on the page's own rows and for the selection.

   The buttons only exist on the selected row (and for the selection), so this walks the three cases a user
   meets: a movie row (one track or all of them), a series row (scope + language filter), and the selection
   bar. Each step records the button's own text and the controls that decide it.

   Usage: PLAYWRIGHT_BROWSERS_PATH=... node c5-labels.js [label]
*/
const fs = require('fs');
const { chromium } = require('/tmp/audit/pw/node_modules/playwright');
const BASE = 'http://127.0.0.1:8096';
const IDS = JSON.parse(fs.readFileSync(__dirname + '/ids.json', 'utf8'));
const LABEL = process.argv[2] || 'c5';
const SHOTS = __dirname + '/shots';
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const AUTH = 'MediaBrowser Token="' + IDS.admin_token + '"';

const buttons = () => ({
  selectionButton: (function () { const b = document.querySelector('#ss-syncsel-btn span'); return b ? b.textContent : null; })(),
  rowButton: (function () { const b = document.querySelector('#ss-syncbtn span'); return b ? b.textContent : null; })(),
  rowName: (function () { const r = document.querySelector('#ss-list .ss-row.selected .ss-name'); return r ? r.textContent : null; })(),
  rowType: (function () { const r = document.querySelector('#ss-list .ss-row.selected .ss-meta'); return r ? r.textContent : null; })(),
  trackPick: (function () {
    const s = document.querySelector('#ss-trackpick');
    return s ? { options: s.options.length, selected: s.options[s.selectedIndex].textContent, value: s.value } : null;
  })(),
  langFilter: (function () {
    const s = document.querySelector('#ss-langfilter');
    return s ? { options: s.options.length, selected: s.options[s.selectedIndex].textContent, value: s.value } : null;
  })(),
  dataline: (document.querySelector('#ss-dataline') || {}).textContent,
});

const clipOf = (sel) => {
  const el = document.querySelector(sel);
  if (!el) return null;
  const r = el.getBoundingClientRect();
  return { x: Math.max(0, r.x - 8), y: Math.max(0, r.y - 8), width: Math.min(1330, r.width + 16), height: r.height + 16 };
};

(async () => {
  const user = await (await fetch(BASE + '/Users/New', {
    method: 'POST', headers: { 'Content-Type': 'application/json', Authorization: AUTH },
    body: JSON.stringify({ Name: 'gui-c5-' + Date.now().toString(36) }),
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

  // the selection bar
  await page.evaluate(() => {
    const rows = Array.from(document.querySelectorAll('#ss-list .ss-row')).slice(0, 3);
    rows.forEach((r) => r.dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true })));
  });
  await wait(8000);
  const selClip = await page.evaluate(clipOf, '#ss-selactions');
  steps.push({ step: 'selection of three rows', state: await page.evaluate(buttons) });
  if (selClip) await page.screenshot({ path: SHOTS + '/' + LABEL + '-selection.png', clip: selClip });

  // a movie row: the button states what its own picker would queue
  await page.evaluate(() => {
    const rows = Array.from(document.querySelectorAll('#ss-list .ss-row'));
    const movie = rows.find((r) => /Movie/.test((r.querySelector('.ss-meta') || {}).textContent || ''));
    if (movie) movie.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }));
  });
  await wait(6000);
  steps.push({ step: 'a movie row selected (all tracks)', state: await page.evaluate(buttons) });
  let rowClip = await page.evaluate(clipOf, '#ss-list .ss-row.selected');
  if (rowClip) await page.screenshot({ path: SHOTS + '/' + LABEL + '-movie-all.png', clip: rowClip });

  const changed = await page.evaluate(() => {
    const s = document.querySelector('#ss-trackpick');
    if (!s || s.options.length < 3) return null;
    s.value = s.options[2].value;
    s.dispatchEvent(new Event('change', { bubbles: true }));
    return s.options[2].textContent;
  });
  await wait(1200);
  steps.push({ step: 'one track picked from that movie', picked: changed, state: await page.evaluate(buttons) });
  rowClip = await page.evaluate(clipOf, '#ss-list .ss-row.selected');
  if (rowClip) await page.screenshot({ path: SHOTS + '/' + LABEL + '-movie-one.png', clip: rowClip });

  // a series row: the count follows the language filter
  await page.evaluate(() => {
    const rows = Array.from(document.querySelectorAll('#ss-list .ss-row'));
    const series = rows.find((r) => /Series/.test((r.querySelector('.ss-meta') || {}).textContent || ''));
    if (series) series.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }));
  });
  for (let i = 0; i < 40; i++) {
    await wait(1000);
    const ready = await page.evaluate(() => {
      const s = document.querySelector('#ss-langfilter');
      return !!s && s.options.length > 1;
    });
    if (ready) break;
  }
  steps.push({ step: 'a series row selected (all subtitles)', state: await page.evaluate(buttons) });
  rowClip = await page.evaluate(clipOf, '#ss-list .ss-row.selected');
  if (rowClip) await page.screenshot({ path: SHOTS + '/' + LABEL + '-series-all.png', clip: rowClip });

  const langChanged = await page.evaluate(() => {
    const s = document.querySelector('#ss-langfilter');
    if (!s || s.options.length < 2) return null;
    s.value = s.options[1].value;
    s.dispatchEvent(new Event('change', { bubbles: true }));
    return s.options[1].textContent;
  });
  await wait(1200);
  steps.push({ step: 'one language picked from that series', picked: langChanged, state: await page.evaluate(buttons) });
  rowClip = await page.evaluate(clipOf, '#ss-list .ss-row.selected');
  if (rowClip) await page.screenshot({ path: SHOTS + '/' + LABEL + '-series-one.png', clip: rowClip });

  const out = { label: LABEL, steps };
  fs.writeFileSync(__dirname + '/' + LABEL + '-labels.json', JSON.stringify(out, null, 1));
  console.log(JSON.stringify(out, null, 1));
  await browser.close();
  await fetch(BASE + '/Users/' + user.Id, { method: 'DELETE', headers: { Authorization: AUTH } });
})();
