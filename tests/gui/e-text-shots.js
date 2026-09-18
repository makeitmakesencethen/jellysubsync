/* E: the text the plugin shows, photographed where it lives - the Sync tab's opening paragraph, the
   "Multi-subtitle sync mode" description and the "Cached data" description.

   Three clips, one per item, so each rewrite can be judged as rendered rather than as markup. Usage:
   PLAYWRIGHT_BROWSERS_PATH=... node e-text-shots.js [label]
*/
const fs = require('fs');
const { chromium } = require('/tmp/audit/pw/node_modules/playwright');
const BASE = 'http://127.0.0.1:8096';
const IDS = JSON.parse(fs.readFileSync(__dirname + '/ids.json', 'utf8'));
const LABEL = process.argv[2] || 'e';
const SHOTS = __dirname + '/shots';
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const AUTH = 'MediaBrowser Token="' + IDS.admin_token + '"';

const clipAround = (selector, padTop, padBottom, maxHeight) => {
  const el = document.querySelector(selector);
  if (!el) return null;
  const r = el.getBoundingClientRect();
  const y = Math.max(0, r.y - padTop);
  const height = Math.min(maxHeight || 1400, r.height + padTop + padBottom);
  return { x: Math.max(0, r.x - 24), y, width: Math.min(1330, r.width + 48), height };
};

(async () => {
  const user = await (await fetch(BASE + '/Users/New', {
    method: 'POST', headers: { 'Content-Type': 'application/json', Authorization: AUTH },
    body: JSON.stringify({ Name: 'gui-e-' + Date.now().toString(36) }),
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
    if (await page.evaluate(() => document.querySelectorAll('#ss-list .ss-row').length > 3)) break;
  }

  const out = { label: LABEL, clips: [] };

  // E3: the Sync tab's opening paragraph
  const e3 = await page.evaluate((args) => {
    const el = document.querySelector(args.sel);
    if (!el) return null;
    const r = el.getBoundingClientRect();
    return { x: Math.max(0, r.x - 24), y: Math.max(0, r.y - args.top),
             width: Math.min(1330, r.width + 48), height: Math.min(args.max, r.height + args.top + args.bottom) };
  }, { sel: '#panel-sync > p.sectionDescription', top: 40, bottom: 40, max: 400 });
  if (e3) {
    await page.screenshot({ path: SHOTS + '/' + LABEL + '-e3-sync-intro.png', clip: e3 });
    out.clips.push({ item: 'E3', file: LABEL + '-e3-sync-intro.png',
      text: await page.evaluate(() => (document.querySelector('#panel-sync > p.sectionDescription') || {}).innerText) });
  }

  // E1 and E2 live in Settings
  await page.evaluate(() => { const b = document.querySelector('.ss-nav-item[data-panel="settings"]'); if (b) b.click(); });
  await wait(1500);

  await page.evaluate(() => {
    const head = Array.from(document.querySelectorAll('#panel-settings .ss-subhead')).find((h) => /Multi-subtitle sync mode/i.test(h.textContent));
    if (head) head.scrollIntoView({ block: 'start' });
  });
  await wait(600);
  const e1 = await page.evaluate(() => {
    const head = Array.from(document.querySelectorAll('#panel-settings .ss-subhead')).find((h) => /Multi-subtitle sync mode/i.test(h.textContent));
    if (!head) return null;
    const field = head.nextElementSibling;
    const r = field.getBoundingClientRect();
    return { clip: { x: Math.max(0, r.x - 24), y: Math.max(0, r.y - 46), width: Math.min(1330, r.width + 48), height: Math.min(700, r.height + 60) },
             text: field.innerText };
  });
  if (e1) {
    await page.screenshot({ path: SHOTS + '/' + LABEL + '-e1-multimode.png', clip: e1.clip });
    out.clips.push({ item: 'E1', file: LABEL + '-e1-multimode.png', text: e1.text });
  }

  await page.evaluate(() => {
    const el = document.querySelector('#ss-clearcache');
    if (el) el.scrollIntoView({ block: 'center' });
  });
  await wait(600);
  const e2 = await page.evaluate(() => {
    const field = document.querySelector('#ss-clearcache') ? document.querySelector('#ss-clearcache').closest('.ss-field') : null;
    if (!field) return null;
    const r = field.getBoundingClientRect();
    return { clip: { x: Math.max(0, r.x - 24), y: Math.max(0, r.y - 16), width: Math.min(1330, r.width + 48), height: Math.min(700, r.height + 40) },
             text: field.innerText };
  });
  if (e2) {
    await page.screenshot({ path: SHOTS + '/' + LABEL + '-e2-cachedata.png', clip: e2.clip });
    out.clips.push({ item: 'E2', file: LABEL + '-e2-cachedata.png', text: e2.text });
  }

  fs.writeFileSync(__dirname + '/' + LABEL + '-text.json', JSON.stringify(out, null, 1));
  console.log(JSON.stringify(out, null, 1));
  await browser.close();
  await fetch(BASE + '/Users/' + user.Id, { method: 'DELETE', headers: { Authorization: AUTH } });
})();
