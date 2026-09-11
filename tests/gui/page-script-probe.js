const fs = require('fs');
const { chromium } = require('/tmp/audit/pw/node_modules/playwright');
const BASE = 'http://127.0.0.1:8096';
const IDS = JSON.parse(fs.readFileSync('/opt/data/jellysubsync/tests/gui/ids.json','utf8'));
const wait = (ms) => new Promise(r => setTimeout(r, ms));
(async () => {
  const auth = 'MediaBrowser Token="' + IDS.admin_token + '"';
  const u = await (await fetch(BASE + '/Users/New', { method:'POST', headers:{'Content-Type':'application/json', Authorization:auth}, body: JSON.stringify({ Name: 'guitest-p2-' + Date.now().toString(36) }) })).json();
  await fetch(BASE + '/Users/' + u.Id + '/Policy', { method:'POST', headers:{'Content-Type':'application/json', Authorization:auth}, body: JSON.stringify({ IsAdministrator:true, EnableAllFolders:true }) });
  const browser = await chromium.launch({ args:['--no-sandbox'], executablePath:'/opt/data/.playwright/chromium-1234/chrome-linux64/chrome' });
  const page = await (await browser.newContext({ viewport:{width:1400,height:900} })).newPage();
  const msgs = [], reqs = [];
  page.on('console', m => msgs.push(m.type() + ': ' + m.text().slice(0,160)));
  page.on('pageerror', e => msgs.push('PAGEERROR: ' + String(e).slice(0,200)));
  page.on('request', r => { if (/SubSync/.test(r.url())) reqs.push(r.method() + ' ' + r.url().replace(BASE,'')); });
  await page.goto(BASE + '/web/', { waitUntil:'domcontentloaded', timeout:60000 });
  await wait(3500);
  await page.fill('input[name="username"], input[type="text"]', u.Name);
  const b = await page.$('button[type="submit"], .btnSubmit, button:has-text("Sign In")');
  if (b) await b.click();
  await wait(7000);
  msgs.length = 0; reqs.length = 0;
  await page.goto(BASE + '/web/configurationpage?name=subsync-main', { waitUntil:'domcontentloaded', timeout:60000 });
  await wait(10000);
  const state = await page.evaluate(() => ({
    externalRan: window.__ssMainScriptRan === true,
    hasPageEl: !!document.getElementById('subsyncMainPage'),
    scripts: Array.from(document.scripts).map(s => s.src || '(inline)').slice(0, 6),
    status: (document.querySelector('#ss-status') || {}).textContent,
  }));
  console.log('STATE', JSON.stringify(state));
  console.log('SUBSYNC REQS', JSON.stringify(reqs.slice(0,10)));
  console.log('CONSOLE', JSON.stringify(msgs.filter(m => /SUBSYNC|PAGEERROR|error/i.test(m)).slice(0,8)));
  await fetch(BASE + '/Users/' + u.Id, { method:'DELETE', headers:{Authorization:auth} });
  await browser.close();
})();
