/* The colour the browser actually resolved for a pending row's state word, read from the computed style.

   The DOM observation records the class name; this reads what the class resolves to, so "not red" is a
   measurement of the rendered colour rather than a claim about a stylesheet rule. It queues a batch behind
   an occupied pump, opens History, and reports each row's word with the colour the browser painted it.

   Usage: PLAYWRIGHT_BROWSERS_PATH=... node priority4-colour.js
*/
const fs = require('fs');
const { chromium } = require('/tmp/audit/pw/node_modules/playwright');
const BASE = 'http://127.0.0.1:8096';
const IDS = JSON.parse(fs.readFileSync(__dirname + '/ids.json', 'utf8'));
const SHOTS = __dirname + '/shots';
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const header = () => 'MediaBrowser Token="' + IDS.admin_token + '"';

(async () => {
  const user = await (await fetch(BASE + '/Users/New', {
    method: 'POST', headers: { 'Content-Type': 'application/json', Authorization: header() },
    body: JSON.stringify({ Name: 'gui-col-' + Date.now().toString(36) }),
  })).json();
  const current = await (await fetch(BASE + '/Users/' + user.Id, { headers: { Authorization: header() } })).json();
  await fetch(BASE + '/Users/' + user.Id + '/Policy', {
    method: 'POST', headers: { 'Content-Type': 'application/json', Authorization: header() },
    body: JSON.stringify(Object.assign({}, current.Policy, { IsAdministrator: true, EnableAllFolders: true })),
  });

  const items = await (await fetch(BASE + '/Items?Recursive=true&IncludeItemTypes=Movie&Limit=40', {
    headers: { Authorization: header() } })).json();
  const tasks = [];
  for (const item of (items.Items || [])) {
    const tracks = await (await fetch(BASE + '/SubSync/Subtitles/' + item.Id, { headers: { Authorization: header() } })).json();
    for (const t of (tracks || [])) {
      if (t.UnsupportedReason) continue;
      tasks.push({ itemId: item.Id, subtitleIndex: t.Index, title: item.Name });
    }
    if (tasks.length >= 30) break;
  }
  const view = await (await fetch(BASE + '/SubSync/Batch', {
    method: 'POST', headers: { 'Content-Type': 'application/json', Authorization: header() },
    body: JSON.stringify({ label: 'prio4-colour', tasks: tasks.slice(0, 30) }),
  })).json();

  const browser = await chromium.launch({ args: ['--no-sandbox'], executablePath: '/opt/data/.playwright/chromium-1234/chrome-linux64/chrome' });
  const page = await (await browser.newContext({ viewport: { width: 1400, height: 1000 } })).newPage();
  await page.goto(BASE + '/web/', { waitUntil: 'domcontentloaded', timeout: 60000 });
  await wait(3500);
  await page.fill('input[name="username"], input[type="text"]', user.Name);
  const submit = await page.$('button[type="submit"], .btnSubmit, button:has-text("Sign In")');
  if (submit) await submit.click();
  await wait(9000);
  await page.goto(BASE + '/web/#/configurationpage?name=subsync-main', { waitUntil: 'domcontentloaded', timeout: 60000 });
  for (let i = 0; i < 40; i++) {
    await wait(1000);
    if (await page.evaluate(() => !!document.querySelector('.ss-nav-item'))) break;
  }
  await page.evaluate(() => { const b = document.querySelector('.ss-nav-item[data-panel="history"]'); if (b) b.click(); });
  await wait(3500);
  const opened = await page.evaluate(async (label) => {
    const items = Array.from(document.querySelectorAll('#panel-history .ss-hist-item'));
    const mine = items.find((it) => ((it.querySelector('.ss-hist-title') || {}).textContent || '') === label);
    const target = mine || items[0];
    if (!target) return null;
    target.click();
    await new Promise((r) => setTimeout(r, 2500));
    return (target.querySelector('.ss-hist-title') || {}).textContent || '';
  }, 'prio4-colour');
  console.log('opened the row titled: ' + JSON.stringify(opened));

  const read = () => Array.from(document.querySelectorAll('#panel-history .ss-task')).map((row) => {
    const el = row.querySelector('.ss-task-state');
    const cs = el ? getComputedStyle(el) : null;
    return {
      word: el ? el.textContent : '',
      colour: cs ? cs.color : '',
      cls: row.className,
    };
  });

  let shot = false;
  let result = null;
  for (let i = 0; i < 40; i++) {
    const rows = await page.evaluate(read);
    const pending = rows.filter((r) => r.word === 'Queued' || r.word === 'Running');
    if (pending.length && !shot) {
      await page.screenshot({ path: SHOTS + '/priority4-colour-pending.png' });
      shot = true;
    }
    if (pending.length) {
      result = {
        pendingRows: pending,
        // #ff6b6b is .ss-task-bad's colour: any pending row painted with it is the defect
        redPending: pending.filter((r) => /255,\s*107,\s*107/.test(r.colour)),
        allRows: rows,
      };
    }
    const row = await (await fetch(BASE + '/SubSync/Batches', { headers: { Authorization: header() } })).json();
    const me = (row || []).find((b) => b.Id === view.Id);
    if (me && me.Completed >= me.Total && me.Total > 0) break;
    await wait(400);
  }

  console.log(JSON.stringify({
    batch: view.Id,
    sawPending: !!result,
    pendingWords: result ? result.pendingRows.map((r) => r.word + ' ' + r.colour) : [],
    redPendingCount: result ? result.redPending.length : null,
    anyRedRows: result ? result.allRows.filter((r) => /255,\s*107,\s*107/.test(r.colour)).map((r) => r.word) : [],
    shot: shot,
  }, null, 1));

  fs.writeFileSync(__dirname + '/priority4-colour.json', JSON.stringify(result || {}, null, 1));
  await browser.close();
  await fetch(BASE + '/Users/' + user.Id, { method: 'DELETE', headers: { Authorization: header() } });
})();
