/* D1: the History panel - before and after, measured and photographed.

   Records what the panel is made of (rows, day headings, chips, counts lines, whether a monospace <pre>
   exists), then opens the first run and, after the redesign, exercises the two controls the new detail adds:
   the problems-only switch and Copy as text (read back from the clipboard). Every state is screenshotted.

   Usage: PLAYWRIGHT_BROWSERS_PATH=... node d1-history.js [label]
*/
const fs = require('fs');
const { chromium } = require('/tmp/audit/pw/node_modules/playwright');
const BASE = 'http://127.0.0.1:8096';
const IDS = JSON.parse(fs.readFileSync(__dirname + '/ids.json', 'utf8'));
const LABEL = process.argv[2] || 'd1';
const SHOTS = __dirname + '/shots';
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const AUTH = 'MediaBrowser Token="' + IDS.admin_token + '"';

const survey = () => {
  const panel = document.querySelector('#panel-history');
  const item = panel ? panel.querySelector('.ss-hist-item') : null;
  const detail = item ? item.querySelector('.ss-hist-detail') : null;
  const pre = panel ? panel.querySelector('pre') : null;
  const clipOf = (sel) => {
    const el = document.querySelector(sel);
    if (!el) return null;
    const r = el.getBoundingClientRect();
    return { x: Math.max(0, r.x - 20), y: Math.max(0, r.y - 12), width: Math.min(1330, r.width + 40), height: Math.min(760, r.height + 24) };
  };
  return {
    stats: (document.querySelector('#ss-history-stats') || {}).textContent || '',
    items: panel ? panel.querySelectorAll('.ss-hist-item').length : 0,
    dayHeadings: panel ? Array.from(panel.querySelectorAll('.ss-hist-day')).map((d) => d.textContent) : [],
    firstRow: item ? {
      title: (item.querySelector('.ss-hist-title') || {}).textContent || '',
      chip: (item.querySelector('.ss-hist-badge') || {}).textContent || '',
      counts: Array.from(item.querySelectorAll('.ss-hist-meta')).map((m) => m.textContent),
      hasPre: !!item.querySelector('pre'),
      hasDetail: !!detail,
      detailOpen: detail ? !detail.classList.contains('ss-hidden') : false,
      detailText: detail ? (detail.textContent || '').trim().slice(0, 400) : '',
      summary: detail ? (detail.querySelector('.ss-hist-summary') || {}).textContent || '' : '',
      filterButtons: detail ? Array.from(detail.querySelectorAll('.ss-hist-filter-btn')).map((b) => b.textContent) : [],
      taskRows: detail ? detail.querySelectorAll('.ss-task').length : 0,
      noteRows: detail ? Array.from(detail.querySelectorAll('.ss-task-note-row')).map((n) => n.textContent) : [],
      monospace: pre ? getComputedStyle(pre).fontFamily.slice(0, 30) : null,
    } : null,
    clip: clipOf('#panel-history'),
  };
};

(async () => {
  const user = await (await fetch(BASE + '/Users/New', {
    method: 'POST', headers: { 'Content-Type': 'application/json', Authorization: AUTH },
    body: JSON.stringify({ Name: 'gui-d1-' + Date.now().toString(36) }),
  })).json();
  const current = await (await fetch(BASE + '/Users/' + user.Id, { headers: { Authorization: AUTH } })).json();
  await fetch(BASE + '/Users/' + user.Id + '/Policy', {
    method: 'POST', headers: { 'Content-Type': 'application/json', Authorization: AUTH },
    body: JSON.stringify(Object.assign({}, current.Policy, { IsAdministrator: true, EnableAllFolders: true })),
  });

  const browser = await chromium.launch({ args: ['--no-sandbox'], executablePath: '/opt/data/.playwright/chromium-1234/chrome-linux64/chrome' });
  const context = await browser.newContext({ viewport: { width: 1400, height: 1000 }, permissions: ['clipboard-read', 'clipboard-write'] });
  const page = await context.newPage();
  await page.goto(BASE + '/web/', { waitUntil: 'domcontentloaded', timeout: 60000 });
  await wait(3500);
  await page.fill('input[name="username"], input[type="text"]', user.Name);
  const submit = await page.$('button[type="submit"], .btnSubmit, button:has-text("Sign In")');
  if (submit) await submit.click();
  await wait(9000);
  await page.goto(BASE + '/web/#/configurationpage?name=subsync-main', { waitUntil: 'domcontentloaded', timeout: 60000 });
  for (let i = 0; i < 30; i++) {
    await wait(1000);
    if (await page.evaluate(() => !!document.querySelector('.ss-nav-item'))) break;
  }
  await page.evaluate(() => { const b = document.querySelector('.ss-nav-item[data-panel="history"]'); if (b) b.click(); });
  await wait(4000);

  const out = { label: LABEL, steps: [] };
  const before = await page.evaluate(survey);
  out.steps.push({ step: 'history list', state: before });
  if (before.clip) await page.screenshot({ path: SHOTS + '/' + LABEL + '-1-list.png', clip: before.clip });

  // open the first run
  await page.evaluate(() => {
    const item = document.querySelector('#panel-history .ss-hist-item');
    if (item) item.click();
  });
  await wait(4000);
  const opened = await page.evaluate(survey);
  out.steps.push({ step: 'first run opened', state: opened });
  if (opened.clip) await page.screenshot({ path: SHOTS + '/' + LABEL + '-2-open.png', clip: opened.clip });

  // the two controls the redesign adds, if they are there
  const problems = await page.evaluate(() => {
    const btns = Array.from(document.querySelectorAll('#panel-history .ss-hist-filter-btn'));
    const b = btns.find((x) => /Problems/i.test(x.textContent));
    if (!b) return null;
    b.click();
    return b.textContent;
  });
  if (problems) {
    await wait(1200);
    const filteredState = await page.evaluate(survey);
    out.steps.push({ step: 'problems only', state: filteredState });
    if (filteredState.clip) await page.screenshot({ path: SHOTS + '/' + LABEL + '-3-problems.png', clip: filteredState.clip });
  }
  out.problemsButton = problems;

  const copied = await page.evaluate(async () => {
    const link = document.querySelector('#panel-history .ss-hist-copy');
    if (!link) return null;
    link.click();
    await new Promise((r) => setTimeout(r, 800));
    try {
      return (await navigator.clipboard.readText()).slice(0, 300);
    } catch (e) {
      return 'clipboard unreadable: ' + (e && e.message ? e.message : e);
    }
  });
  out.copiedText = copied;

  fs.writeFileSync(__dirname + '/' + LABEL + '-history.json', JSON.stringify(out, null, 1));
  console.log(JSON.stringify(out, null, 1));
  await browser.close();
  await fetch(BASE + '/Users/' + user.Id, { method: 'DELETE', headers: { Authorization: AUTH } });
})();
