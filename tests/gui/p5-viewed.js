/* Pass 5, before/after pair: the row a plain click selects and views, and a single right-click pick.

   Both actions are real input (Playwright's own mouse), measured as the row's class and its computed
   background / inset bar / name colour, with a screenshot of the list for each. Run it against the
   unmodified build and against the fixed one; the files are named with the label so the two sets sit side
   by side.

   Usage: node p5-viewed.js <label>
*/
const fs = require('fs');
const { chromium } = require('/tmp/pw/node_modules/playwright');
const BASE = 'http://127.0.0.1:8096';
const USER = 'gui-p5';
const LABEL = process.argv[2] || 'before';
const SHOTS = __dirname + '/shots';
const wait = (ms) => new Promise((r) => setTimeout(r, ms));

const state = () => ({
  dataline: (document.querySelector('#ss-dataline') || {}).textContent || '',
  rows: Array.from(document.querySelectorAll('#ss-list .ss-row')).map((r, i) => {
    const cs = getComputedStyle(r);
    const nm = r.querySelector('.ss-name');
    return {
      i: i,
      name: nm ? nm.textContent : '',
      cls: r.className,
      bg: cs.backgroundColor,
      shadow: cs.boxShadow,
      nameColor: nm ? getComputedStyle(nm).color : null,
      nameWeight: nm ? getComputedStyle(nm).fontWeight : null,
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
    await page.screenshot({ path: SHOTS + '/p5-' + LABEL + '-look-' + name + '.png', clip: c });
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

  let rows = await page.$$('#ss-list .ss-row');
  out.clicked = await rows[3].evaluate((r) => (r.querySelector('.ss-name') || {}).textContent);
  await rows[3].click();
  await wait(2500);
  out.steps.push({ step: 'one row clicked (selected and viewed)', got: await page.evaluate(state) });
  await shot('1-clicked');

  rows = await page.$$('#ss-list .ss-row');
  out.picked = await rows[6].evaluate((r) => (r.querySelector('.ss-name') || {}).textContent);
  await rows[6].click({ button: 'right' });
  await wait(2500);
  out.steps.push({ step: 'one row right-clicked (picked) while another is viewed', got: await page.evaluate(state) });
  await shot('2-picked');

  fs.writeFileSync(__dirname + '/p5-' + LABEL + '-look.json', JSON.stringify(out, null, 1));
  console.log(JSON.stringify(out.steps.map((s) => ({
    step: s.step,
    dataline: s.got.dataline.replace(/^\d+ items in [^·]*/, '').trim(),
    marked: s.got.rows.filter((r) => r.cls.indexOf('viewed') !== -1 || r.cls.indexOf('selected') !== -1)
      .map((r) => r.i + ':' + r.name + ' [' + r.cls + '] bg=' + r.bg + ' bar=' + (r.shadow || '').slice(0, 24) + ' name=' + r.nameColor + '/' + r.nameWeight),
    optionsOn: s.got.rows.filter((r) => r.hasOptions).map((r) => r.i + ':' + r.name),
  })), null, 1));
  await browser.close();
})();
