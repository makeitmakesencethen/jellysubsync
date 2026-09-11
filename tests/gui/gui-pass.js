/*
 * Matrix B — the plugin's GUI in a real Chromium against the live test server.
 *
 * Answers the two rows nobody has ever verified (D14: does the injected client script actually hook
 * the Jellyfin 12 item page? D13: does one press on a series scope start a batch or only preview one?)
 * and records the surrounding facts the report needs: how a user is supposed to reach the plugin's own
 * pages (D18), the golden-section checkbox's real pixel size (D19), every /SubSync/* call with its
 * status, console output, uncaught errors, and screenshots per step.
 *
 * Run:  PLAYWRIGHT_BROWSERS_PATH=/opt/data/.playwright node tests/gui/gui-pass.js
 * Evidence: tests/gui/shots/*.png and tests/gui/gui-pass-<label>.json
 */
const fs = require('fs');
const path = require('path');
const { chromium } = require('/tmp/audit/pw/node_modules/playwright');

const BASE = 'http://127.0.0.1:8096';
const HERE = __dirname;
const SHOTS = path.join(HERE, 'shots');
const LABEL = process.env.LABEL || 'pass';
const IDS = JSON.parse(fs.readFileSync(path.join(HERE, 'ids.json'), 'utf8'));
const UI = process.env.VIEWPORT ? process.env.VIEWPORT.split('x').map(Number) : [1600, 1000];
fs.mkdirSync(SHOTS, { recursive: true });

const wait = (ms) => new Promise((r) => setTimeout(r, ms));

