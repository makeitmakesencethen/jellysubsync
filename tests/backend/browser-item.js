/*
 * Real-browser pass over the injected item-page integration (audit D14/D13 were never verified
 * in a browser) and the series/season pages.
 *
 * A real Chromium loads the real Jellyfin Web client from the live test server, logs in through the
 * login form, opens the detail page of an episode, and reports what the injected client script
 * actually produced there (buttons, dialog, API calls, console output).
 */
const fs = require('fs');
const { chromium } = require('/tmp/audit/pw/node_modules/playwright');

const BASE = 'http://127.0.0.1:8096';
const OUT = '/opt/data/tmp/backendtest/browser';
fs.mkdirSync(OUT, { recursive: true });
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const IDS = JSON.parse(fs.readFileSync('/opt/data/tmp/backendtest/ids.json', 'utf8'));

(async () => {
  const report = { steps: [], api: [], console: [], errors: [] };
  const browser = await chromium.launch({ args: ['--no-sandbox'] });
  const ctx = await browser.newContext({ viewport: { width: 1600, height: 1000 } });
  const page = await ctx.newPage();
  page.on('console', (m) => {
    const t = m.text();
    if (t.includes('SubSync') || m.type() === 'error') report.console.push(m.type() + ': ' + t.slice(0, 300));
  });
  page.on('pageerror', (e) => report.errors.push(String(e).slice(0, 300)));
  page.on('request', (r) => {
    const u = r.url();
    if (u.includes('/SubSync/')) report.api.push(r.method() + ' ' + u.replace(BASE, ''));
  });
  page.on('response', async (r) => {
    const u = r.url();
    if (u.includes('/SubSync/')) {
      const i = report.api.findIndex((x) => x.endsWith(u.replace(BASE, '')));
      if (i >= 0) report.api[i] += ' -> ' + r.status();
    }
  });

  const step = async (name, fn) => {
    try {
      const v = await fn();
      report.steps.push({ name, ok: true, value: v });
      console.log('STEP', name, JSON.stringify(v).slice(0, 400));
    } catch (e) {
      report.steps.push({ name, ok: false, error: String(e).slice(0, 300) });
      console.log('STEP', name, 'FAILED', String(e).slice(0, 200));
    }
  };

  // --- 1. the login page ---------------------------------------------------
  await step('open-web', async () => {
    await page.goto(BASE + '/web/', { waitUntil: 'domcontentloaded', timeout: 60000 });
    await wait(4000);
    return { url: page.url(), title: await page.title() };
  });

  await step('login', async () => {
    const inputs = await page.$$('input');
    const info = [];
    for (const el of inputs) {
      info.push({
        type: await el.getAttribute('type'),
        name: await el.getAttribute('name'),
        id: await el.getAttribute('id'),
        ph: await el.getAttribute('placeholder'),
      });
    }
    const user = await page.$('input[name="username"], #txtManualName, input[type="text"]');
    if (user) await user.fill('admin');
    const pw = await page.$('input[type="password"]');
    if (pw) await pw.fill('');
    await page.keyboard.press('Enter');
    await wait(6000);
    return { inputs: info, url: page.url(), body: (await page.innerText('body')).slice(0, 200) };
  });

  await page.screenshot({ path: OUT + '/01-after-login.png' });

  // --- 2. the episode detail page -----------------------------------------
  await step('episode-page', async () => {
    await page.goto(BASE + '/web/#/details?id=' + IDS.ep1, { waitUntil: 'domcontentloaded', timeout: 60000 });
    await wait(9000);
    const text = (await page.innerText('body')).slice(0, 500);
    const btns = await page.$$eval('button, a', (els) =>
      els.map((e) => ((e.innerText || '') + '|' + (e.getAttribute('aria-label') || '') + '|' + (e.id || '')).trim())
         .filter((t) => t.length > 1).slice(0, 80));
    return { url: page.url(), text, buttons: btns };
  });

  await step('find-subsync-button', async () => {
    const found = await page.evaluate(() => {
      const out = [];
      for (const el of document.querySelectorAll('button, a, [role="menuitem"], .btnUserData')) {
        const s = (el.innerText || '') + ' ' + (el.getAttribute('aria-label') || '') + ' ' + (el.id || '') + ' ' + (el.className || '');
        if (/subsync|sync sub/i.test(s)) out.push({ tag: el.tagName, id: el.id, cls: String(el.className).slice(0, 80), txt: (el.innerText || '').slice(0, 60) });
      }
      return out;
    });
    return { matches: found };
  });

  await step('open-overflow-menu', async () => {
    // the item page puts plugin actions behind its ⋮ / more button
    const more = await page.$$('button[aria-label*="ore"], button[title*="ore"], .btnMoreCommands, button[aria-label*="More"], .detailButton-more');
    const info = { candidates: more.length };
    if (more.length) {
      await more[0].click();
      await wait(2500);
      await page.screenshot({ path: OUT + '/02-menu.png' });
      info.menu = await page.$$eval('.actionSheet, .menu, [role="menu"], .mdl-list, .actionSheetContent', (els) =>
        els.map((e) => (e.innerText || '').slice(0, 600)).filter(Boolean).slice(0, 4));
      info.items = await page.$$eval('[role="menuitem"], .actionSheetMenuItem, button, a', (els) =>
        els.map((e) => (e.innerText || '').trim()).filter((t) => t && t.length < 50).slice(0, 60));
    }
    return info;
  });

  await step('click-subsync', async () => {
    const clicked = await page.evaluate(() => {
      for (const el of document.querySelectorAll('button, a, [role="menuitem"]')) {
        const s = (el.innerText || '') + ' ' + (el.getAttribute('aria-label') || '');
        if (/sync subtitles/i.test(s)) { el.click(); return (el.innerText || '').trim().slice(0, 60); }
      }
      return null;
    });
    if (!clicked) return { clicked: false, hint: 'no "Sync Subtitles" entry found anywhere' };
    await wait(5000);
    await page.screenshot({ path: OUT + '/03-dialog.png' });
    const dialogText = await page.evaluate(() => {
      const d = document.querySelector('dialog, .dialog, .dialogContainer, [role="dialog"]');
      return d ? (d.innerText || '').slice(0, 1200) : null;
    });
    return { clicked, dialogText };
  });

  // --- 3. series / season page --------------------------------------------
  await step('series-page', async () => {
    await page.goto(BASE + '/web/#/details?id=' + IDS.series, { waitUntil: 'domcontentloaded', timeout: 60000 });
    await wait(9000);
    const found = await page.evaluate(() => {
      const out = [];
      for (const el of document.querySelectorAll('button, a')) {
        const s = (el.innerText || '') + ' ' + (el.getAttribute('aria-label') || '');
        if (/sync/i.test(s)) out.push((el.innerText || el.getAttribute('aria-label') || '').trim().slice(0, 60));
      }
      return out;
    });
    return { url: page.url(), syncish: found, text: (await page.innerText('body')).slice(0, 300) };
  });

  await page.screenshot({ path: OUT + '/04-series.png' });
  fs.writeFileSync(OUT + '/result.json', JSON.stringify(report, null, 1));
  await browser.close();
  console.log('DONE — wrote ' + OUT + '/result.json');
})();
