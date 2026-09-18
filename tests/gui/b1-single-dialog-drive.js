/* B1, driven the way a user drives it: open the detail-page dialog on one movie, use the form, press Sync,
   and see whether the dialog reports what actually happened.

   What it records, all of it read from the live DOM:
     * the two pickers, their selected option, and the two lines under them (the detail line and the hint);
     * what the language filter does to the subtitle picker (options before and after filtering);
     * a real run started by the dialog's own Sync button, the button's own progress labels, the outcome
       line, and whether the picker says "synced before" about the track that was just written.

   Screenshots go to shots/ as <label>-<step>.png.

   Usage: PLAYWRIGHT_BROWSERS_PATH=... node b1-single-dialog-drive.js [label]
*/
const fs = require('fs');
const { chromium } = require('/tmp/audit/pw/node_modules/playwright');
const BASE = 'http://127.0.0.1:8096';
const IDS = JSON.parse(fs.readFileSync(__dirname + '/ids.json', 'utf8'));
const LABEL = process.argv[2] || 'b1-drive';
const SHOTS = __dirname + '/shots';
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const AUTH = 'MediaBrowser Token="' + IDS.admin_token + '"';
const out = { label: LABEL, steps: [] };

const readDialog = () => {
  const dialog = document.querySelector('.ss-card');
  if (!dialog) return null;
  const fields = Array.from(dialog.querySelectorAll('.ss-field')).map((f) => {
    const select = f.querySelector('select');
    return {
      label: (f.querySelector('label') || {}).textContent || '',
      options: select ? select.options.length : 0,
      selected: select ? select.options[select.selectedIndex].textContent : null,
      selectedValue: select ? select.value : null,
    };
  });
  const notes = Array.from(dialog.querySelectorAll('.ss-note')).map((n) => n.textContent);
  const primary = dialog.querySelector('.ss-actions .ss-btn-primary');
  const close = Array.from(dialog.querySelectorAll('.ss-actions .ss-btn')).find((b) => b.textContent.trim() === 'Close');
  const rect = (el) => { const r = el.getBoundingClientRect(); return [Math.round(r.top), Math.round(r.bottom)]; };
  return {
    sub: (dialog.querySelector('.ss-sub') || {}).textContent,
    fields,
    notes,
    rows: dialog.querySelectorAll('.ss-row').length,
    buttonsInBody: dialog.querySelectorAll('.ss-body button').length,
    primary: { label: primary ? primary.textContent.trim() : null, disabled: primary ? primary.disabled : null,
               inWindow: primary ? rect(primary)[0] >= 0 && rect(primary)[1] <= window.innerHeight : null },
    close: { present: !!close, inWindow: close ? rect(close)[0] >= 0 && rect(close)[1] <= window.innerHeight : null },
    card: { height: Math.round(dialog.getBoundingClientRect().height), scrollHeight: dialog.scrollHeight },
    progressLine: (dialog.querySelector('.ss-progress-line') || {}).textContent || '',
    error: (dialog.querySelector('.ss-error') || {}).textContent || '',
  };
};

