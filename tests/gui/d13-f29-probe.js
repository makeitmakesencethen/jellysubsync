/*
 * The two GUI questions the first pass could not finish:
 *
 *  D13 — on a series page, is there a bulk entry, and does one press create a batch or only preview
 *        one? (The first pass could not open the page's ⋮ menu, so it clicked nothing.)
 *  F29 — the global Kill: does the button ask before it stops every run on the server, and is it
 *        reachable at all while a run is being mirrored?
 *
 * Same throwaway-account pattern as the main pass: one temporary admin with no password, deleted at
 * the end. Evidence lands in tests/gui/d13-f29-<label>.json plus screenshots.
 */
const fs = require('fs');
const path = require('path');
const { chromium } = require('/tmp/audit/pw/node_modules/playwright');

const BASE = 'http://127.0.0.1:8096';
const HERE = __dirname;
const SHOTS = path.join(HERE, 'shots');
const LABEL = process.env.LABEL || 'probe';
const IDS = JSON.parse(fs.readFileSync(path.join(HERE, 'ids.json'), 'utf8'));
const wait = (ms) => new Promise((r) => setTimeout(r, ms));

(async () => {
  const out = { label: LABEL, series: {}, kill: {}, api: [] };
  const authHeader = 'MediaBrowser Token="' + IDS.admin_token + '"';
  const USER_NAME = 'guitest-probe-' + Date.now().toString(36);
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
    const u = r.url();
    if (u.includes('/SubSync/')) out.api.push({ call: r.method() + ' ' + u.replace(BASE, '') });
  });
  page.on('response', (r) => {
    const u = r.url();
    if (u.includes('/SubSync/')) {
      const i = out.api.map((x) => x.call).lastIndexOf(r.request().method() + ' ' + u.replace(BASE, ''));
      if (i >= 0) out.api[i].status = r.status();
    }
  });
  page.on('pageerror', (e) => { out.pageError = String(e).slice(0, 200); });

  await page.goto(BASE + '/web/', { waitUntil: 'domcontentloaded', timeout: 60000 });
  await wait(4000);
  await page.fill('input[name="username"], #txtManualName, input[type="text"]', USER_NAME);
  const submit = await page.$('button[type="submit"], .btnSubmit, button:has-text("Sign In")');
  if (submit) await submit.click();
  await wait(8000);
  out.loggedIn = !/Please sign in/.test(await page.evaluate(() => document.body.innerText));

  // ---------------------------------------------------------------- D13: the series page
  await page.goto(BASE + '/web/#/details?id=' + IDS.series, { waitUntil: 'domcontentloaded', timeout: 60000 });
  await wait(9000);
  out.series.controls = await page.evaluate(() => {
    const rows = [];
    for (const el of document.querySelectorAll('button, a, [role="menuitem"]')) {
      const text = (el.innerText || el.getAttribute('aria-label') || '').trim();
      if (!text || text.length > 60) continue;
      const r = el.getBoundingClientRect();
      rows.push({ text, cls: String(el.className).slice(0, 40), visible: r.width > 0 && r.height > 0 });
    }
    const counts = {};
    for (const r of rows) counts[r.text] = (counts[r.text] || 0) + 1;
    return { total: rows.length, byText: counts, syncish: rows.filter((r) => /sync/i.test(r.text)).slice(0, 8) };
  });
  await page.screenshot({ path: path.join(SHOTS, LABEL + '-d13-series.png') });

  // every ⋮-ish button, clicked until a menu appears
  out.series.menuAttempts = [];
  for (const selector of ['button[aria-label*="ore"]', '.btnMoreCommands', 'button[title*="ore"]',
                          '.detailButton-more', '.btnMore', 'button[aria-label*="More"]']) {
    const handles = await page.$$(selector);
    if (!handles.length) continue;
    for (const h of handles.slice(0, 3)) {
      const before = await page.evaluate(() => document.querySelectorAll('[role="menuitem"], .actionSheetMenuItem').length);
      await page.evaluate((sel) => {
        const el = document.querySelector(sel);
        if (el) el.click();
      }, selector);
      await wait(2000);
      const after = await page.evaluate(() => Array.from(document.querySelectorAll('[role="menuitem"], .actionSheetMenuItem'))
        .map((e) => (e.innerText || '').trim()).filter(Boolean).slice(0, 30));
      out.series.menuAttempts.push({ selector, before, items: after });
      if (after.length > before) break;
    }
    if (out.series.menuAttempts.some((a) => a.items.length)) break;
  }
  await page.screenshot({ path: path.join(SHOTS, LABEL + '-d13-menu.png') });

  const bulkClicked = await page.evaluate(() => {
    for (const el of document.querySelectorAll('[role="menuitem"], .actionSheetMenuItem, button, a')) {
      const t = (el.innerText || '').trim();
      if (/^sync all episodes/i.test(t)) { el.click(); return t.slice(0, 60); }
    }
    return null;
  });
  out.series.bulkClicked = bulkClicked;
  await wait(6000);
  out.series.bulkDialogText = await page.evaluate(() => {
    const o = document.querySelector('.ss-overlay');
    return o ? (o.innerText || '').slice(0, 400) : null;
  });
  await page.screenshot({ path: path.join(SHOTS, LABEL + '-d13-dialog.png') });
  out.series.apiAfterBulkOpen = out.api.slice(-6).map((x) => x.call + (x.status ? ' -> ' + x.status : ''));

  if (out.series.bulkDialogText) {
    out.series.pressed = await page.evaluate(() => {
      const b = document.querySelector('.ss-overlay .ss-btn-primary')
        || Array.from(document.querySelectorAll('.ss-overlay .ss-btn')).find((x) => /sync|start/i.test(x.textContent || ''));
      if (!b) return null;
      const t = (b.textContent || '').trim();
      b.click();
      return t;
    });
    await wait(8000);
    await page.screenshot({ path: path.join(SHOTS, LABEL + '-d13-after-press.png') });
  }
  out.series.apiAfterBulkPress = out.api.slice(-10).map((x) => x.call + (x.status ? ' -> ' + x.status : ''));
  out.series.dialogAfterPress = await page.evaluate(() => {
    const o = document.querySelector('.ss-overlay');
    return o ? (o.innerText || '').slice(0, 300) : null;
  });

  // ---------------------------------------------------------------- F29: the global Kill
  const queued = await page.evaluate(async ({ ids, token }) => {
    const tracks = await (await fetch('/SubSync/Subtitles/' + ids.movie, {
      headers: { Authorization: 'MediaBrowser Token="' + token + '"' } })).json();
    const tasks = tracks.filter((t) => !t.IsExternal).map((t) => ({ ItemId: ids.movie, SubtitleIndex: t.Index }));
    const r = await fetch('/SubSync/Batch', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', Authorization: 'MediaBrowser Token="' + token + '"' },
      body: JSON.stringify({ Label: 'probe-F29', Tasks: tasks }) });
    const b = await r.json();
    return { status: r.status, tasks: tasks.length, batch: b.Id || b.BatchId || null };
  }, { ids: IDS, token: IDS.admin_token });
  out.kill.queued = queued;

  await page.goto(BASE + '/web/configurationpage?name=subsync-main', { waitUntil: 'domcontentloaded', timeout: 60000 });
  await wait(5000);

  // The button only exists while a run is mirrored; wait for it rather than assuming.
  let visible = false;
  let label = null;
  const deadline = Date.now() + 60000;
  while (Date.now() < deadline) {
    const state = await page.evaluate(() => {
      const el = document.querySelector('#ss-cancel');
      if (!el) return { found: false };
      const r = el.getBoundingClientRect();
      return { found: true, visible: r.width > 0 && r.height > 0, label: (el.innerText || '').trim(),
               phase: (document.querySelector('#ss-phase') || {}).textContent };
    });
    if (state.found && state.visible) { visible = true; label = state.label; out.kill.whileRunning = state; break; }
    await wait(1500);
  }
  out.kill.buttonVisibleWhileRunning = visible;
  await page.screenshot({ path: path.join(SHOTS, LABEL + '-f29-running.png') });

  if (visible) {
    // press 1: cancel (drops queued tasks) — this is what turns the button into Kill
    await page.evaluate(() => document.querySelector('#ss-cancel').click());
    await wait(4000);
    out.kill.afterFirstPress = await page.evaluate(() => ({
      label: (document.querySelector('#ss-cancel') || {}).innerText,
      phase: (document.querySelector('#ss-phase') || {}).textContent }));

    if (/kill/i.test(out.kill.afterFirstPress.label || '')) {
      const apiBeforeArm = out.api.length;
      await page.evaluate(() => document.querySelector('#ss-cancel').click());   // arms
      await wait(1500);
      out.kill.armed = await page.evaluate(() => ({
        label: (document.querySelector('#ss-cancel') || {}).innerText,
        phase: (document.querySelector('#ss-phase') || {}).textContent }));
      out.kill.apiAfterArming = out.api.slice(apiBeforeArm).map((x) => x.call + (x.status ? ' -> ' + x.status : ''));
      await page.screenshot({ path: path.join(SHOTS, LABEL + '-f29-armed.png') });

      await page.evaluate(() => document.querySelector('#ss-cancel').click());   // confirms
      await wait(4000);
      out.kill.apiAfterConfirm = out.api.slice(apiBeforeArm).map((x) => x.call + (x.status ? ' -> ' + x.status : ''));
      out.kill.afterConfirm = await page.evaluate(() => ({
        label: (document.querySelector('#ss-cancel') || {}).innerText,
        phase: (document.querySelector('#ss-phase') || {}).textContent }));
      await page.screenshot({ path: path.join(SHOTS, LABEL + '-f29-confirmed.png') });
    }
  }

  await fetch(BASE + '/Users/' + created.Id, { method: 'DELETE', headers: { Authorization: authHeader } });
  fs.writeFileSync(path.join(HERE, 'd13-f29-' + LABEL + '.json'), JSON.stringify(out, null, 1));
  console.log(JSON.stringify(out, null, 1));
  await browser.close();
})();
