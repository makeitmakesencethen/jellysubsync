/* What the plugin page does, once its script is actually running.

   Jellyfin 12 fetches the page's <script src> but does not execute it (see the report), so this probe
   attaches the same served script by hand — the way that is measured to run — and then drives the page
   the way a user would: read the settings, save one, start a run elsewhere on the server, watch the
   mirrored run box, and press the global Kill twice.

   Usage: PLAYWRIGHT_BROWSERS_PATH=... node page-interactive-probe.js [label]
*/
const fs = require('fs');
const { chromium } = require('/tmp/audit/pw/node_modules/playwright');
const BASE = 'http://127.0.0.1:8096';
const IDS = JSON.parse(fs.readFileSync(__dirname + '/ids.json', 'utf8'));
const LABEL = process.argv[2] || 'page-interactive';
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
const results = { label: LABEL, steps: [] };

(async () => {
  const auth = 'MediaBrowser Token="' + IDS.admin_token + '"';
  const user = await (await fetch(BASE + '/Users/New', {
    method: 'POST', headers: { 'Content-Type': 'application/json', Authorization: auth },
    body: JSON.stringify({ Name: 'guitest-page-' + Date.now().toString(36) }),
  })).json();
  await grantAdmin(auth, user.Id);

  const browser = await chromium.launch({ args: ['--no-sandbox'], executablePath: '/opt/data/.playwright/chromium-1234/chrome-linux64/chrome' });
  const page = await (await browser.newContext({ viewport: { width: 1400, height: 900 } })).newPage();
  const calls = [];
  page.on('request', (r) => { if (/SubSync\//.test(r.url())) calls.push(r.method() + ' ' + r.url().replace(BASE, '').split('?')[0]); });
  page.on('pageerror', (e) => calls.push('PAGEERROR ' + String(e).slice(0, 160)));

  await page.goto(BASE + '/web/', { waitUntil: 'domcontentloaded', timeout: 60000 });
  await wait(3500);
  await page.fill('input[name="username"], input[type="text"]', user.Name);
  const submit = await page.$('button[type="submit"], .btnSubmit, button:has-text("Sign In")');
  if (submit) await submit.click();
  await wait(8000);
  await page.goto(BASE + '/web/configurationpage?name=subsync-main', { waitUntil: 'domcontentloaded', timeout: 60000 });
  await wait(5000);

  // Attach the page's script the way that is measured to run, since the client does not do it.
  const attached = await page.evaluate(async () => {
    const src = await (await fetch('/SubSync/MainScript?t=' + Date.now())).text();
    window.__subsyncPageLoaded = false; // let the copy run even if the page tag half-loaded it
    return await new Promise((resolve) => {
      const s = document.createElement('script');
      s.src = URL.createObjectURL(new Blob([src], { type: 'application/javascript' }));
      s.onload = () => resolve('attached');
      s.onerror = () => resolve('attach failed');
      document.head.appendChild(s);
      setTimeout(() => resolve('attached (no load event)'), 4000);
    });
  });
  await wait(4000);
  results.steps.push({ step: 'attach the page script by hand', attached });
  results.steps.push({ step: 'page after start', state: await page.evaluate(() => ({
    status: (document.querySelector('#ss-status') || {}).textContent.slice(0, 90),
    libraries: (document.querySelector('#ss-library') || {}).innerHTML.slice(0, 90),
    killLabel: (document.querySelector('#ss-cancel') || {}).textContent,
  })) });

  // The settings the page shows, and one saved value read back from the server.
  const settings = await page.evaluate(async () => {
    const tabs = Array.from(document.querySelectorAll('.ss-nav-item'));
    const settingsTab = tabs.find((t) => /setting/i.test(t.textContent));
    if (settingsTab) settingsTab.click();
    await new Promise((r) => setTimeout(r, 1200));
    return {
      tabs: tabs.map((t) => t.textContent.trim()).slice(0, 6),
      maxReferenceOffsetField: (document.querySelector('#ss-maxrefoffset') || {}).value,
      saveButton: !!document.querySelector('#ss-save'),
    };
  });
  const storedBefore = await (await fetch(BASE + '/SubSync/Configuration', { headers: { Authorization: auth } })).json();
  settings.storedMaxReferenceOffset = storedBefore.MaxSubtitleReferenceOffsetSeconds;
  results.steps.push({ step: 'settings tab', settings });

  // A run started elsewhere on the server (not by this page): does the page mirror it?
  // Queued with the page's own session, not the harness token: the page shows the runs of the user it
  // is signed in as, so a batch started by another account would be filtered out and nothing would be
  // mirrored — which would say nothing about the mirror.
  const batch = await page.evaluate(async (itemId) => {
    const creds = JSON.parse(localStorage.getItem('jellyfin_credentials') || '{}');
    const accessToken = ((creds.Servers || [])[0] || {}).AccessToken;
    const r = await fetch('/SubSync/Batch', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', Authorization: 'MediaBrowser Token="' + accessToken + '"' },
      body: JSON.stringify({ Label: 'page-interactive', Tasks: Array.from({ length: 40 }, (_, i) => ({ ItemId: itemId, SubtitleIndex: i + 10 })) }),
    });
    const body = await r.text();
    return { status: r.status, body: body.slice(0, 160) };
  }, IDS.movie);
  let mirrored = null;
  for (let i = 0; i < 40; i++) {
    await wait(700);
    mirrored = await page.evaluate(() => {
      const box = document.querySelector('#ss-runbox');
      const kill = document.querySelector('#ss-cancel');
      return { hidden: box ? box.classList.contains('ss-hidden') : null, killLabel: kill ? kill.textContent.trim() : null,
               runLine: (document.querySelector('#ss-run-label') || {}).textContent };
    });
    if (mirrored && mirrored.hidden === false) break;
  }
  results.steps.push({ step: 'a run this page did not start', batchId: batch.Id, mirrored });

  // F29: the first press asks, the second press kills.
  const presses = [];
  for (const note of ['first press', 'second press']) {
    const state = await page.evaluate(async () => {
      const kill = document.querySelector('#ss-cancel');
      kill.click();
      return { label: kill.textContent.trim(), hint: (document.querySelector('#ss-phase') || {}).textContent };
    });
    await wait(1200);
    presses.push({ note, state });
  }
  await wait(2500);
  const killed = await (await fetch(BASE + '/SubSync/Active', { headers: { Authorization: auth } })).json();
  results.steps.push({ step: 'F29 kill', presses, activeAfter: { running: (killed.running || []).length, queued: killed.queued } });
  results.calls = calls;
  fs.writeFileSync(OUT, JSON.stringify(results, null, 2) + '\n');
  console.log(JSON.stringify(results.steps.slice(0, 4), null, 1).slice(0, 1400));
  console.log('wrote ' + OUT);
  await fetch(BASE + '/Users/' + user.Id, { method: 'DELETE', headers: { Authorization: auth } });
  await browser.close();
})().catch((e) => { console.error('probe failed: ' + e.message); process.exit(1); });
