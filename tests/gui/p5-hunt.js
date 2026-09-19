/* Pass 5, state hunt: which state, if any, leaves a single pick unmarked.

   The report is "a single item picked via right-click no longer gets the visual highlight". On the
   shipped build a right-click on an unviewed row DOES mark it (measured in p5-probe.js phase 1), so this
   walks the states a user actually passes through and measures the marking at each:

     1  left-click a row (view it), then right-click the SAME row (pick it) - the natural "look at a film,
        then add it" order
     2  right-click the FIRST row of the list
     3  right-click a Series row (the option panel has a different shape)
     4  right-click a row, then type in the search box (the list re-renders)
     5  right-click a row, then change the library filter (the list re-renders from a fresh fetch)
     6  right-click a row, then press "Select all" and "Clear selection" once each

   Usage: node p5-hunt.js [label]
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
  selBar: (function () {
    const b = document.querySelector('#ss-selactions');
    return b ? getComputedStyle(b).display : null;
  })(),
});

const clip = () => {
  const el = document.querySelector('#ss-list');
  const r = el.getBoundingClientRect();
  return { x: Math.max(0, r.x - 6), y: Math.max(0, r.y - 6), width: Math.min(1340, r.width + 12), height: Math.min(620, r.height + 12) };
};

const rcByIdx = (i) => { const r = document.querySelectorAll('#ss-list .ss-row')[i]; if (!r) return null; r.dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true })); return (r.querySelector('.ss-name') || {}).textContent; };
const lcByIdx = (i) => { const r = document.querySelectorAll('#ss-list .ss-row')[i]; if (!r) return null; r.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true })); return (r.querySelector('.ss-name') || {}).textContent; };
const rcSeries = () => {
  const r = Array.from(document.querySelectorAll('#ss-list .ss-row')).find((x) => /Series|Season/.test(((x.querySelector('.ss-meta') || {}).textContent) || ''));
  if (!r) return null;
  r.dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true }));
  return (r.querySelector('.ss-name') || {}).textContent;
};
const search = (q) => {
  const el = document.querySelector('#ss-search');
  el.value = q;
  el.dispatchEvent(new Event('input', { bubbles: true }));
  return q;
};
const clickText = (o) => {
  const el = Array.from(document.querySelectorAll(o.sel)).find((x) => (x.textContent || '').indexOf(o.text) !== -1);
  if (!el) return false;
  el.click();
  return true;
};

(async () => {
  const browser = await chromium.launch({ args: ['--no-sandbox'], executablePath: '/opt/data/.playwright/chromium-1234/chrome-linux64/chrome' });
  const page = await (await browser.newContext({ viewport: { width: 1400, height: 1100 } })).newPage();
  const out = { label: LABEL, steps: [] };
  const shot = async (name) => {
    const c = await page.evaluate(clip);
    await page.screenshot({ path: SHOTS + '/p5-' + LABEL + '-hunt-' + name + '.png', clip: c });
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

  // 1: view a row, then pick that same row
  out.steps.push({ step: 'baseline', got: await page.evaluate(mark) });
  out.viewedThenPicked = { view: await page.evaluate(lcByIdx, 3), pick: await page.evaluate(rcByIdx, 3) };
  await wait(2500);
  out.steps.push({ step: 'left-clicked row 3, then right-clicked row 3', got: await page.evaluate(mark) });
  await shot('1-viewed-then-picked');

  // reset the pick, then 2: pick the first row
  out.unpick = await page.evaluate(rcByIdx, 3);
  await wait(1500);
  out.firstRow = await page.evaluate(rcByIdx, 0);
  await wait(2500);
  out.steps.push({ step: 'right-clicked the first row', got: await page.evaluate(mark) });
  await shot('2-first-row-picked');

  // 3: a Series row
  out.series = await page.evaluate(rcSeries);
  await wait(2500);
  out.steps.push({ step: 'right-clicked a Series row (plus the first row still picked)', got: await page.evaluate(mark) });
  await shot('3-series-row-picked');

  // 4: search re-render
  out.searched = await page.evaluate((o) => { const el = document.querySelector(o.sel); el.value = o.q; el.dispatchEvent(new Event('input', { bubbles: true })); return o.q; }, { sel: '#ss-search', q: 'e' });
  await wait(2500);
  out.steps.push({ step: 'typed "e" in the search box', got: await page.evaluate(mark) });
  await shot('4-after-search');

  // 5: library filter change
  await page.evaluate(() => { const el = document.querySelector('#ss-library'); if (el && el.options.length > 1) { el.selectedIndex = 1; el.dispatchEvent(new Event('change', { bubbles: true })); } });
  await wait(3500);
  out.steps.push({ step: 'changed the library filter', got: await page.evaluate(mark) });
  await shot('5-after-library-change');

  // 6: select all / clear selection
  out.selectAll = await page.evaluate(clickText, { sel: 'a', text: 'Select all' });
  await wait(3000);
  out.steps.push({ step: 'pressed Select all', got: await page.evaluate(mark) });
  out.clear = await page.evaluate(clickText, { sel: 'a', text: 'Clear selection' });
  await wait(3000);
  out.steps.push({ step: 'pressed Clear selection', got: await page.evaluate(mark) });
  await shot('6-after-clear');

  fs.writeFileSync(__dirname + '/p5-' + LABEL + '-hunt.json', JSON.stringify(out, null, 1));
  const brief = out.steps.map((s) => ({
    step: s.step,
    dataline: s.got.dataline.replace(/^41 items in [^·]*/, '').trim(),
    picked: s.got.picked,
    pickedRows: s.got.rows.filter((r) => r.picked).map((r) => r.i + ':' + r.name + ':' + r.bg + ':' + (r.shadow || '').slice(0, 24)),
    selBar: s.got.selBar,
  }));
  console.log(JSON.stringify(brief, null, 1));
  await browser.close();
})();
