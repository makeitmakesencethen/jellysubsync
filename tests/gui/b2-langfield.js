/* B2: the "Only sync subtitles in these languages" field - where the Add-language button actually sits.

   The row is a flex container with `align-items: flex-start` holding a `.ss-langfield` (max-width 340px,
   width 100%) and the button. This measures the three boxes and the gaps between them, and screenshots the
   row itself, so "crooked/offset" can be answered with numbers instead of a description.

   Usage: PLAYWRIGHT_BROWSERS_PATH=... node b2-langfield.js [label]
*/
const fs = require('fs');
const { chromium } = require('/tmp/audit/pw/node_modules/playwright');
const BASE = 'http://127.0.0.1:8096';
const IDS = JSON.parse(fs.readFileSync(__dirname + '/ids.json', 'utf8'));
const LABEL = process.argv[2] || 'b2';
const SHOTS = __dirname + '/shots';
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const AUTH = 'MediaBrowser Token="' + IDS.admin_token + '"';

(async () => {
  const user = await (await fetch(BASE + '/Users/New', {
    method: 'POST', headers: { 'Content-Type': 'application/json', Authorization: AUTH },
    body: JSON.stringify({ Name: 'gui-b2-' + Date.now().toString(36) }),
  })).json();
  const current = await (await fetch(BASE + '/Users/' + user.Id, { headers: { Authorization: AUTH } })).json();
  await fetch(BASE + '/Users/' + user.Id + '/Policy', {
    method: 'POST', headers: { 'Content-Type': 'application/json', Authorization: AUTH },
    body: JSON.stringify(Object.assign({}, current.Policy, { IsAdministrator: true, EnableAllFolders: true })),
  });

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
    const ready = await page.evaluate(() => {
      const s = document.querySelector('#ss-status');
      return !!s && /ffsubsync|ffmpeg/.test(s.textContent || '');
    });
    if (ready) break;
  }
  await page.evaluate(() => {
    const b = document.querySelector('.ss-nav-item[data-panel="settings"]');
    if (b) b.click();
  });
  await wait(1500);
  await page.evaluate(() => {
    const el = document.querySelector('#ss-langaddbtn');
    if (el) el.scrollIntoView({ block: 'center' });
  });
  await wait(800);

  const measured = await page.evaluate(() => {
    const input = document.querySelector('#ss-langadd');
    const field = input.parentElement;
    const button = document.querySelector('#ss-langaddbtn');
    const row = button.parentElement;
    const box = (el) => {
      const r = el.getBoundingClientRect();
      return { x: Math.round(r.x), y: Math.round(r.y), w: Math.round(r.width), h: Math.round(r.height),
               right: Math.round(r.right), bottom: Math.round(r.bottom) };
    };
    return {
      row: Object.assign(box(row), { alignItems: getComputedStyle(row).alignItems, display: getComputedStyle(row).display }),
      field: Object.assign(box(field), { maxWidth: getComputedStyle(field).maxWidth, width: getComputedStyle(field).width }),
      input: Object.assign(box(input), { cssWidth: getComputedStyle(input).width, minHeight: getComputedStyle(input).minHeight }),
      button: Object.assign(box(button), { minHeight: getComputedStyle(button).minHeight, marginTop: getComputedStyle(button).marginTop }),
      gapInputToButton: Math.round(button.getBoundingClientRect().x - input.getBoundingClientRect().right),
      fieldSlack: Math.round(field.getBoundingClientRect().right - input.getBoundingClientRect().right),
      topDelta: Math.round(input.getBoundingClientRect().top - button.getBoundingClientRect().top),
    };
  });
  const survey = await page.evaluate(() => {
    const controls = Array.from(document.querySelectorAll('#panel-settings input, #panel-settings select, #panel-settings button'));
    return controls.map((el) => {
      const cs = getComputedStyle(el);
      const r = el.getBoundingClientRect();
      return {
        tag: el.tagName.toLowerCase(), id: el.id || null, cls: el.className || null,
        is: el.getAttribute('is') || null,
        size: [Math.round(r.width), Math.round(r.height)],
        bg: cs.backgroundColor, color: cs.color, border: cs.borderTopWidth + ' ' + cs.borderTopColor,
        fontSize: cs.fontSize, padding: cs.padding,
      };
    });
  });

  const rowBox = await page.evaluate(() => {
    const row = document.querySelector('#ss-langaddbtn').parentElement.getBoundingClientRect();
    return { x: row.x, y: row.y, w: row.width, h: row.height };
  });
  await page.screenshot({ path: SHOTS + '/' + LABEL + '-settings-languagefield.png',
    clip: { x: Math.max(0, rowBox.x - 40), y: Math.max(0, rowBox.y - 90), width: Math.min(760, rowBox.w + 120), height: rowBox.h + 150 } });
  console.log(JSON.stringify({ label: LABEL, measured, survey, shot: SHOTS + '/' + LABEL + '-settings-languagefield.png' }, null, 1));
  fs.writeFileSync(__dirname + '/' + LABEL + '-langfield.json', JSON.stringify({ label: LABEL, measured, survey }, null, 1));
  await browser.close();
  await fetch(BASE + '/Users/' + user.Id, { method: 'DELETE', headers: { Authorization: AUTH } });
})();
