/* Priority 4, verified where the defect was seen: a run opened in History *while it is still going*.

   The defect was in how a run's detail draws a task that has not finished. This probe queues a batch, opens
   the History tab immediately, expands the running row, and reads every result row's state word and colour
   class - the two things `buildTaskRow` produces. Then it samples until the run settles.

   What must be true after the fix:
     * no row reads "Failed" while its own task is still Queued or Running;
     * a pending row carries the neutral class, not `ss-task-bad`;
     * the run is still going when those rows are read (otherwise the probe proves nothing).

   Usage: PLAYWRIGHT_BROWSERS_PATH=... node priority4-history.js [label]
*/
const fs = require('fs');
const { chromium } = require('/tmp/audit/pw/node_modules/playwright');
const BASE = 'http://127.0.0.1:8096';
const IDS = JSON.parse(fs.readFileSync(__dirname + '/ids.json', 'utf8'));
const LABEL = process.argv[2] || 'prio4';
const SHOTS = __dirname + '/shots';
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const AUTH = 'MediaBrowser Token="' + IDS.admin_token + '"';

// What the run's detail actually renders: the state word and the class on every result row.
const detailRows = () => Array.from(document.querySelectorAll('#panel-history .ss-task')).map((row) => ({
  word: (row.querySelector('.ss-task-state') || {}).textContent || '',
  cls: row.className,
  note: (row.querySelector('.ss-task-note') || {}).textContent || '',
  name: (row.querySelector('.ss-task-name') || {}).textContent || '',
}));

const historyChips = () => Array.from(document.querySelectorAll('#panel-history .ss-hist-item')).slice(0, 4).map((it) => ({
  title: (it.querySelector('.ss-hist-title') || {}).textContent || '',
  chip: (it.querySelector('.ss-hist-badge') || {}).textContent || '',
  chipClass: (it.querySelector('.ss-hist-badge') || {}).className || '',
}));

(async () => {
  const user = await (await fetch(BASE + '/Users/New', {
    method: 'POST', headers: { 'Content-Type': 'application/json', Authorization: AUTH },
    body: JSON.stringify({ Name: 'gui-prio4-' + Date.now().toString(36) }),
  })).json();
  const current = await (await fetch(BASE + '/Users/' + user.Id, { headers: { Authorization: AUTH } })).json();
  await fetch(BASE + '/Users/' + user.Id + '/Policy', {
    method: 'POST', headers: { 'Content-Type': 'application/json', Authorization: AUTH },
    body: JSON.stringify(Object.assign({}, current.Policy, { IsAdministrator: true, EnableAllFolders: true })),
  });

  // Queue a sizeable batch from the API so its size is known and the run lasts long enough to open.
  const items = await (await fetch(BASE + '/Items?Recursive=true&IncludeItemTypes=Movie&Limit=40', {
    headers: { Authorization: AUTH },
  })).json();
  const tasks = [];
  for (const item of (items.Items || [])) {
    const tracks = await (await fetch(BASE + '/SubSync/Subtitles/' + item.Id, { headers: { Authorization: AUTH } })).json();
    for (const t of (tracks || [])) {
      if (t.UnsupportedReason) continue;
      tasks.push({ itemId: item.Id, subtitleIndex: t.Index, title: item.Name + ' — ' + (t.Title || ('track ' + t.Index)) });
    }
    if (tasks.length >= 24) break;
  }
  const batchView = await (await fetch(BASE + '/SubSync/Batch', {
    method: 'POST', headers: { 'Content-Type': 'application/json', Authorization: AUTH },
    body: JSON.stringify({ label: 'prio4-verify', tasks: tasks.slice(0, 24) }),
  })).json();
  const batchId = batchView.Id;
  console.log('queued batch ' + batchId + ' with ' + tasks.length + ' tasks; server says ' + batchView.Status);

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
  // Straight to History, and open the running row before the run is over.
  await page.evaluate(() => { const b = document.querySelector('.ss-nav-item[data-panel="history"]'); if (b) b.click(); });
  await wait(3000);

  const opened = await page.evaluate(async (id) => {
    // open the row for this batch by clicking the first item, then re-open if the detail closed
    const item = document.querySelector('#panel-history .ss-hist-item');
    if (item) item.click();
    await new Promise((r) => setTimeout(r, 2500));
    return !!document.querySelector('#panel-history .ss-hist-detail:not(.ss-hidden)');
  }, batchId);
  console.log('detail opened while the run was going: ' + opened);

  const samples = [];
  let shot = false;
  for (let i = 0; i < 60; i++) {
    const [rows, chips, apiRow] = await Promise.all([
      page.evaluate(detailRows),
      page.evaluate(historyChips),
      fetch(BASE + '/SubSync/Batches', { headers: { Authorization: AUTH } }).then((r) => r.json())
        .then((list) => (list || []).find((b) => b.Id === batchId)),
    ]);
    const bad = rows.filter((r) => r.cls.indexOf('ss-task-bad') !== -1);
    const failedWords = rows.filter((r) => r.word === 'Failed');
    samples.push({
      at: i,
      server: apiRow ? { Status: apiRow.Status, Total: apiRow.Total, Completed: apiRow.Completed, Ok: apiRow.Ok, Failed: apiRow.Failed } : null,
      words: rows.map((r) => r.word),
      badRows: bad.map((r) => r.word + ' | ' + r.note.slice(0, 60)),
      failedWordCount: failedWords.length,
      serverFailed: apiRow ? apiRow.Failed : null,
      chips: chips.map((c) => c.title + '=' + c.chip),
    });
    if (!shot && rows.some((r) => r.word === 'Queued' || r.word === 'Running')) {
      await page.screenshot({ path: SHOTS + '/' + LABEL + '-history-running.png' });
      shot = true;
    }
    if (apiRow && apiRow.Completed >= apiRow.Total && apiRow.Total > 0) break;
    await wait(500);
  }

  // The two claims that matter, evaluated over the whole run.
  const live = samples.filter((s) => s.server && s.server.Completed < s.server.Total);
  const mismatches = live.filter((s) => s.failedWordCount !== s.serverFailed);
  const out = {
    label: LABEL,
    batch: batchId,
    tasks: tasks.length,
    samplesWithRunUnfinished: live.length,
    mismatches: mismatches.length,
    worstSamples: mismatches.slice(0, 4),
    sawPendingWord: samples.some((s) => s.words.some((w) => w === 'Queued' || w === 'Running')),
    lastSample: samples[samples.length - 1],
  };
  fs.writeFileSync(__dirname + '/' + LABEL + '-history-verify.json', JSON.stringify(out, null, 1));
  console.log(JSON.stringify(out, null, 1));

  await browser.close();
  await fetch(BASE + '/Users/' + user.Id, { method: 'DELETE', headers: { Authorization: AUTH } });
})();
