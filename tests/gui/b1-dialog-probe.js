/* B1 — what the plugin shows on a detail page, compared across item kinds.

   The claim to confirm or refute: "movie / single-episode detail pages should visually match the
   series detail page's theme". There are exactly two plugin surfaces on a Jellyfin detail page —
   the injected action-sheet item and the dialog it opens — and only one of them is kind-dependent:
   `openDialog` sends a Movie/Episode/Video to `openSingleDialog` and a Series/Season to
   `openBulkDialog` (Web/subsync.js:865-883). So this probe photographs all three and measures the
   same elements in each, rather than comparing two dialogs by eye.

   It reports, per item kind: the action-sheet item, the dialog that opened, the computed style of
   every element the two dialogs have in common (card, heading, subtitle, field labels, selects,
   rows, buttons) and the geometry of the overlay and card. Screenshots go to shots/.

   Usage: PLAYWRIGHT_BROWSERS_PATH=... node b1-dialog-probe.js [label]
*/
const fs = require('fs');
const { chromium } = require('/tmp/audit/pw/node_modules/playwright');
const BASE = 'http://127.0.0.1:8096';
const IDS = JSON.parse(fs.readFileSync(__dirname + '/ids.json', 'utf8'));
const LABEL = process.argv[2] || 'b1-dialogs';
const SHOTS = __dirname + '/shots';
const OUT = __dirname + '/' + LABEL + '.json';
const wait = (ms) => new Promise((r) => setTimeout(r, ms));

const AUTH = 'MediaBrowser Token="' + IDS.admin_token + '"';
const results = { label: LABEL, base: BASE, steps: [] };

const STYLE_KEYS = ['backgroundColor', 'color', 'fontSize', 'fontWeight', 'textTransform',
  'letterSpacing', 'borderRadius', 'padding', 'borderColor', 'borderWidth', 'borderBottomColor',
  'boxShadow', 'fontFamily', 'lineHeight', 'display', 'gap'];

function measure() {
  const pick = (el) => {
    if (!el) return null;
    const cs = getComputedStyle(el);
    const out = { tag: el.tagName.toLowerCase(), cls: el.className };
    ['backgroundColor', 'color', 'fontSize', 'fontWeight', 'textTransform', 'letterSpacing',
      'borderRadius', 'padding', 'borderColor', 'borderWidth', 'borderBottomColor', 'boxShadow',
      'fontFamily', 'lineHeight', 'display', 'gap'].forEach((k) => { out[k] = cs[k]; });
    const r = el.getBoundingClientRect();
    out.box = [Math.round(r.x), Math.round(r.y), Math.round(r.width), Math.round(r.height)];
    return out;
  };
  const card = document.querySelector('.ss-card');
  return {
    overlay: pick(document.querySelector('.ss-overlay')),
    card: pick(card),
    heading: pick(document.querySelector('.ss-card h2')),
    sub: pick(document.querySelector('.ss-sub')),
    fieldLabel: pick(document.querySelector('.ss-field label')),
    select: pick(document.querySelector('.ss-field select')),
    row: pick(document.querySelector('.ss-row')),
    rowName: pick(document.querySelector('.ss-row-name')),
    rowMeta: pick(document.querySelector('.ss-row-meta')),
    primaryBtn: pick(document.querySelector('.ss-btn-primary')),
    plainBtn: pick(document.querySelector('.ss-btn')),
    bodyHtml: card ? card.innerHTML.slice(0, 1400) : null,
    rows: document.querySelectorAll('.ss-row').length,
    catalogSelect: null
  };
}

