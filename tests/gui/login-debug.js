/*
 * Why did the login form not authenticate? And what does the web client need to see a real session?
 *
 * Runs the form step by step, prints every visible error, then tries the API the client itself uses
 * (`/Users/AuthenticateByName`) with the same credentials. If the API accepts them and the form does
 * not, that is a finding about the form path, not about the credentials, and the session can be seeded
 * the way the client stores it (localStorage `jellyfin_credentials`) so the rest of Matrix B can run
 * against a real, server-side session.
 */
const fs = require('fs');
const path = require('path');
const { chromium } = require('/tmp/audit/pw/node_modules/playwright');

const BASE = 'http://127.0.0.1:8096';
const HERE = __dirname;
const IDS = JSON.parse(fs.readFileSync(path.join(HERE, 'ids.json'), 'utf8'));
const wait = (ms) => new Promise((r) => setTimeout(r, ms));

(async () => {
  const out = { steps: [] };
  const browser = await chromium.launch({
    args: ['--no-sandbox'],
    executablePath: process.env.BROWSER_PATH || '/opt/data/.playwright/chromium-1234/chrome-linux64/chrome',
  });
  const ctx = await browser.newContext({ viewport: { width: 1600, height: 1000 } });
  const page = await ctx.newPage();
  const consoleLines = [];
  page.on('console', (m) => consoleLines.push(m.type() + ': ' + m.text().slice(0, 200)));
  page.on('response', async (r) => {
    if (r.url().includes('/Users/AuthenticateByName') || r.url().includes('Users/AuthenticateByName')) {
      out.authenticateResponse = { status: r.status(), url: r.url() };
    }
  });

  await page.goto(BASE + '/web/', { waitUntil: 'domcontentloaded', timeout: 60000 });
  await wait(5000);
  out.afterLoad = { url: page.url(), fields: await page.$$eval('input', (els) => els.map((e) => e.type || e.name)) };

  await page.fill('input[name="username"], #txtManualName, input[type="text"]', 'admin');
  const pw = await page.$('input[type="password"]');
  if (pw) await pw.fill('');
  await page.screenshot({ path: path.join(HERE, 'shots', 'login-debug-01-filled.png') });

  // the real button, not Enter
  const signIn = await page.$('button[type="submit"], .btnSubmit, button:has-text("Sign In")');
  out.signInButton = !!signIn;
  if (signIn) await signIn.click();
  await wait(7000);
  out.afterSubmit = {
    url: page.url(),
    text: (await page.evaluate(() => document.body.innerText)).slice(0, 300),
  };
  await page.screenshot({ path: path.join(HERE, 'shots', 'login-debug-02-after-submit.png') });

  // the API the client itself calls, with the same credentials
  out.api = await page.evaluate(async (base) => {
    const r = await fetch(base + '/Users/AuthenticateByName', {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        Authorization: 'MediaBrowser Client="LoginDebug", Device="Chromium", DeviceId="login-debug", Version="1.0.0"',
      },
      body: JSON.stringify({ Username: 'admin', Pw: '' }),
    });
    const body = await r.text();
    return { status: r.status, body: body.slice(0, 300) };
  }, BASE);

  // seeded session: the same shape the web client keeps in localStorage
  if (out.api.status === 200) {
    const creds = await page.evaluate(async (base) => {
      const r = await fetch(base + '/Users/AuthenticateByName', {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          Authorization: 'MediaBrowser Client="Jellyfin Web", Device="Chromium", DeviceId="gui-pass", Version="1.0.0"',
        },
        body: JSON.stringify({ Username: 'admin', Pw: '' }),
      });
      return r.json();
    }, BASE);
    await page.evaluate((c) => {
      localStorage.setItem('jellyfin_credentials', JSON.stringify({
        Servers: [{
          Id: c.ServerId, Name: 'test', AccessToken: c.AccessToken, UserId: c.User.Id,
          ManualAddress: 'http://127.0.0.1:8096', DateLastAccessed: Date.now(), LastConnectionMode: 0,
        }],
      }));
    }, creds);
    await page.goto(BASE + '/web/#/home', { waitUntil: 'domcontentloaded', timeout: 60000 });
    await wait(8000);
    out.seeded = { url: page.url(), text: (await page.evaluate(() => document.body.innerText)).slice(0, 200) };
    await page.screenshot({ path: path.join(HERE, 'shots', 'login-debug-03-seeded.png') });
  }

  out.console = consoleLines.filter((l) => /error|warn/i.test(l)).slice(0, 10);
  fs.writeFileSync(path.join(HERE, 'gui-login-debug.json'), JSON.stringify(out, null, 1));
  console.log(JSON.stringify(out, null, 1));
  await browser.close();
})();
