/*
 * D14 — is the injected item-menu entry actually there on the SPA's item page?
 *
 * The script is delivered into the SPA shell, which was verified earlier; what was never checked is whether its hooks
 * match the markup the React item page renders. The failure mode is a missing button, not damage, so the measurement is
 * simply: open the detail page of a movie, look for the injected menu entry (opening the ⋮ menu if it is not already
 * open), and record what is there. It also presses the entry, to see whether pressing it does anything at all - the
 * menu item opens the plugin's own sheet rather than queueing work directly.
 */
const fs = require('fs');
const path = require('path');
const { chromium } = require('/tmp/audit/pw/node_modules/playwright');

const BASE = 'http://127.0.0.1:8096';
const HERE = __dirname;
const LABEL = process.env.LABEL || 'd14';
const IDS = JSON.parse(fs.readFileSync(path.join(HERE, 'ids.json'), 'utf8'));
const wait = (ms) => new Promise((r) => setTimeout(r, ms));

(async () => {
  const out = { label: LABEL, movie: IDS.movie, menu: {}, api: [] };
  const authHeader = 'MediaBrowser Token="' + IDS.admin_token + '"';
  const USER_NAME = 'guitest-d14-' + Date.now().toString(36);
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
    if (r.url().includes('/SubSync/')) out.api.push(r.method() + ' ' + r.url().replace(BASE, ''));
  });

  await page.goto(BASE + '/web/', { waitUntil: 'domcontentloaded', timeout: 60000 });
  await wait(4000);
  await page.fill('input[name="username"], #txtManualName, input[type="text"]', USER_NAME);
  const submit = await page.$('button[type="submit"], .btnSubmit, button:has-text("Sign In")');
  if (submit) await submit.click();
  await wait(8000);

  // The script is injected into every page, so the item page is where its hook has to match.
  await page.goto(BASE + '/web/#/details?id=' + IDS.movie, { waitUntil: 'domcontentloaded', timeout: 60000 });
  await wait(9000);
  out.api = [];
  out.menu.scriptLoaded = await page.evaluate(() => typeof window.SubSyncMenu !== 'undefined' || !!document.querySelector('script[src*="ClientScript"]'));

  const findEntry = () => page.evaluate(() => {
    const nodes = Array.from(document.querySelectorAll('button, a, [data-id="subsync"]'));
    const hit = nodes.find((n) => ((n.innerText || '') + ' ' + (n.getAttribute('aria-label') || '')).toLowerCase().includes('sync subtitles'));
    return hit ? { text: (hit.innerText || '').trim(), id: hit.getAttribute('data-id') } : null;
  });

  out.menu.entryBeforeOpeningMenu = await findEntry();
  if (!out.menu.entryBeforeOpeningMenu) {
    // open the ⋮ menu the way a user does, then look again
    const opened = await page.evaluate(() => {
      const more = document.querySelector('.btnMoreCommands, button[title="More"], button[aria-label*="More"]');
      if (more) { more.click(); return true; }
      return false;
    });
    out.menu.openedMoreCommands = opened;
    await wait(1500);
    out.menu.entryAfterOpeningMenu = await findEntry();
  }

  const entry = out.menu.entryBeforeOpeningMenu || out.menu.entryAfterOpeningMenu;
  out.menu.found = !!entry;
  if (entry) {
    out.menu.clicked = await page.evaluate(() => {
      const nodes = Array.from(document.querySelectorAll('button, a, [data-id="subsync"]'));
      const hit = nodes.find((n) => ((n.innerText || '') + ' ' + (n.getAttribute('aria-label') || '')).toLowerCase().includes('sync subtitles'));
      if (hit) { hit.click(); return true; }
      return false;
    });
    await wait(4000);
    out.menu.afterClickText = (await page.evaluate(() => (document.body.innerText || '').slice(-400)));
    out.apiAfterClick = out.api.slice(-6);
  }

  fs.writeFileSync(path.join(HERE, LABEL + '.json'), JSON.stringify(out, null, 2));
  console.log(JSON.stringify(out, null, 2));
  await browser.close();
})().catch((e) => { console.error(e.stack || String(e)); process.exit(1); });