(async () => {
  const user = await (await fetch(BASE + '/Users/New', {
    method: 'POST', headers: { 'Content-Type': 'application/json', Authorization: AUTH },
    body: JSON.stringify({ Name: 'gui-b1d-' + Date.now().toString(36) }),
  })).json();
  const current = await (await fetch(BASE + '/Users/' + user.Id, { headers: { Authorization: AUTH } })).json();
  await fetch(BASE + '/Users/' + user.Id + '/Policy', {
    method: 'POST', headers: { 'Content-Type': 'application/json', Authorization: AUTH },
    body: JSON.stringify(Object.assign({}, current.Policy, { IsAdministrator: true, EnableAllFolders: true })),
  });

  const browser = await chromium.launch({ args: ['--no-sandbox'], executablePath: '/opt/data/.playwright/chromium-1234/chrome-linux64/chrome' });
  const page = await (await browser.newContext({ viewport: { width: 1600, height: 1000 } })).newPage();
  const calls = [];
  page.on('request', (r) => { if (/SubSync\//.test(r.url())) calls.push(r.method() + ' ' + r.url().replace(BASE, '').split('?')[0]); });
  page.on('pageerror', (e) => calls.push('PAGEERROR ' + String(e).slice(0, 200)));

  await page.goto(BASE + '/web/', { waitUntil: 'domcontentloaded', timeout: 60000 });
  await wait(3500);
  await page.fill('input[name="username"], input[type="text"]', user.Name);
  const submit = await page.$('button[type="submit"], .btnSubmit, button:has-text("Sign In")');
  if (submit) await submit.click();
  await wait(9000);

  await page.goto(BASE + '/web/#/details?id=' + IDS.movie, { waitUntil: 'domcontentloaded', timeout: 60000 });
  await wait(9000);
  await page.evaluate(() => {
    const candidates = Array.from(document.querySelectorAll(
      '.detailButtons .btnMore, .btnMore, button[title="More"], button[aria-label="More"], button[aria-label="More options"]'));
    const visible = candidates.find((b) => b.offsetParent !== null && b.getBoundingClientRect().width > 0);
    if (visible) visible.click();
  });
  await wait(2500);
  const item = await page.$('.actionSheetContent [data-id="subsync"]');
  if (!item) { out.error = 'no plugin menu item'; console.log(JSON.stringify(out)); await browser.close(); return; }
  await item.click();
  await wait(4500);

  out.steps.push({ step: 'dialog as opened', state: await page.evaluate(readDialog) });
  await page.screenshot({ path: SHOTS + '/' + LABEL + '-1-open.png' });

  // the language filter narrows the picker
  const filtered = await page.evaluate(() => {
    const select = document.querySelector('.ss-card .ss-field select');
    if (!select || select.options.length < 3) return { skipped: 'no language filter' };
    const wanted = Array.from(select.options).find((o) => /Swedish/i.test(o.textContent));
    if (!wanted) return { skipped: 'no Swedish option' };
    select.value = wanted.value;
    select.dispatchEvent(new Event('change', { bubbles: true }));
    return { chose: wanted.textContent };
  });
  await wait(1200);
  out.steps.push({ step: 'after choosing a language', filter: filtered, state: await page.evaluate(readDialog) });
  await page.screenshot({ path: SHOTS + '/' + LABEL + '-2-filtered.png' });

  // press Sync and follow the run from the dialog alone
  const trackText = await page.evaluate(() => {
    const selects = document.querySelectorAll('.ss-card .ss-field select');
    const track = selects[selects.length - 1];
    return { text: track.options[track.selectedIndex].textContent, value: track.value };
  });
  out.pressed = trackText;
  await page.click('.ss-card .ss-actions .ss-btn-primary');
  const labels = [];
  let state = null;
  for (let i = 0; i < 90; i++) {
    await wait(2000);
    state = await page.evaluate(readDialog);
    const label = state && state.primary && state.primary.label;
    if (label && labels[labels.length - 1] !== label) labels.push(label);
    if (label === 'Synced' || label === 'Failed' || label === 'Cancelled') break;
  }
  out.buttonLabels = labels;
  out.steps.push({ step: 'after the run', state: state });
  await page.screenshot({ path: SHOTS + '/' + LABEL + '-3-done.png' });

  // the picker has to tell the truth about the track that was just written
  out.pickerAfterRun = await page.evaluate((value) => {
    const selects = document.querySelectorAll('.ss-card .ss-field select');
    const track = selects[selects.length - 1];
    const option = Array.from(track.options).find((o) => o.value === value);
    return option ? { text: option.textContent, selectedNow: track.value } : null;
  }, trackText.value);

  out.calls = calls;
  fs.writeFileSync(__dirname + '/' + LABEL + '.json', JSON.stringify(out, null, 1));
  console.log(JSON.stringify(out, null, 1));
  await browser.close();
  await fetch(BASE + '/Users/' + user.Id, { method: 'DELETE', headers: { Authorization: AUTH } });
})();
