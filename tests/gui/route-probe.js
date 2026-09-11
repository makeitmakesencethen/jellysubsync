/*
 * D18 — how is the plugin's own page reached in Jellyfin 12?
 *
 * The plugin registers two pages through `IHasWebPages` (`SubSync` = the dashboard config page, and
 * `subsync-main` = the main-menu page with `EnableInMainMenu = true`). The 2026-09-11 session recorded
 * that no main-menu entry appears; this probe tries every route shape the web client might use, from
 * the dashboard's own plugin list to the direct page URLs, and reports which one renders plugin markup
 * (`[id^="ss-"]`).
 */
const fs = require('fs');
const path = require('path');
const { chromium } = require('/tmp/audit/pw/node_modules/playwright');

const BASE = 'http://127.0.0.1:8096';
const HERE = __dirname;
const IDS = JSON.parse(fs.readFileSync(path.join(HERE, 'ids.json'), 'utf8'));
const wait = (ms) => new Promise((r) => setTimeout(r, ms));

(async () => {
  const out = { probes: [], mainMenu: null, dashboard: null };
  const authHeader = 'MediaBrowser Token="' + IDS.admin_token + '"';
  const USER_NAME = 'guitest-route-' + Date.now().toString(36);
  const created = await (await fetch(BASE + '/Users/New', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', Authorization: authHeader },
    body: JSON.stringify({ Name: USER_NAME }),
  })).json();
  await fetch(BASE + '/Users/' + created.Id + '/Policy', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', Authorization: authHeader },
    body: JSON.stringify({ IsAdministrator: true, EnableAllFolders: true, EnableRemoteAccess: true }),
  });

  const browser = await chromium.launch({
    args: ['--no-sandbox'],
    executablePath: process.env.BROWSER_PATH || '/opt/data/.playwright/chromium-1234/chrome-linux64/chrome',
  });
  const page = await (await browser.newContext({ viewport: { width: 1600, height: 1000 } })).newPage();
  await page.goto(BASE + '/web/', { waitUntil: 'domcontentloaded', timeout: 60000 });
  await wait(4000);
  await page.fill('input[name="username"], #txtManualName, input[type="text"]', USER_NAME);
  const submit = await page.$('button[type="submit"], .btnSubmit, button:has-text("Sign In")');
  if (submit) await submit.click();
  await wait(8000);
  out.loggedIn = !/Please sign in/.test(await page.evaluate(() => document.body.innerText));

  // the main menu, as the client renders it
  await page.goto(BASE + '/web/#/home', { waitUntil: 'domcontentloaded', timeout: 60000 });
  await wait(6000);
  out.mainMenu = await page.evaluate(() => {
    const links = [];
    for (const el of document.querySelectorAll('a[href], .navMenuOption, .mainDrawerButton, [role="menuitem"]')) {
      const href = el.getAttribute('href') || '';
      const text = (el.innerText || el.getAttribute('aria-label') || '').trim();
      if (href || text) links.push({ text: text.slice(0, 40), href });
    }
    return { count: links.length, subsyncEntries: links.filter((l) => /subsync/i.test(l.text + ' ' + l.href)) };
  });

  // the dashboard's plugin list, and the links it offers
  await page.goto(BASE + '/web/#/dashboard/plugins', { waitUntil: 'domcontentloaded', timeout: 60000 });
  await wait(7000);
  out.dashboard = await page.evaluate(() => ({
    url: location.href,
    text: (document.body.innerText || '').slice(0, 400),
    links: Array.from(document.querySelectorAll('a[href]'))
      .map((a) => ({ text: (a.innerText || '').trim().slice(0, 40), href: a.getAttribute('href') }))
      .filter((l) => /subsync|plugin/i.test(l.text + ' ' + l.href)).slice(0, 20),
  }));
  await page.screenshot({ path: path.join(HERE, 'shots', 'route-dashboard-plugins.png') });

  const routes = [
    '/web/configurationpage?name=subsync-main',
    '/web/configurationpage?name=SubSync',
    '/web/#/configurationpage?name=subsync-main',
    '/web/#/configurationpage?name=SubSync',
    '/web/index.html#/configurationpage?name=subsync-main',
    '/web/#/plugins/subsync-main',
    '/web/#/dashboard/plugins/subsync-main',
    '/web/#/dashboard/plugins/' + IDS.plugin_id,
    '/SubSync/ClientScript',
  ];

  for (const route of routes) {
    const probe = { route };
    try {
      await page.goto(BASE + route, { waitUntil: 'domcontentloaded', timeout: 45000 });
      await wait(4000);
      probe.finalUrl = page.url();
      probe.ssIds = await page.evaluate(() => Array.from(document.querySelectorAll('[id^="ss-"]')).map((e) => e.id).slice(0, 8));
      probe.bodyStart = (await page.evaluate(() => document.body.innerText)).slice(0, 120).replace(/\n/g, ' | ');
      probe.hasSubSyncText = await page.evaluate(() => /subsync/i.test(document.body.innerText || ''));
    } catch (e) {
      probe.error = String(e).slice(0, 160);
    }
    out.probes.push(probe);
    console.log('PROBE ' + route + ' -> ' + JSON.stringify({ finalUrl: probe.finalUrl, ssIds: probe.ssIds, hasSubSyncText: probe.hasSubSyncText, error: probe.error }));
  }

  await fetch(BASE + '/Users/' + created.Id, { method: 'DELETE', headers: { Authorization: authHeader } });
  fs.writeFileSync(path.join(HERE, 'route-probe.json'), JSON.stringify(out, null, 1));
  await browser.close();
  console.log('DONE — wrote tests/gui/route-probe.json');
})();
