/*
 * D7 / D18 — measured in a real browser against the rig.
 *
 * D7: how much does an idle settings page ask of the server? The claim was ~1.4 requests per second with nothing
 * running, including the same GET several times at load. This loads the page as a signed-in admin, lets it settle,
 * then counts every `/SubSync/` request for 20 seconds of an idle page and reports the rate. The idle poll is
 * meant to be one tick per 10 s (two GETs), so a rate above ~0.45/s means the old flat 2 s poll is back.
 *
 * D18: does Jellyfin 12 put this plugin's page in the main menu? The client picks a plugin's *representative page*
 * with EnableInMainMenu and does not build sidebar entries from plugin pages, so the expected answer is no entry -
 * but the two routes that do work (the dashboard's plugin page, and the page URL itself) are exercised here too,
 * so the record says what a user can actually reach rather than what the flag implies.
 */
const fs = require('fs');
const path = require('path');
const { chromium } = require('/tmp/audit/pw/node_modules/playwright');

const BASE = 'http://127.0.0.1:8096';
const HERE = __dirname;
const LABEL = process.env.LABEL || 'd7-d18';
const IDS = JSON.parse(fs.readFileSync(path.join(HERE, 'ids.json'), 'utf8'));
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const WINDOW_MS = Number(process.env.WINDOW_MS || 20000);

(async () => {
  const out = { label: LABEL, windowMs: WINDOW_MS, idle: {}, load: [], menuEntries: [], routes: {} };
  const authHeader = 'MediaBrowser Token="' + IDS.admin_token + '"';
  const USER_NAME = 'guitest-d7-' + Date.now().toString(36);
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
  const calls = [];
  page.on('request', (r) => {
    if (r.url().includes('/SubSync/')) calls.push({ call: r.method() + ' ' + r.url().replace(BASE, ''), t: Date.now() });
  });

  await page.goto(BASE + '/web/', { waitUntil: 'domcontentloaded', timeout: 60000 });
  await wait(4000);
  await page.fill('input[name="username"], #txtManualName, input[type="text"]', USER_NAME);
  const submit = await page.$('button[type="submit"], .btnSubmit, button:has-text("Sign In")');
  if (submit) await submit.click();
  await wait(8000);

  // ---- D7: the page's own load, then an idle window ---------------------------------------------------------
  calls.length = 0;
  await page.goto(BASE + '/web/configurationpage?name=subsync-main', { waitUntil: 'domcontentloaded', timeout: 60000 });
  await wait(9000);           // the page has initialised and its first poll has answered
  out.load = calls.slice();   // everything from navigation to a settled page

  calls.length = 0;
  const started = Date.now();
  await wait(WINDOW_MS);
  const idleCalls = calls.slice();
  out.idle = {
    seconds: Math.round((Date.now() - started) / 1000),
    requests: idleCalls.length,
    perSecond: Number((idleCalls.length / ((Date.now() - started) / 1000)).toFixed(2)),
    calls: idleCalls.map((c) => c.call),
  };
  out.loadWindow = {
    requests: out.load.length,
    duplicates: out.load.filter((c, i) => i > 0 && c.call === out.load[i - 1].call &&
      (c.t - out.load[i - 1].t) < 500 && c.t > out.load[i - 1].t).map((c) => c.call),
    calls: out.load.map((c) => c.call),
  };

  // ---- D18: the main menu, and the two routes that work -----------------------------------------------------
  await page.goto(BASE + '/web/#/home', { waitUntil: 'domcontentloaded', timeout: 60000 });
  await wait(6000);
  out.menuEntries = await page.evaluate(() => {
    const found = [];
    for (const el of document.querySelectorAll('a, button, [role="menuitem"], .navMenuOption, .mainDrawerButton')) {
      const s = ((el.innerText || '') + ' ' + (el.getAttribute('aria-label') || '')).trim();
      if (/subsync/i.test(s)) found.push(s.slice(0, 80));
    }
    return found;
  });

  await page.goto(BASE + '/web/#/dashboard/plugins', { waitUntil: 'domcontentloaded', timeout: 60000 });
  await wait(5000);
  out.routes.dashboardListsPlugin = await page.evaluate(() => (document.body.innerText || '').includes('SubSync'));

  await page.goto(BASE + '/web/configurationpage?name=subsync-main', { waitUntil: 'domcontentloaded', timeout: 60000 });
  await wait(7000);
  out.routes.pageRenders = await page.evaluate(() => {
    const el = document.getElementById('subsyncMainPage');
    return !!el && (el.innerText || '').length > 40;
  });
  out.routes.pageText = (await page.evaluate(() => (document.body.innerText || '').slice(0, 120)));

  await page.goto(BASE + '/web/configurationpage?name=SubSync', { waitUntil: 'domcontentloaded', timeout: 60000 });
  await wait(5000);
  out.routes.pointerReachesMainPage = page.url().includes('subsync-main')
    || await page.evaluate(() => (document.body.innerText || '').toLowerCase().includes('subsync'));

  fs.writeFileSync(path.join(HERE, LABEL + '.json'), JSON.stringify(out, null, 2));
  console.log(JSON.stringify(out, null, 2));
  await browser.close();
})().catch((e) => { console.error(e.stack || String(e)); process.exit(1); });
