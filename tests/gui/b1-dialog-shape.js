/* B1 (shape): what is inside each of the two dialogs, measured rather than eyeballed.

   The user's finding is that the issue is not colour, font or spacing (the B1 dialog probe measured those and
   they match), it is the shape: the series dialog is a compact form with one primary action, the movie/episode
   dialog is a long list where every track carries its own Sync button, and no Close button is visible.

   This probe reports, for a movie and for a series:
     * what controls the dialog holds (fields, selects, buttons, and where each button sits);
     * how tall the content is against how tall the card is (does the card scroll?);
     * whether the Close button buildShell always creates is inside the visible window or below it;
     * which body is the scrolling element.
*/
const fs = require('fs');
const { chromium } = require('/tmp/audit/pw/node_modules/playwright');
const BASE = 'http://127.0.0.1:8096';
const IDS = JSON.parse(fs.readFileSync(__dirname + '/ids.json', 'utf8'));
const LABEL = process.argv[2] || 'b1-shape';
const SHOTS = __dirname + '/shots';
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const AUTH = 'MediaBrowser Token="' + IDS.admin_token + '"';

(async () => {
  const user = await (await fetch(BASE + '/Users/New', {
    method: 'POST', headers: { 'Content-Type': 'application/json', Authorization: AUTH },
    body: JSON.stringify({ Name: 'gui-b1s-' + Date.now().toString(36) }),
  })).json();
  const current = await (await fetch(BASE + '/Users/' + user.Id, { headers: { Authorization: AUTH } })).json();
  await fetch(BASE + '/Users/' + user.Id + '/Policy', {
    method: 'POST', headers: { 'Content-Type': 'application/json', Authorization: AUTH },
    body: JSON.stringify(Object.assign({}, current.Policy, { IsAdministrator: true, EnableAllFolders: true })),
  });

  const browser = await chromium.launch({ args: ['--no-sandbox'], executablePath: '/opt/data/.playwright/chromium-1234/chrome-linux64/chrome' });
  const page = await (await browser.newContext({ viewport: { width: 1600, height: 1000 } })).newPage();
  await page.goto(BASE + '/web/', { waitUntil: 'domcontentloaded', timeout: 60000 });
  await wait(3500);
  await page.fill('input[name="username"], input[type="text"]', user.Name);
  const submit = await page.$('button[type="submit"], .btnSubmit, button:has-text("Sign In")');
  if (submit) await submit.click();
  await wait(9000);

  const out = {};
  for (const [kind, id] of [['movie', IDS.movie], ['series', IDS.series]]) {
    await page.goto(BASE + '/web/#/details?id=' + id, { waitUntil: 'domcontentloaded', timeout: 60000 });
    await wait(9000);
    // jellyfin-web keeps more than one "more" button in the document (a hidden one among them), so the
    // visible one is picked out and clicked from the page rather than by selector order.
    const opened = await page.evaluate(() => {
      const candidates = Array.from(document.querySelectorAll(
        '.detailButtons .btnMore, .btnMore, button[title="More"], button[aria-label="More"], button[aria-label="More options"]'));
      const visible = candidates.find((b) => b.offsetParent !== null && b.getBoundingClientRect().width > 0);
      if (!visible) return false;
      visible.click();
      return true;
    });
    if (!opened) { out[kind] = { error: 'no visible More button' }; continue; }
    await wait(2500);
    const item = await page.$('.actionSheetContent [data-id="subsync"]');
    await item.click();
    await wait(4000);
    out[kind] = await page.evaluate(() => {
      const card = document.querySelector('.ss-card');
      const body = document.querySelector('.ss-body');
      const actions = document.querySelector('.ss-actions');
      const rect = (el) => { const r = el.getBoundingClientRect(); return [Math.round(r.top), Math.round(r.bottom), Math.round(r.height)]; };
      const closeBtn = Array.from(document.querySelectorAll('.ss-btn')).find((b) => b.textContent.trim() === 'Close');
      const controls = Array.from(card.querySelectorAll('select, input, button')).map((c) => {
        const r = c.getBoundingClientRect();
        return { tag: c.tagName.toLowerCase(), text: (c.textContent || c.value || '').trim().slice(0, 26),
                 cls: c.className.slice(0, 26), options: c.tagName === 'SELECT' ? c.options.length : null,
                 rect: [Math.round(r.top), Math.round(r.bottom)], inWindow: r.top >= 0 && r.bottom <= window.innerHeight };
      });
      const scrolling = Array.from(card.querySelectorAll('*')).filter((e) => e.scrollHeight > e.clientHeight + 4)
        .map((e) => e.className || e.tagName);
      return {
        viewport: window.innerHeight,
        card: { rect: rect(card), scrollHeight: card.scrollHeight, clientHeight: card.clientHeight,
                overflowY: getComputedStyle(card).overflowY },
        body: body ? { rect: rect(body), scrollHeight: body.scrollHeight, clientHeight: body.clientHeight } : null,
        actions: actions ? { rect: rect(actions), visible: actions.getBoundingClientRect().bottom <= window.innerHeight } : null,
        closeButtonPresent: !!closeBtn,
        closeButtonRect: closeBtn ? rect(closeBtn) : null,
        closeButtonInWindow: closeBtn ? (closeBtn.getBoundingClientRect().top >= 0 && closeBtn.getBoundingClientRect().bottom <= window.innerHeight) : null,
        fields: card.querySelectorAll('.ss-field').length,
        selects: card.querySelectorAll('select').length,
        rows: card.querySelectorAll('.ss-row').length,
        buttonsInBody: card.querySelectorAll('.ss-body button').length,
        buttonsInActions: actions ? actions.querySelectorAll('button').length : 0,
        scrolling,
        controls: controls.slice(0, 8),
      };
    });
    await page.screenshot({ path: SHOTS + '/' + LABEL + '-' + kind + '.png' });
    await page.evaluate(() => { const c = document.querySelector('.ss-overlay'); if (c) c.remove(); });
    await wait(800);
  }

  fs.writeFileSync(__dirname + '/' + LABEL + '.json', JSON.stringify(out, null, 1));
  console.log(JSON.stringify(out, null, 1));
  await browser.close();
  await fetch(BASE + '/Users/' + user.Id, { method: 'DELETE', headers: { Authorization: AUTH } });
})();
