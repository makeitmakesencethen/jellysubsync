/* Reproduce the field failure: a real web client *does* have window.ApiClient.

   2.0.10 shipped a rename that made `currentUserId()` call itself, so with window.ApiClient present (every
   real Jellyfin web client) the page died and sat on "Loading libraries…" — while every browser test here ran
   where ApiClient is undefined and never took that branch. This probe defines a minimal ApiClient before any
   page script runs, exactly like the web client does, and checks that the page lists libraries.

   Usage: PLAYWRIGHT_BROWSERS_PATH=... node page-apiclient-probe.js [label]
*/
const fs = require('fs');
const { chromium } = require('/tmp/audit/pw/node_modules/playwright');
const BASE = 'http://127.0.0.1:8096';
const IDS = JSON.parse(fs.readFileSync(__dirname + '/ids.json', 'utf8'));
const LABEL = process.argv[2] || 'page-apiclient';
const OUT = __dirname + '/' + LABEL + '.json';
const grantAdmin = async (auth, userId) => {
  // A partial body for POST /Users/{id}/Policy answers 400 and leaves the account as it was, so the
  // throwaway accounts stayed non-administrators and every elevated call from the page (settings, Kill)
  // answered 403. Send the account's own policy back with the flag set.
  const current = await (await fetch(BASE + '/Users/' + userId, { headers: { Authorization: auth } })).json();
  const policy = Object.assign({}, current.Policy, { IsAdministrator: true, EnableAllFolders: true });
  const r = await fetch(BASE + '/Users/' + userId + '/Policy', {
    method: 'POST', headers: { 'Content-Type': 'application/json', Authorization: auth }, body: JSON.stringify(policy),
  });
  if (!r.ok) { throw new Error('could not grant administrator: HTTP ' + r.status); }
  return true;
};

const wait = (ms) => new Promise((r) => setTimeout(r, ms));

(async () => {
  const auth = 'MediaBrowser Token="' + IDS.admin_token + '"';
  const user = await (await fetch(BASE + '/Users/New', {
    method: 'POST', headers: { 'Content-Type': 'application/json', Authorization: auth },
    body: JSON.stringify({ Name: 'guitest-api-' + Date.now().toString(36) }),
  })).json();
  await grantAdmin(auth, user.Id);

  const browser = await chromium.launch({ args: ['--no-sandbox'], executablePath: '/opt/data/.playwright/chromium-1234/chrome-linux64/chrome' });
  const context = await browser.newContext({ viewport: { width: 1400, height: 900 } });
  const page = await context.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push(String(e).slice(0, 200)));

  // sign in the way a user does, so the browser holds a session
  await page.goto(BASE + '/web/', { waitUntil: 'domcontentloaded', timeout: 60000 });
  await wait(3500);
  await page.fill('input[name="username"], input[type="text"]', user.Name);
  const submit = await page.$('button[type="submit"], .btnSubmit, button:has-text("Sign In")');
  if (submit) await submit.click();
  await wait(7000);

  const session = await page.evaluate(() => {
    const creds = JSON.parse(localStorage.getItem('jellyfin_credentials') || '{}');
    const server = (creds.Servers || [])[0] || {};
    return { token: server.AccessToken || '', userId: server.UserId || '' };
  });
  let userId = session.userId;
  if (!userId) {
    userId = (await (await fetch(BASE + '/Users/Me', { headers: { Authorization: auth } })).json()).Id;
  }

  // A new page in the same context, with the web client's API object defined before anything else runs.
  const page2 = await context.newPage();
  const logs = [];
  page2.on('console', (m) => logs.push(m.text().slice(0, 120)));
  page2.on('pageerror', (e) => errors.push('PAGE2 ' + String(e).slice(0, 200)));
  await page2.addInitScript(({ token, userId }) => {
    window.ApiClient = {
      accessToken: function () { return token; },
      getCurrentUserId: function () { return userId; },
      getUrl: function (path) { return '/' + path; },
    };
  }, { token: session.token, userId: userId });

  await page2.goto(BASE + '/web/configurationpage?name=subsync-main', { waitUntil: 'domcontentloaded', timeout: 60000 });
  let state = null;
  for (let i = 0; i < 25; i++) {
    await wait(1000);
    state = await page2.evaluate(() => ({
      status: ((document.querySelector('#ss-status') || {}).textContent || '').slice(0, 70),
      dataline: ((document.querySelector('#ss-dataline') || {}).textContent || '').slice(0, 110),
      libraryOptions: Array.from(document.querySelectorAll('#ss-library option')).map((o) => o.textContent).slice(0, 6),
      diag: ((document.querySelector('#ss-diag') || {}).textContent || '').slice(0, 140),
      rows: document.querySelectorAll('#ss-items tr, #ss-items .ss-row').length,
    }));
    if (/librar/i.test(state.dataline) && !/Loading libraries/.test(state.dataline)) break;
  }

  const result = {
    label: LABEL,
    session: { tokenPresent: !!session.token, userId: userId || '' },
    state,
    apiClientHandover: logs.filter((l) => /subsync/i.test(l)).slice(0, 4),
    errors,
    verdict: (state && state.libraryOptions.length > 1 && !/^Loading libraries/.test(state.dataline)) ? 'PASS' : 'CHECK',
  };
  fs.writeFileSync(OUT, JSON.stringify(result, null, 2) + '\n');
  console.log(JSON.stringify(result, null, 1).slice(0, 1400));
  console.log('wrote ' + OUT);
  await fetch(BASE + '/Users/' + user.Id, { method: 'DELETE', headers: { Authorization: auth } });
  await browser.close();
})().catch((e) => { console.error('probe failed: ' + e.message); process.exit(1); });