(async () => {
  // a throwaway administrator, signed in through the real form (the rig's own admin has a password)
  const user = await (await fetch(BASE + '/Users/New', {
    method: 'POST', headers: { 'Content-Type': 'application/json', Authorization: AUTH },
    body: JSON.stringify({ Name: 'gui-b1-' + Date.now().toString(36) }),
  })).json();
  const current = await (await fetch(BASE + '/Users/' + user.Id, { headers: { Authorization: AUTH } })).json();
  await fetch(BASE + '/Users/' + user.Id + '/Policy', {
    method: 'POST', headers: { 'Content-Type': 'application/json', Authorization: AUTH },
    body: JSON.stringify(Object.assign({}, current.Policy, { IsAdministrator: true, EnableAllFolders: true })),
  });
  results.user = { id: user.Id, name: user.Name };

  const browser = await chromium.launch({ args: ['--no-sandbox'], executablePath: '/opt/data/.playwright/chromium-1234/chrome-linux64/chrome' });
  const page = await (await browser.newContext({ viewport: { width: 1600, height: 1000 } })).newPage();
  const calls = [];
  page.on('request', (r) => { if (/SubSync\//.test(r.url())) calls.push(r.method() + ' ' + r.url().replace(BASE, '').split('?')[0]); });
  page.on('pageerror', (e) => calls.push('PAGEERROR ' + String(e).slice(0, 200)));
  page.on('console', (m) => { if (m.type() === 'error') calls.push('CONSOLE ' + m.text().slice(0, 200)); });

  await page.goto(BASE + '/web/', { waitUntil: 'domcontentloaded', timeout: 60000 });
  await wait(3500);
  await page.fill('input[name="username"], input[type="text"]', user.Name);
  const submit = await page.$('button[type="submit"], .btnSubmit, button:has-text("Sign In")');
  if (submit) await submit.click();
  await wait(9000);
  results.signed_in = await page.evaluate(() => location.hash);

  const targets = [
    { kind: 'movie', id: IDS.movie, label: 'Helikopterrånet S01E01 (Movie)' },
    { kind: 'episode', id: IDS.ep1, label: 'The Helicopter Heist S01E01 (Episode)' },
    { kind: 'series', id: IDS.series, label: 'The Helicopter Heist (Series)' },
    { kind: 'season', id: IDS.season, label: 'The Helicopter Heist S1 (Season)' },
  ];

  for (const t of targets) {
    const step = { kind: t.kind, item: t.id, label: t.label };
    try {
      await page.goto(BASE + '/web/#/details?id=' + t.id, { waitUntil: 'domcontentloaded', timeout: 60000 });
      await wait(9000);
      step.page = await page.evaluate(() => ({
        hash: location.hash,
        title: (document.querySelector('.nameContainer .itemName, h1, .itemName') || {}).textContent || '',
        buttons: Array.from(document.querySelectorAll('.detailButtons button, .mainDetailButtons button, button'))
          .map((b) => ({ cls: b.className, text: (b.innerText || '').trim().slice(0, 24), aria: b.getAttribute('aria-label') }))
          .filter((b) => /more|button/i.test(b.cls)).slice(0, 12),
      }));
      await page.screenshot({ path: SHOTS + '/' + LABEL + '-' + t.kind + '-1-page.png' });

      // open the ⋮ menu
      const more = await page.$('.detailButtons .btnMore, .btnMore, button[title="More"], .mainDetailButtons .btnMore')
        || await page.$('button[aria-label="More"], button[aria-label="More options"]');
      if (!more) { step.error = 'no More button found'; results.steps.push(step); continue; }
      await more.click();
      await wait(2500);
      await page.screenshot({ path: SHOTS + '/' + LABEL + '-' + t.kind + '-2-actionsheet.png' });

      step.menuItem = await page.evaluate(() => {
        const it = document.querySelector('.actionSheetContent [data-id="subsync"]');
        if (!it) return null;
        const cs = getComputedStyle(it);
        const r = it.getBoundingClientRect();
        return {
          text: (it.innerText || '').trim(),
          cls: it.className,
          icon: (it.querySelector('.material-icons') || {}).className || null,
          color: cs.color, fontSize: cs.fontSize, padding: cs.padding,
          box: [Math.round(r.x), Math.round(r.y), Math.round(r.width), Math.round(r.height)],
          siblings: Array.from(it.parentNode.children).map((c) => ((c.innerText || '').trim().split('\n')[0] || c.tagName)).slice(0, 14),
        };
      });

      const item = await page.$('.actionSheetContent [data-id="subsync"]');
      if (!item) { step.error = 'the plugin menu item was not injected'; results.steps.push(step); continue; }
      await item.click();
      await wait(4000);
      step.dialog = await page.evaluate(measure);
      step.sheetStillOpen = await page.evaluate(() => !!document.querySelector('.actionSheetContent'));
      await page.screenshot({ path: SHOTS + '/' + LABEL + '-' + t.kind + '-3-dialog.png' });
      await page.evaluate(() => { const c = document.querySelector('.ss-overlay'); if (c) c.remove(); });
      await wait(800);
    } catch (e) {
      step.error = String(e).slice(0, 300);
    }
    results.steps.push(step);
  }

  results.calls = calls;
  fs.writeFileSync(OUT, JSON.stringify(results, null, 1));
  console.log(JSON.stringify({ out: OUT, signed_in: results.signed_in, steps: results.steps.map((s) => ({ kind: s.kind, menu: s.menuItem && s.menuItem.text, err: s.error, dialog: s.dialog && { rows: s.dialog.rows, card: s.dialog.card && s.dialog.card.box, cardBg: s.dialog.card && s.dialog.card.backgroundColor, heading: s.dialog.heading && s.dialog.heading.fontSize, rowPad: s.dialog.row && s.dialog.row.padding, rowBorder: s.dialog.row && s.dialog.row.borderBottomColor, btn: s.dialog.primaryBtn && s.dialog.primaryBtn.backgroundColor } })) }, null, 1));

  // leave the rig as it was: the throwaway account goes away
  await browser.close();
  await fetch(BASE + '/Users/' + user.Id, { method: 'DELETE', headers: { Authorization: AUTH } });
})();
