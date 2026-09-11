/*
 * F29 — the global Kill in a real browser: is it reachable while a run is mirrored, and does the first
 * press ask before it stops every run on the server?
 *
 * The earlier passes could not answer it: one ran unauthenticated, and the next queued its batch before
 * loading the page, so the run was over before the page could mirror it. This one loads the page first,
 * clears the extracted-subtitle cache so the batch really takes a while, queues, waits for the button,
 * then presses it three times and records what each press did (label, phase line, and the API calls).
 */
const fs = require('fs');
const path = require('path');
const { chromium } = require('/tmp/audit/pw/node_modules/playwright');

const BASE = 'http://127.0.0.1:8096';
const HERE = __dirname;
const LABEL = process.env.LABEL || 'probe';
const IDS = JSON.parse(fs.readFileSync(path.join(HERE, 'ids.json'), 'utf8'));
const wait = (ms) => new Promise((r) => setTimeout(r, ms));

(async () => {
  const out = { label: LABEL, api: [], presses: [] };
  const authHeader = 'MediaBrowser Token="' + IDS.admin_token + '"';
  const USER_NAME = 'guitest-f29-' + Date.now().toString(36);
  const created = await (await fetch(BASE + '/Users/New', {
    method: 'POST', headers: { 'Content-Type': 'application/json', Authorization: authHeader },
    body: JSON.stringify({ Name: USER_NAME }),
  })).json();
  await fetch(BASE + '/Users/' + created.Id + '/Policy', {
    method: 'POST', headers: { 'Content-Type': 'application/json', Authorization: authHeader },
    body: JSON.stringify({ IsAdministrator: true, EnableAllFolders: true, EnableRemoteAccess: true }),
  });

  const browser = await chromium.launch({
    args: ['--no-sandbox'],
    executablePath: process.env.BROWSER_PATH || '/opt/data/.playwright/chromium-1234/chrome-linux64/chrome',
  });
  const page = await (await browser.newContext({ viewport: { width: 1600, height: 1000 } })).newPage();
  page.on('request', (r) => {
    if (r.url().includes('/SubSync/')) out.api.push({ call: r.method() + ' ' + r.url().replace(BASE, ''), t: Date.now() });
  });
  page.on('response', (r) => {
    if (r.url().includes('/SubSync/')) {
      const i = out.api.map((x) => x.call).lastIndexOf(r.request().method() + ' ' + r.url().replace(BASE, ''));
      if (i >= 0) out.api[i].status = r.status();
    }
  });

  await page.goto(BASE + '/web/', { waitUntil: 'domcontentloaded', timeout: 60000 });
  await wait(4000);
  await page.fill('input[name="username"], #txtManualName, input[type="text"]', USER_NAME);
  const submit = await page.$('button[type="submit"], .btnSubmit, button:has-text("Sign In")');
  if (submit) await submit.click();
  await wait(8000);

  // the page first, so it is ready and mirroring before anything runs
  await page.goto(BASE + '/web/configurationpage?name=subsync-main', { waitUntil: 'domcontentloaded', timeout: 60000 });
  await wait(6000);

  // WARM=1 measures the case where the jobs go Running immediately; the default clears the cache so the
  // run is long enough for the page to mirror it.
  if (process.env.WARM !== '1') {
    const cleared = await fetch(BASE + '/SubSync/SpeechCache/Clear', { method: 'POST', headers: { Authorization: authHeader } });
    out.cacheClearedStatus = cleared.status;
  } else {
    out.cacheClearedStatus = 'kept (WARM=1)';
  }

  const queued = await page.evaluate(async ({ ids, token }) => {
    const tracks = await (await fetch('/SubSync/Subtitles/' + ids.movie, {
      headers: { Authorization: 'MediaBrowser Token="' + token + '"' } })).json();
    const tasks = tracks.filter((t) => !t.IsExternal).map((t) => ({ ItemId: ids.movie, SubtitleIndex: t.Index }));
    const r = await fetch('/SubSync/Batch', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', Authorization: 'MediaBrowser Token="' + token + '"' },
      body: JSON.stringify({ Label: 'f29-probe', Tasks: tasks }) });
    const b = await r.json();
    return { status: r.status, tasks: tasks.length, batch: b.Id || b.BatchId || null };
  }, { ids: IDS, token: IDS.admin_token });
  out.queued = queued;

  const state = () => page.evaluate(() => {
    const el = document.querySelector('#ss-cancel');
    const r = el ? el.getBoundingClientRect() : null;
    const box = document.querySelector('#ss-runbox');
    const boxStyle = box ? getComputedStyle(box) : null;
    return {
      present: !!el,
      visible: !!r && r.width > 0 && r.height > 0,
      label: el ? (el.innerText || '').trim() : null,
      phase: (document.querySelector('#ss-phase') || {}).textContent,
      runLabel: (document.querySelector('#ss-run-label') || {}).textContent,
      runBox: box ? { hiddenClass: box.classList.contains('ss-hidden'), display: boxStyle.display,
                      visibility: boxStyle.visibility, height: Math.round(box.getBoundingClientRect().height) } : null,
      bodyTail: (document.body.innerText || '').slice(-300).replace(/\n/g, ' | '),
    };
  });

  let first = null;
  const observed = [];
  const deadline = Date.now() + 120000;
  while (Date.now() < deadline) {
    const s = await state();
    observed.push({ t: Date.now(), visible: s.visible, label: s.label, runBox: s.runBox });
    if (s.present && s.visible) { first = s; break; }
    await wait(700);
  }
  out.observations = observed.length;
  out.firstObservation = observed[0];
  out.lastObservation = observed[observed.length - 1];
  out.whileRunning = first;
  await page.screenshot({ path: path.join(HERE, 'shots', LABEL + '-f29-running.png') });

  if (first) {
    const pressOnce = async (note) => {
      const before = out.api.length;
      await page.evaluate(() => document.querySelector('#ss-cancel').click());
      await wait(4000);
      const s = await state();
      const calls = out.api.slice(before).map((x) => x.call + (x.status ? ' -> ' + x.status : ''));
      out.presses.push({ note, state: s, api: calls });
      console.log('PRESS ' + note + ' -> ' + JSON.stringify({ label: s.label, phase: s.phase, api: calls }) );
      return s;
    };

    await pressOnce('cancel the queued tasks');
    await page.screenshot({ path: path.join(HERE, 'shots', LABEL + '-f29-after-cancel.png') });
    await pressOnce('arm the kill');
    await page.screenshot({ path: path.join(HERE, 'shots', LABEL + '-f29-armed.png') });
    await pressOnce('confirm the kill');
    await page.screenshot({ path: path.join(HERE, 'shots', LABEL + '-f29-confirmed.png') });
  }

  await fetch(BASE + '/Users/' + created.Id, { method: 'DELETE', headers: { Authorization: authHeader } });
  fs.writeFileSync(path.join(HERE, 'f29-' + LABEL + '.json'), JSON.stringify(out, null, 1));
  console.log('DONE — wrote tests/gui/f29-' + LABEL + '.json');
  await browser.close();
})();
