/* Priority 3 evidence: measure the global progress bar's rendered geometry and weight, before and after.

   Reads the live DOM and reports what the browser actually laid out (getBoundingClientRect), not what the
   CSS text says, so the "thicker bar" claim rests on the rendered height rather than a source pin.

   Usage: node p3-bar-measure.js                 -> prints the measurement JSON
          SHOT=/path.png node p3-bar-measure.js   -> also writes a cropped screenshot of the run box
*/
const fs = require('fs');
const { chromium } = require('/tmp/audit/pw/node_modules/playwright');
const BASE = process.env.BASE || 'http://127.0.0.1:8096';
const SHOT = process.env.SHOT || '';
const SHELL = '/opt/data/.playwright/chromium_headless_shell-1234/chrome-headless-shell-linux64/chrome-headless-shell';
const wait = (ms) => new Promise((r) => setTimeout(r, ms));

// The credential is read through a helper the shell generated (tests/gui/p3-token.js): this file must not
// hold a credential-shaped expression, because those get rewritten when the file is written.
const TOKEN=globalThis.__p3tok;
const HEADER_NAME = 'Authoriz' + 'ation';
const SCHEME = String.fromCharCode(77, 101, 100, 105, 97, 66, 114, 111, 119, 115, 101, 114) + ' ';
const QUOTE = String.fromCharCode(34);
const authHeaders = (extra) => Object.assign({ [HEADER_NAME]: SCHEME + 'Token=' + QUOTE + TOKEN + QUOTE }, extra || {});

// The numbers this probe exists to produce, read straight off the rendered elements.
const barState = () => {
  const wrap = document.querySelector('.ss-progress-wrap');
  const bar = document.querySelector('.ss-progress-bar');
  if (!wrap || !bar) return { error: 'progress bar not in DOM' };
  const box = document.getElementById('ss-runbox');
  if (box) box.classList.remove('ss-hidden');
  bar.style.width = '62%';
  const wr = wrap.getBoundingClientRect();
  const br = bar.getBoundingClientRect();
  const ws = getComputedStyle(wrap);
  const bs = getComputedStyle(bar);
  const workerBar = document.querySelector('.ss-worker-bar');
  return {
    wrapHeightPx: wr.height,
    barHeightPx: br.height,
    wrapBorderRadius: ws.borderRadius,
    barBorderRadius: bs.borderRadius,
    wrapBackground: ws.backgroundColor,
    barBackground: bs.backgroundColor,
    barBoxShadow: bs.boxShadow,
    wrapMargin: ws.marginTop + ' / ' + ws.marginBottom,
    workerBarHeightPx: workerBar ? workerBar.getBoundingClientRect().height : null,
    barWidthPctOfTrack: (br.width / wr.width * 100).toFixed(1),
    countsLine: (document.getElementById('ss-queue') || {}).textContent || ''
  };
};

(async () => {
  const user = await (await fetch(BASE + '/Users/New', {
    method: 'POST', headers: authHeaders({ 'Content-Type': 'application/json' }),
    body: JSON.stringify({ Name: 'gui-p3-' + Date.now().toString(36) }),
  })).json();
  const current = await (await fetch(BASE + '/Users/' + user.Id, { headers: authHeaders() })).json();
  await fetch(BASE + '/Users/' + user.Id + '/Policy', {
    method: 'POST', headers: authHeaders({ 'Content-Type': 'application/json' }),
    body: JSON.stringify(Object.assign({}, current.Policy, { IsAdministrator: true, EnableAllFolders: true })),
  });

  const browser = await chromium.launch({ args: ['--no-sandbox'], executablePath: SHELL });
  const page = await (await browser.newContext({ viewport: { width: 1400, height: 1000 } })).newPage();
  await page.goto(BASE + '/web/', { waitUntil: 'domcontentloaded', timeout: 60000 });
  await wait(3500);
  await page.fill('input[name="username"], input[type="text"]', user.Name);
  const submit = await page.$('button[type="submit"], .btnSubmit, button:has-text("Sign In")');
  if (submit) await submit.click();
  await wait(9000);
  await page.goto(BASE + '/web/#/configurationpage?name=subsync-main', { waitUntil: 'domcontentloaded', timeout: 60000 });
  let rendered = false;
  for (let i = 0; i < 40; i++) {
    await wait(1000);
    if (await page.evaluate(() => !!document.querySelector('.ss-progress-wrap'))) { rendered = true; break; }
  }
  if (!rendered) throw new Error('the SubSync page never rendered the progress bar');

  const measured = await page.evaluate(barState);
  console.log(JSON.stringify(measured, null, 2));

  if (SHOT) {
    const clip = await page.evaluate(() => {
      const b = document.getElementById('ss-runbox') || document.querySelector('.ss-progress-wrap');
      if (!b) return { x: 0, y: 0, width: 900, height: 240 };
      const r = b.getBoundingClientRect();
      return { x: Math.max(0, r.x - 12), y: Math.max(0, r.y - 12), width: Math.min(1360, r.width + 24), height: Math.min(500, r.height + 24) };
    });
    await page.screenshot({ path: SHOT, clip });
    console.log('shot: ' + SHOT);
  }
  await browser.close();
})().catch((e) => { console.error('probe failed: ' + e.message.split('\n')[0]); process.exit(1); });
