/* Does the plugin page start by itself, and do F29 and the mirrored run box work once it has?

   The page's script is attached by the client script the middleware injects (SubSync.js), because the
   <script src> that arrives with the injected page markup is fetched but never executed in Jellyfin 12.
   This probe does **not** attach anything by hand: it loads the page the way the web client does, then

     1. reports whether the page filled itself in (status line, libraries) and whether anything threw;
     2. queues a run with the page's own session and waits for the run box to appear (the mirrored-run
        fix: `renderMirroredBatch` used to fill a box that was never shown);
     3. presses the global Cancel/Kill button twice and reports the two labels plus the phase line (F29:
        the first press must ask, the second must kill), and checks the server ended up with nothing
        running.

   Usage: PLAYWRIGHT_BROWSERS_PATH=... node page-selfstart-probe.js [label]
*/
const fs = require('fs');
const { chromium } = require('/tmp/audit/pw/node_modules/playwright');
const BASE = 'http://127.0.0.1:8096';
const IDS = JSON.parse(fs.readFileSync(__dirname + '/ids.json', 'utf8'));
const LABEL = process.argv[2] || 'page-selfstart';
const OUT = __dirname + '/' + LABEL + '.json';
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const results = { label: LABEL, steps: [] };

(async () => {
  const auth = 'MediaBrowser Token="' + IDS.admin_token + '"';
  const user = await (await fetch(BASE + '/Users/New', {
    method: 'POST', headers: { 'Content-Type': 'application/json', Authorization: auth },
    body: JSON.stringify({ Name: 'guitest-self-' + Date.now().toString(36) }),
  })).json();
  await fetch(BASE + '/Users/' + user.Id + '/Policy', {
    method: 'POST', headers: { 'Content-Type': 'application/json', Authorization: auth },
    body: JSON.stringify({ IsAdministrator: true, EnableAllFolders: true }),
  });

  const browser = await chromium.launch({ args: ['--no-sandbox'], executablePath: '/opt/data/.playwright/chromium-1234/chrome-linux64/chrome' });
  const page = await (await browser.newContext({ viewport: { width: 1400, height: 900 } })).newPage();
  const calls = [];
  page.on('request', (r) => { if (/SubSync\//.test(r.url())) calls.push(r.method() + ' ' + r.url().replace(BASE, '').split('?')[0]); });
  page.on('pageerror', (e) => calls.push('PAGEERROR ' + String(e).slice(0, 160)));
  page.on('response', (r) => { if (r.status() >= 400) calls.push('HTTP ' + r.status() + ' ' + r.url().replace(BASE, '').split('?')[0]); });

  await page.goto(BASE + '/web/', { waitUntil: 'domcontentloaded', timeout: 60000 });
  await wait(3500);
  await page.fill('input[name="username"], input[type="text"]', user.Name);
  const submit = await page.$('button[type="submit"], .btnSubmit, button:has-text("Sign In")');
  if (submit) await submit.click();
  await wait(8000);

  // 1. the page, opened the way the web client opens it, with nothing attached by hand
  await page.goto(BASE + '/web/configurationpage?name=subsync-main', { waitUntil: 'domcontentloaded', timeout: 60000 });
  let started = null;
  for (let i = 0; i < 30; i++) {
    await wait(1000);
    started = await page.evaluate(() => ({
      status: (document.querySelector('#ss-status') || {}).textContent.slice(0, 80),
      libraries: Array.from(document.querySelectorAll('#ss-library option')).length,
      filled: !!((document.querySelector('#ss-status') || {}).textContent || '').match(/ffsubsync source|ffmpeg/),
    }));
    if (started.filled) break;
  }
  results.steps.push({ step: 'page starts by itself', state: started, after_s: null });

  // 2. a run started with the page's own session: does the run box appear?
  const batch = await page.evaluate(async (itemId) => {
    const creds = JSON.parse(localStorage.getItem('jellyfin_credentials') || '{}');
    const accessToken = ((creds.Servers || [])[0] || {}).AccessToken;
    const headers = { 'Content-Type': 'application/json', Authorization: 'MediaBrowser Token="' + accessToken + '"' };
    // The tracks are read from the server rather than assumed: a sidecar added by an earlier run
    // renumbers them (S14), and a hard-coded range then asks for indices that do not exist.
    const tracks = await (await fetch('/SubSync/Subtitles/' + itemId, { headers })).json();
    const wanted = (Array.isArray(tracks) ? tracks : []).slice(0, 20).map((x) => x.Index);
    const r = await fetch('/SubSync/Batch', {
      method: 'POST',
      headers,
      body: JSON.stringify({ Label: 'self-start probe', Tasks: wanted.map((Index) => ({ ItemId: itemId, SubtitleIndex: Index })) }),
    });
    const body = await r.text();
    return { status: r.status, body: body.slice(0, 120) };
  }, IDS.movie);
  let mirrored = null;
  for (let i = 0; i < 60; i++) {
    await wait(700);
    mirrored = await page.evaluate(() => {
      const box = document.querySelector('#ss-runbox');
      const btn = document.querySelector('#ss-cancel');
      return {
        boxHidden: box ? box.classList.contains('ss-hidden') : null,
        buttonVisible: btn ? !btn.classList.contains('ss-hidden') : null,
        buttonLabel: btn ? btn.textContent.trim() : null,
        runLine: ((document.querySelector('#ss-run-label') || {}).textContent || '').slice(0, 70),
        phase: ((document.querySelector('#ss-phase') || {}).textContent || '').slice(0, 90),
      };
    });
    if (mirrored.boxHidden === false) break;
  }
  results.steps.push({ step: 'a run the page did not start', batch, mirrored });

  // 3. F29: the button asks before it kills everything on the server
  const presses = [];
  // The two-press confirmation belongs to the *global* button ("Kill all syncing"), which the page shows
  // once there is no run of its own to cancel. Mirroring a run puts the button in cancel mode, so the
  // first press here is a plain cancel; with that run dropped the button switches to the global kill and
  // the next two presses are the ones F29 changes.
  presses.push({ note: 'cancel the mirrored run', state: await page.evaluate(() => {
    const btn = document.querySelector('#ss-cancel');
    if (btn) btn.click();
    return { label: btn ? btn.textContent.trim() : null };
  }) });
  await wait(4000);
  presses.push({ note: 'after the cancel', state: await page.evaluate(() => {
    const btn = document.querySelector('#ss-cancel');
    return { label: btn ? btn.textContent.trim() : null, phase: ((document.querySelector('#ss-phase') || {}).textContent || '').slice(0, 120) };
  }) });
  for (const note of ['first press', 'second press']) {
    presses.push({ note, state: await page.evaluate(() => {
      const btn = document.querySelector('#ss-cancel');
      if (!btn) return { error: 'no button' };
      btn.click();
      return { label: btn.textContent.trim(), phase: ((document.querySelector('#ss-phase') || {}).textContent || '').slice(0, 160) };
    }) });
    await wait(1500);
  }
  await wait(3000);
  const active = await (await fetch(BASE + '/SubSync/Active', { headers: { Authorization: auth } })).json();
  results.steps.push({ step: 'F29 two-press kill', presses, activeAfter: { running: (active.running || []).length, queued: active.queued } });
  results.calls = calls;

  fs.writeFileSync(OUT, JSON.stringify(results, null, 2) + '\n');
  console.log(JSON.stringify(results.steps, null, 1).slice(0, 2200));
  console.log('wrote ' + OUT);
  await fetch(BASE + '/Users/' + user.Id, { method: 'DELETE', headers: { Authorization: auth } });
  await browser.close();
})().catch((e) => { console.error('probe failed: ' + e.message); process.exit(1); });
