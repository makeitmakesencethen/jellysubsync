/* Does the SubSync dashboard page finish loading its library list?

   The user's page sits on "Loading libraries…" after updating. This loads the same page in Chromium against the
   local test server (which carries the same page code) and records what the page did: the dataline text, the
   library select and the item list, every SubSync request with its status, console errors and page errors.

   Usage: PLAYWRIGHT_BROWSERS_PATH=/opt/data/.playwright node page-library-probe.js [label]
*/
const fs = require('fs');
const { chromium } = require('/tmp/audit/pw/node_modules/playwright');
const BASE = 'http://127.0.0.1:8096';
const IDS = JSON.parse(fs.readFileSync(__dirname + '/ids.json', 'utf8'));
const LABEL = process.argv[2] || 'page-library';
const OUT = __dirname + '/' + LABEL + '.json';

const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const say = (m) => console.log(new Date().toISOString().slice(11, 19) + ' ' + m);

const grantAdmin = async (auth, userId) => {
  const current = await (await fetch(BASE + '/Users/' + userId, { headers: { Authorization: auth } })).json();
  const policy = Object.assign({}, current.Policy, { IsAdministrator: true, EnableAllFolders: true });
  const r = await fetch(BASE + '/Users/' + userId + '/Policy', {
    method: 'POST', headers: { 'Content-Type': 'application/json', Authorization: auth },
    body: JSON.stringify(policy),
  });
  if (!r.ok) throw new Error('could not grant administrator: HTTP ' + r.status);
};

(async () => {
  const auth = 'MediaBrowser Token="' + IDS.admin_token + '"';
  const user = await (await fetch(BASE + '/Users/New', {
    method: 'POST', headers: { 'Content-Type': 'application/json', Authorization: auth },
    body: JSON.stringify({ Name: 'guitest-lib-' + Date.now().toString(36) }),
  })).json();
  await grantAdmin(auth, user.Id);

  const browser = await chromium.launch({ args: ['--no-sandbox'], executablePath: '/opt/data/.playwright/chromium-1234/chrome-linux64/chrome' });
  const ctx = await browser.newContext({ viewport: { width: 1400, height: 900 } });
  const page = await ctx.newPage();

  const calls = [];
  const errors = [];
  page.on('response', (r) => {
    if (/SubSync\//.test(r.url())) calls.push(r.status() + ' ' + r.request().method() + ' ' + r.url().replace(BASE, '').split('?')[0]);
  });
  page.on('pageerror', (e) => errors.push('PAGEERROR ' + String(e).slice(0, 300)));
  page.on('console', (m) => { if (m.type() === 'error') errors.push('CONSOLE ' + m.text().slice(0, 300)); });

  await page.goto(BASE + '/web/', { waitUntil: 'domcontentloaded', timeout: 60000 });
  await wait(3000);
  await page.fill('input[name="username"], input[type="text"]', user.Name);
  const submit = await page.$('button[type="submit"], .btnSubmit');
  if (submit) await submit.click();
  await wait(7000);

  await page.goto(BASE + '/web/configurationpage?name=subsync-main', { waitUntil: 'domcontentloaded', timeout: 60000 });
  const snapshots = [];
  for (let i = 0; i < 20; i++) {
    await wait(1500);
    const snap = await page.evaluate(() => {
      const t = (sel) => { const el = document.querySelector(sel); return el ? (el.textContent || '').trim().slice(0, 120) : null; };
      const list = document.querySelector('#ss-list');
      const lib = document.querySelector('#ss-library');
      return {
        dataline: t('#ss-dataline'),
        diag: t('#ss-diag'),
        badge: t('#ss-badge'),
        libOptions: lib ? lib.options.length : null,
        listRows: list ? list.children.length : null,
        hasPageScript: typeof window.SubSyncMain !== 'undefined' || !!document.querySelector('#ss-runbox'),
        bodyText: (document.body.innerText || '').replace(/\s+/g, ' ').slice(0, 200),
      };
    });
    snapshots.push(snap);
    if (snap.listRows && snap.listRows > 0) break;
    if (i === 3 || i === 9 || i === 19) say(JSON.stringify(snap).slice(0, 240));
  }

  const final = snapshots[snapshots.length - 1];
  const result = {
    label: LABEL, user: user.Name, final, snapshots, calls, errors,
    verdict: {
      dataline_still_loading: /Loading libraries/i.test(final.dataline || ''),
      library_select_filled: (final.libOptions || 0) > 0,
      items_rendered: (final.listRows || 0) > 0,
      error_count: errors.length,
      pass: !/Loading libraries/i.test(final.dataline || '') && (final.listRows || 0) > 0 && errors.length === 0,
    },
  };
  fs.writeFileSync(OUT, JSON.stringify(result, null, 2));
  say('final: ' + JSON.stringify(final).slice(0, 300));
  say('SubSync calls: ' + JSON.stringify(calls.slice(0, 12)));
  say('errors: ' + JSON.stringify(errors.slice(0, 5)));
  console.log('verdict:', JSON.stringify(result.verdict));
  console.log('wrote', OUT);
  await browser.close();
})().catch((e) => { console.error('probe failed:', e); process.exit(1); });