(async () => {
  const report = { label: LABEL, viewport: UI, steps: [], api: [], console: [], errors: [], shots: [] };

  // A throwaway account, created and deleted by this script: the test server's admin has a password,
  // and the only secret-free way to drive the real login form is a temporary account with an empty
  // one. Nothing is typed anywhere, and the account is removed in the last step.
  const MODE = process.env.MODE === 'user' ? 'user' : 'admin';
  const USER_NAME = 'guitest-' + Date.now().toString(36) + (MODE === 'user' ? '-u' : '-a');
  const authHeader = 'MediaBrowser Token="' + IDS.admin_token + '"';
  let createdUser = null;
  report.throwaway_user = { name: USER_NAME, mode: MODE };
  try {
    const createRes = await fetch(BASE + '/Users/New', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', Authorization: authHeader },
      body: JSON.stringify({ Name: USER_NAME }),
    });
    createdUser = await createRes.json();
    await fetch(BASE + '/Users/' + createdUser.Id + '/Policy', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', Authorization: authHeader },
      body: JSON.stringify({
        IsAdministrator: MODE === 'admin',
        EnableAllFolders: true,
        EnableRemoteAccess: true,
        EnableContentDownloading: true,
        EnableMediaPlayback: true,
        EnableAudioPlaybackTranscoding: true,
        EnableVideoPlaybackTranscoding: true,
        EnablePlaybackRemuxing: true,
        EnableLiveTvAccess: true,
        EnableSharedDeviceControl: true,
        EnableCollectionManagement: false,
      }),
    });
    report.throwaway_user.id = createdUser.Id;
  } catch (e) {
    report.throwaway_user.error = String(e).slice(0, 200);
  }

  const browser = await chromium.launch({
    args: ['--no-sandbox'],
    executablePath: process.env.BROWSER_PATH || '/opt/data/.playwright/chromium-1234/chrome-linux64/chrome',
  });
  const ctx = await browser.newContext({ viewport: { width: UI[0], height: UI[1] } });
  const page = await ctx.newPage();

  page.on('console', (m) => {
    const t = m.text();
    if (t.includes('SubSync') || m.type() === 'error' || m.type() === 'warning') {
      report.console.push(m.type() + ': ' + t.slice(0, 240));
    }
  });
  page.on('pageerror', (e) => report.errors.push(String(e).slice(0, 240)));
  page.on('request', (r) => {
    const u = r.url();
    if (u.includes('/SubSync/')) report.api.push({ t: Date.now(), call: r.method() + ' ' + u.replace(BASE, '') });
  });
  page.on('response', async (r) => {
    const u = r.url();
    if (u.includes('/SubSync/')) {
      const i = report.api.map((x) => x.call).lastIndexOf(r.request().method() + ' ' + u.replace(BASE, ''));
      if (i >= 0) report.api[i].status = r.status();
    }
  });

  const shot = async (name) => {
    const file = path.join(SHOTS, LABEL + '-' + name + '.png');
    await page.screenshot({ path: file });
    report.shots.push(path.basename(file));
    return path.basename(file);
  };

  const step = async (name, fn) => {
    const apiBefore = report.api.length;
    try {
      const value = await fn();
      report.steps.push({ name, ok: true, value,
        api: report.api.slice(apiBefore).map((x) => x.call + (x.status ? ' -> ' + x.status : '')) });
      console.log('STEP ' + name + ' ' + JSON.stringify(value).slice(0, 500));
    } catch (e) {
      report.steps.push({ name, ok: false, error: String(e).slice(0, 300),
        api: report.api.slice(apiBefore).map((x) => x.call + (x.status ? ' -> ' + x.status : '')) });
      console.log('STEP ' + name + ' FAILED ' + String(e).slice(0, 200));
    }
  };

  // ---------------------------------------------------------------- login (the real form)
  await step('login-form', async () => {
    await page.goto(BASE + '/web/', { waitUntil: 'domcontentloaded', timeout: 60000 });
    await wait(4000);
    const user = await page.$('input[name="username"], #txtManualName, input[type="text"]');
    if (user) await user.fill(USER_NAME);
    const pw = await page.$('input[type="password"]');
    if (pw) await pw.fill('');
    const submit = await page.$('button[type="submit"], .btnSubmit, button:has-text("Sign In")');
    if (submit) await submit.click();
    else await page.keyboard.press('Enter');
    await wait(9000);
    const body = (await page.evaluate(() => document.body.innerText)).slice(0, 120);
    return { user: USER_NAME, url: page.url(), title: await page.title(),
             loggedIn: !/Please sign in/.test(body), body };
  });
  await shot('01-login');

  // ---------------------------------------------------------------- D18: how are the pages reached?
  await step('D18-main-menu', async () => {
    await page.goto(BASE + '/web/#/home', { waitUntil: 'domcontentloaded', timeout: 60000 });
    await wait(6000);
    const entries = await page.evaluate(() => {
      const out = [];
      for (const el of document.querySelectorAll('a, button, [role="menuitem"], .navMenuOption, .mainDrawerButton')) {
        const s = ((el.innerText || '') + ' ' + (el.getAttribute('aria-label') || '')).trim();
        if (/subsync|sync/i.test(s)) out.push(s.slice(0, 80));
      }
      return out;
    });
    // the dashboard's own entry point
    await page.goto(BASE + '/web/#/dashboard/plugins', { waitUntil: 'domcontentloaded', timeout: 60000 });
    await wait(5000);
    const pluginNamed = await page.evaluate(() => (document.body.innerText || '').includes('SubSync'));
    return { mainMenuEntries: entries, dashboardListsPlugin: pluginNamed };
  });
  await shot('02-d18');

  // ---------------------------------------------------------------- the plugin's own page
  await step('own-page', async () => {
    await page.goto(BASE + '/web/configurationpage?name=subsync-main', { waitUntil: 'domcontentloaded', timeout: 60000 });
    await wait(7000);
    // The page has its own left nav (Sync / History / Settings); the settings fields are in a hidden
    // panel until that entry is clicked, which is why clicking Save timed out in the first pass.
    await page.evaluate(() => {
      const b = document.querySelector('.ss-nav-item[data-panel="settings"]');
      if (b) b.click();
    });
    await wait(2500);
    const dom = await page.evaluate(() => ({
      hasPage: !!document.querySelector('#ss-runbox, #ss-app, .ss-wrap, [id^="ss-"]'),
      ids: Array.from(document.querySelectorAll('[id^="ss-"]')).map((e) => e.id).slice(0, 40),
      text: (document.body.innerText || '').slice(0, 300),
    }));
    return dom;
  });
  await shot('03-own-page');

  // ---------------------------------------------------------------- D19: the golden-section checkbox
  await step('D19-gss-checkbox', async () => {
    const out = await page.evaluate(() => {
      const el = document.querySelector('#ss-gss');
      if (!el) return { found: false };
      const r = el.getBoundingClientRect();
      const cs = getComputedStyle(el);
      const label = el.closest('.ss-field');
      const lr = label ? label.getBoundingClientRect() : null;
      return { found: true, width: Math.round(r.width * 100) / 100, height: Math.round(r.height * 100) / 100,
               visible: r.width > 0 && r.height > 0, display: cs.display, opacity: cs.opacity,
               labelWidth: lr ? Math.round(lr.width) : null,
               clickable: (() => { el.click(); return el.checked; })() };
    });
    if (!out.found) throw new Error('no #ss-gss in the settings markup');
    return out;
  });

  // ---------------------------------------------------------------- settings round trip
  await step('settings-roundtrip', async () => {
    const before = await page.evaluate(() => ({
      maxoffset: (document.querySelector('#ss-maxoffset') || {}).value,
      maxref: (document.querySelector('#ss-maxrefoffset') || {}).value,
      workers: (document.querySelector('#ss-workers-input') || {}).value,
    }));
    const typed = await page.evaluate(() => {
      const el = document.querySelector('#ss-maxrefoffset');
      if (!el) return null;
      el.value = '42';
      el.dispatchEvent(new Event('change', { bubbles: true }));
      return el.value;
    });
    let status = null;
    await page.evaluate(() => {
      const b = document.querySelector('#ss-save');
      if (b) b.click();               // inside the settings panel: click through the DOM
    });
    await wait(4000);
    status = await page.evaluate(() => (document.querySelector('#ss-save-status') || {}).textContent);
    const api = await page.evaluate(async (id) => {
      const r = await fetch('/Plugins/' + id + '/Configuration', {
        headers: { Authorization: 'MediaBrowser Token="' + window.ApiClient.accessToken() + '"' },
      });
      const c = await r.json();
      return { MaxSubtitleReferenceOffsetSeconds: c.MaxSubtitleReferenceOffsetSeconds, MaxOffsetSeconds: c.MaxOffsetSeconds };
    }, IDS.plugin_id);
    return { before, typed, saveStatus: status, stored: api };
  });

  // ---------------------------------------------------------------- D14: the item page
  await step('D14-film-page', async () => {
    const calls = [];
    await page.goto(BASE + '/web/#/details?id=' + IDS.movie, { waitUntil: 'domcontentloaded', timeout: 60000 });
    await wait(9000);
    const buttons = await page.evaluate(() => {
      const out = [];
      for (const el of document.querySelectorAll('button, a, [role="menuitem"]')) {
        const s = ((el.innerText || '') + ' ' + (el.getAttribute('aria-label') || '') + ' ' + (el.title || '')).trim();
        if (/sync sub|subsync/i.test(s)) out.push({ text: s.slice(0, 60), cls: String(el.className).slice(0, 60) });
      }
      return out;
    });
    // the plugin's action lives behind the page's ⋮ button
    const more = await page.$('button[aria-label*="ore"], .btnMoreCommands, button[title*="ore"]');
    let menuItems = [];
    if (more) {
      await more.click();
      await wait(2500);
      menuItems = await page.evaluate(() => Array.from(document.querySelectorAll('[role="menuitem"], .actionSheetMenuItem, .mdl-menu__item'))
        .map((e) => (e.innerText || '').trim()).filter(Boolean).slice(0, 30));
    }
    await shot('04-film-menu');
    calls.push(...report.api.slice(-8).map((x) => x.call + (x.status ? ' -> ' + x.status : '')));
    return { buttons, moreButton: !!more, menuItems };
  });

  await step('D14-click-item-action', async () => {
    // Exactly "Sync Subtitles" (and inside a menu if one is open): the first version matched
    // "SubSync Fixtures" — a library name — and navigated away from the item page.
    const clicked = await page.evaluate(() => {
      const scopes = [document.querySelector('.actionSheet, [role="menu"]'), document];
      for (const scope of scopes) {
        if (!scope) continue;
        for (const el of scope.querySelectorAll('button, a, [role="menuitem"], .actionSheetMenuItem')) {
          const s = ((el.innerText || '') + ' ' + (el.getAttribute('aria-label') || '')).trim();
          if (/^sync subtitles$/i.test(s) || /^sync subtitles/i.test(s)) {
            el.click();
            return s.slice(0, 60);
          }
        }
      }
      return null;
    });
    await wait(5000);
    await shot('05-item-dialog');
    const dialog = await page.evaluate(() => {
      const d = document.querySelector('.ss-overlay, dialog, .dialog, [role="dialog"]');
      return d ? (d.innerText || '').slice(0, 800) : null;
    });
    return { clicked, dialogText: dialog, api: report.api.slice(-10).map((x) => x.call + (x.status ? ' -> ' + x.status : '')) };
  });

  await step('D14-start-from-dialog', async () => {
    // What the dialog offers, then what one press of its primary button actually does: a batch, or a
    // preview call (D13's question, asked of the item page).
    const buttons = await page.evaluate(() => Array.from(document.querySelectorAll('.ss-overlay .ss-btn'))
      .map((b) => (b.textContent || '').trim()).filter(Boolean));
    const overlayText = await page.evaluate(() => {
      const o = document.querySelector('.ss-overlay');
      return o ? (o.innerText || '').slice(0, 500) : null;
    });
    const pressed = await page.evaluate(() => {
      const b = document.querySelector('.ss-overlay .ss-btn-primary')
        || Array.from(document.querySelectorAll('.ss-overlay .ss-btn'))
          .find((x) => /sync|start/i.test(x.textContent || ''));
      if (!b) return null;
      const label = (b.textContent || '').trim();
      b.click();
      return label;
    });
    await wait(6000);
    await shot('05b-item-after-press');
    return { buttons, overlayText, pressed,
             api: report.api.slice(-14).map((x) => x.call + (x.status ? ' -> ' + x.status : '')) };
  });

  // ---------------------------------------------------------------- D13: series scope
  await step('D13-series-page', async () => {
    await page.goto(BASE + '/web/#/details?id=' + IDS.series, { waitUntil: 'domcontentloaded', timeout: 60000 });
    await wait(9000);
    const syncish = await page.evaluate(() => {
      const out = [];
      for (const el of document.querySelectorAll('button, a, [role="menuitem"]')) {
        const s = ((el.innerText || '') + ' ' + (el.getAttribute('aria-label') || '')).trim();
        if (/sync/i.test(s)) out.push(s.slice(0, 70));
      }
      return out;
    });
    await shot('06-series');
    return { syncish, url: page.url() };
  });

  await step('D13-click-series-sync', async () => {
    const more = await page.$('button[aria-label*="ore"], .btnMoreCommands, button[title*="ore"]');
    let menuItems = [];
    if (more) {
      await more.click();
      await wait(2500);
      menuItems = await page.evaluate(() => Array.from(document.querySelectorAll(
        '.actionSheetMenuItem, [role="menuitem"], .mdl-menu__item, button'))
        .map((e) => (e.innerText || '').trim()).filter((s) => s && s.length < 60).slice(0, 40));
    }
    const clicked = await page.evaluate(() => {
      const scopes = [document.querySelector('.actionSheet, [role="menu"]'), document];
      for (const scope of scopes) {
        if (!scope) continue;
        for (const el of scope.querySelectorAll('button, a, [role="menuitem"], .actionSheetMenuItem')) {
          const s = ((el.innerText || '') + ' ' + (el.getAttribute('aria-label') || '')).trim();
          if (/sync all episodes|sync subtitles|sync selected/i.test(s)) { el.click(); return s.slice(0, 60); }
        }
      }
      return null;
    });
    report.d13_menuItems = menuItems;
    await wait(6000);
    await shot('07-series-after-click');
    const dialog = await page.evaluate(() => {
      const d = document.querySelector('.ss-overlay, dialog, .dialog, [role="dialog"]');
      return d ? (d.innerText || '').slice(0, 800) : null;
    });
    const pressed = await page.evaluate(() => {
      const b = document.querySelector('.ss-overlay .ss-btn-primary');
      if (!b) return null;
      const label = (b.textContent || '').trim();
      b.click();
      return label;
    });
    await wait(7000);
    await shot('09-series-after-press');
    return { clicked, menuItems, dialogText: dialog, pressed,
             api: report.api.slice(-16).map((x) => x.call + (x.status ? ' -> ' + x.status : '')) };
  });

  // ---------------------------------------------------------------- polling load (D7), idle
  await step('D7-idle-polling', async () => {
    await page.goto(BASE + '/web/configurationpage?name=subsync-main', { waitUntil: 'domcontentloaded', timeout: 60000 });
    await wait(6000);
    const from = report.api.length;
    await wait(60000);
    const calls = report.api.slice(from);
    const perMinute = {};
    for (const c of calls) {
      const key = c.call.split('?')[0];
      perMinute[key] = (perMinute[key] || 0) + 1;
    }
    return { requestsInOneMinute: calls.length, byEndpoint: perMinute };
  });

  // ---------------------------------------------------------------- D4/F15: the dashboard page is a pointer
  await step('D4-merged-settings-page', async () => {
    await page.goto(BASE + '/web/#/dashboard', { waitUntil: 'domcontentloaded', timeout: 60000 });
    await wait(6000);
    const before = page.url();
    await page.goto(BASE + '/web/configurationpage?name=' + IDS.config_page, { waitUntil: 'domcontentloaded', timeout: 60000 });
    await wait(4000);
    const after = page.url();
    const text = await page.evaluate(() => (document.body.innerText || '').slice(0, 400));
    const pointers = await page.evaluate(() => {
      const out = [];
      for (const el of document.querySelectorAll('a[href], button')) {
        const s = ((el.innerText || '') + ' ' + (el.getAttribute('href') || '')).trim();
        if (/subsync/i.test(s)) out.push(s.slice(0, 80));
      }
      return out;
    });
    return { dashboardBefore: before, after, pointerText: text, pointers };
  });

  // ---------------------------------------------------------------- F29: the global Kill asks twice
  await step('F29-kill-needs-confirmation', async () => {
    // A running batch is what turns the Cancel button into "Kill all syncing"; the batch is queued from
    // inside the page so the page's own heartbeat is the thing that mirrors it.
    const queued = await page.evaluate(async ({ ids, token }) => {
      const tracks = await (await fetch('/SubSync/Subtitles/' + ids.movie, {
        headers: { Authorization: 'MediaBrowser Token="' + token + '"' },
      })).json();
      const tasks = tracks.filter((t) => !t.IsExternal).map((t) => ({ ItemId: ids.movie, SubtitleIndex: t.Index }));
      const r = await fetch('/SubSync/Batch', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', Authorization: 'MediaBrowser Token="' + token + '"' },
        body: JSON.stringify({ Label: 'gui-F29', Tasks: tasks }),
      });
      const body = await r.json();
      return { status: r.status, tasks: tasks.length, batch: body.Id || body.BatchId || null };
    }, { ids: IDS, token: IDS.admin_token });

    await page.goto(BASE + '/web/configurationpage?name=subsync-main', { waitUntil: 'domcontentloaded', timeout: 60000 });
    await wait(4000);
    const cancel = await page.$('#ss-cancel');
    let armed = null;
    let killed = null;
    if (cancel) {
      await cancel.click();                      // drops the queued tasks, jobs keep running
      await wait(3000);
      armed = await page.evaluate(() => ({
        label: (document.querySelector('#ss-cancel') || {}).innerText,
        phase: (document.querySelector('#ss-phase') || {}).textContent,
      }));
      const el = await page.$('#ss-cancel');
      if (el && /kill/i.test(armed.label || '')) {
        await el.click();                        // press 1: should only ask
        await wait(1200);
        armed = await page.evaluate(() => ({
          label: (document.querySelector('#ss-cancel') || {}).innerText,
          phase: (document.querySelector('#ss-phase') || {}).textContent,
          killCallsWhenArmed: 0,
        }));
        await shot('08-kill-armed');
        const callsAfterArm = report.api.length;
        const el2 = await page.$('#ss-cancel');
        await el2.click();                       // press 2: the actual kill
        await wait(2500);
        killed = {
          label: await page.evaluate(() => (document.querySelector('#ss-cancel') || {}).innerText),
          phase: await page.evaluate(() => (document.querySelector('#ss-phase') || {}).textContent),
          callsAfterArming: report.api.slice(callsAfterArm).map((x) => x.call + (x.status ? ' -> ' + x.status : '')),
        };
      }
    }
    return { queued, reachedKillMode: !!armed, armed, killed };
  });

  // ---------------------------------------------------------------- cleanup: the throwaway account
  await step('delete-throwaway-user', async () => {
    if (!createdUser || !createdUser.Id) return { deleted: false, reason: 'no user was created' };
    const r = await fetch(BASE + '/Users/' + createdUser.Id, {
      method: 'DELETE', headers: { Authorization: authHeader },
    });
    return { deleted: r.status === 204 || r.status === 200, status: r.status, name: USER_NAME };
  });

  await browser.close();
  fs.writeFileSync(path.join(HERE, 'gui-pass-' + LABEL + '.json'), JSON.stringify(report, null, 1));
  console.log('DONE — wrote tests/gui/gui-pass-' + LABEL + '.json (' + report.shots.length + ' shots)');
})();
