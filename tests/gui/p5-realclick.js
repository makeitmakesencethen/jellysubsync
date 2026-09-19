/* Pass 5: with the real mouse, not a synthetic event.

   A synthetic `contextmenu` dispatch runs the same handler, but it is not what the user does. This uses
   Playwright's own input path (page.click with button:'right') so the browser sends mousedown, mouseup and
   contextmenu the way a real right-click does, and measures the row afterwards - then does the same for a
   real left-click.

   Usage: node p5-realclick.js [label]
*/
const fs = require('fs');
const { chromium } = require('/tmp/pw/node_modules/playwright');
const BASE = 'http://127.0.0.1:8096';
const USER = 'gui-p5';
const LABEL = process.argv[2] || 'before';
const SHOTS = __dirname + '/shots';
const wait = (ms) => new Promise((r) => setTimeout(r, ms));

const mark = () => ({
  dataline: (document.querySelector('#ss-dataline') || {}).textContent || '',
  picked: Array.from(document.querySelectorAll('#ss-list .ss-row'))
    .filter((r) => r.classList.contains('selected'))
    .map((r) => (r.querySelector('.ss-name') || {}).textContent || ''),
  rows: Array.from(document.querySelectorAll('#ss-list .ss-row')).map((r, i) => {
    const cs = getComputedStyle(r);
    const nm = r.querySelector('.ss-name');
    return {
      i: i,
      name: nm ? nm.textContent : '',
      picked: r.classList.contains('selected'),
      bg: cs.backgroundColor,
      shadow: cs.boxShadow,
      nameColor: nm ? getComputedStyle(nm).color : null,
      hasOptions: !!r.querySelector('.ss-row-actions'),
    };
  }),
});

(async () => {
  const browser = await chromium.launch({ args: ['--no-sandbox'], executablePath: '/opt/data/.playwright/chromium-1234/chrome-linux64/chrome' });
  const page = await (await browser.newContext({ viewport: { width: 1400, height: 1100 } })).newPage();
  const out = { label: LABEL, steps: [] };
  const shot = async (name) => {
    const c = await page.evaluate(() => {
      const el = document.querySelector('#ss-list');
      const r = el.getBoundingClientRect();
      return { x: Math.max(0, r.x - 6), y: Math.max(0, r.y - 6), width: Math.min(1340, r.width + 12), height: Math.min(620, r.height + 12) };
    });
    await page.screenshot({ path: SHOTS + '/p5-' + LABEL + '-real-' + name + '.png', clip: c });
  };

  await page.goto(BASE + '/web/', { waitUntil: 'domcontentloaded', timeout: 60000 });
  await wait(3500);
  await page.fill('input[name="username"], input[type="text"]', USER);
  const submit = await page.$('button[type="submit"], .btnSubmit');
  if (submit) await submit.click();
  await wait(9000);
  await page.goto(BASE + '/web/#/configurationpage?name=subsync-main', { waitUntil: 'domcontentloaded', timeout: 60000 });
  for (let i = 0; i < 40; i++) {
    await wait(1000);
    if (await page.evaluate(() => document.querySelectorAll('#ss-list .ss-row').length > 5)) break;
  }

  // a real right-click on the fourth row
  const rows = await page.$$('#ss-list .ss-row');
  out.rows_seen = rows.length;
  await rows[3].click({ button: 'right' });
  await wait(2500);
  out.steps.push({ step: 'real right-click on row 3', got: await page.evaluate(mark) });
  await shot('1-right-click');

  // a real right-click on a second row
  const rows2 = await page.$$('#ss-list .ss-row');
  await rows2[6].click({ button: 'right' });
  await wait(2500);
  out.steps.push({ step: 'real right-click on a second row', got: await page.evaluate(mark) });
  await shot('2-two-right-clicks');

  fs.writeFileSync(__dirname + '/p5-' + LABEL + '-realclick.json', JSON.stringify(out, null, 1));
  console.log(JSON.stringify(out.steps.map((s) => ({
    step: s.step,
    dataline: s.got.dataline.replace(/^41 items in [^·]*/, '').trim(),
    picked: s.got.picked,
    marked: s.got.rows.filter((r) => r.picked).map((r) => r.i + ':' + r.name + ':' + r.bg + ':' + (r.shadow || '').slice(0, 22) + ':' + r.nameColor),
  })), null, 1));
  await browser.close();
})();
