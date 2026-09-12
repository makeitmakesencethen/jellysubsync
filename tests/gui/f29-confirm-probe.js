/* F29 in a browser: the first press of "Kill all syncing" must ask, the second must kill.

   The button only reads "Kill all syncing" when the server has a sync that a cancel cannot stop — the page
   reaches that state through `reportStillRunning()`: after dropping a batch's queued tasks, a job that is
   already running keeps running, and the button turns into the global kill (the source check pins the rest of
   the flow; this probe is about what a user sees).

   So the probe: queue a batch with the page's own session, wait until a task is actually running, press
   Cancel (that drops the queue but leaves the running job), wait for the button to become the global kill,
   then press twice and record the label and the line under it at each step — plus what the server ended up
   with. Nothing is attached by hand: the page starts itself (see page-selfstart-probe.js).

   Usage: PLAYWRIGHT_BROWSERS_PATH=... node f29-confirm-probe.js [label]
*/
const fs = require('fs');
const { chromium } = require('/tmp/audit/pw/node_modules/playwright');
const BASE = 'http://127.0.0.1:8096';
const IDS = JSON.parse(fs.readFileSync(__dirname + '/ids.json', 'utf8'));
const LABEL = process.argv[2] || 'f29-confirm';
const ITEM = process.argv[3] || IDS.movie;
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
const say = (msg) => console.log(new Date().toISOString().slice(11, 19) + ' ' + msg);
const results = { label: LABEL, steps: [] };

const readButton = (page) => page.evaluate(() => {
  const btn = document.querySelector('#ss-cancel');
  const span = btn ? btn.querySelector('span') : null;
  return {
    label: span ? span.textContent.trim() : (btn ? btn.textContent.trim() : null),
    kill: btn ? btn.classList.contains('ss-kill') : null,
    phase: ((document.querySelector('#ss-phase') || {}).textContent || '').slice(0, 180),
  };
});

(async () => {
  const auth = 'MediaBrowser Token="' + IDS.admin_token + '"';
  const user = await (await fetch(BASE + '/Users/New', {
    method: 'POST', headers: { 'Content-Type': 'application/json', Authorization: auth },
    body: JSON.stringify({ Name: 'guitest-f29-' + Date.now().toString(36) }),
  })).json();
  await grantAdmin(auth, user.Id);

  const browser = await chromium.launch({ args: ['--no-sandbox'], executablePath: '/opt/data/.playwright/chromium-1234/chrome-linux64/chrome' });
  const page = await (await browser.newContext({ viewport: { width: 1400, height: 900 } })).newPage();
  const calls = [];
  page.on('request', (r) => { if (/SubSync\//.test(r.url())) calls.push(r.method() + ' ' + r.url().replace(BASE, '').split('?')[0]); });
  page.on('pageerror', (e) => calls.push('PAGEERROR ' + String(e).slice(0, 140)));

  await page.goto(BASE + '/web/', { waitUntil: 'domcontentloaded', timeout: 60000 });
  await wait(3500);
  await page.fill('input[name="username"], input[type="text"]', user.Name);
  const submit = await page.$('button[type="submit"], .btnSubmit, button:has-text("Sign In")');
  if (submit) await submit.click();
  await wait(8000);
  await page.goto(BASE + '/web/configurationpage?name=subsync-main', { waitUntil: 'domcontentloaded', timeout: 60000 });
  for (let i = 0; i < 25; i++) {
    await wait(1000);
    const started = await page.evaluate(() => /ffsubsync source/.test((document.querySelector('#ss-status') || {}).textContent || ''));
    if (started) break;
  }
  say('page up: ' + JSON.stringify(await readButton(page)));
  results.steps.push({ step: 'the page is up', button: await readButton(page) });

  // Queue with the page's own session, using tracks that exist (a sidecar renumbers them — S14).
  const queue = async () => page.evaluate(async (itemId) => {
    const creds = JSON.parse(localStorage.getItem('jellyfin_credentials') || '{}');
    const accessToken = ((creds.Servers || [])[0] || {}).AccessToken;
    const headers = { 'Content-Type': 'application/json', Authorization: 'MediaBrowser Token="' + accessToken + '"' };
    const tracks = await (await fetch('/SubSync/Subtitles/' + itemId, { headers })).json();
    const wanted = (Array.isArray(tracks) ? tracks : []).map((x) => x.Index);
    const r = await fetch('/SubSync/Batch', {
      method: 'POST', headers,
      body: JSON.stringify({ Label: 'f29 probe', Tasks: wanted.map((Index) => ({ ItemId: itemId, SubtitleIndex: Index })) }),
    });
    return { status: r.status, tasks: wanted.length, body: (await r.text()).slice(0, 80) };
  }, ITEM);

  let reachedKill = null;
  for (let attempt = 1; attempt <= 3 && !reachedKill; attempt++) {
    const queued = await queue();
    let runningSeen = 0;
    for (let i = 0; i < 40; i++) {
      await wait(1000);
      const active = await (await fetch(BASE + '/SubSync/Active', { headers: { Authorization: auth } })).json();
      runningSeen = (active.running || []).length;
      if (runningSeen > 0) { say('a task is running after ' + i + ' s'); break; }
      if (i % 15 === 0) { say('waiting for a task to start (' + i + ' s, running=' + runningSeen + ')'); }
    }
    say('queued: ' + JSON.stringify(queued));
    say('running when cancelled: ' + runningSeen);
    results.steps.push({ step: 'queued a run (attempt ' + attempt + ')', queued, runningWhenCancelled: runningSeen });
    if (!runningSeen) continue;
    // Cancel drops the queue but not the job that is already running: the button becomes the global kill.
    say('pressing cancel');
    await page.evaluate(() => { const b = document.querySelector('#ss-cancel'); if (b) b.click(); });
    for (let i = 0; i < 25; i++) {
      await wait(800);
      const b = await readButton(page);
      if (b.kill === true) { reachedKill = b; break; }
    }
    results.steps.push({ step: 'after the cancel', button: reachedKill || (await readButton(page)) });
  }

  if (reachedKill) {
    const first = await page.evaluate(() => {
      const b = document.querySelector('#ss-cancel');
      if (b) b.click();
      return null;
    });
    await wait(1200);
    const afterFirst = await readButton(page);
    await page.evaluate(() => { const b = document.querySelector('#ss-cancel'); if (b) b.click(); });
    await wait(4000);
    const afterSecond = await readButton(page);
    const active = await (await fetch(BASE + '/SubSync/Active', { headers: { Authorization: auth } })).json();
    results.steps.push({
      step: 'F29 presses',
      afterFirstPress: afterFirst,
      afterSecondPress: afterSecond,
      serverAfter: { running: (active.running || []).length, queued: active.queued },
      killCalls: calls.filter((c) => /POST \/SubSync\/Kill/.test(c)).length,
    });
  } else {
    results.steps.push({ step: 'F29 presses', skipped: 'the button never became the global kill in three attempts' });
  }
  results.calls = calls;
  fs.writeFileSync(OUT, JSON.stringify(results, null, 2) + '\n');
  console.log(JSON.stringify(results.steps, null, 1).slice(0, 2000));
  console.log('wrote ' + OUT);
  await fetch(BASE + '/Users/' + user.Id, { method: 'DELETE', headers: { Authorization: auth } });
  await browser.close();
})().catch((e) => { console.error('probe failed: ' + e.message); process.exit(1); });
