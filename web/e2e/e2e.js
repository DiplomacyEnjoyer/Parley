// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
// End-to-end check of the web client: three browser sessions play a turn by clicking.
const { chromium } = require('playwright');
const shot = process.argv[2];
(async () => {
  const browser = await chromium.launch();
  const base = 'http://localhost:5080';
  const errors = [];
  const mk = async () => { const c = await browser.newContext({ viewport: { width: 1500, height: 1000 } }); const p = await c.newPage(); p.on('pageerror', e => errors.push(String(e))); p.on('console', m => { if (m.type() === 'error') errors.push(m.text()); }); return p; };
  const host = await mk();
  await host.goto(base);
  await host.selectOption('#nMap', 'ring3');
  await host.fill('#nTitle', 'E2E test');
  await host.click('#nGo');
  await host.waitForSelector('#jName');
  const url = host.url();
  const pages = [host, await mk(), await mk()];
  const names = ['Ada', 'Bo', 'Cy'];
  for (let i = 0; i < 3; i++) {
    if (i) { await pages[i].goto(url); await pages[i].waitForSelector('#jName'); }
    await pages[i].fill('#jName', names[i]);
    await pages[i].click('#jGo');
    await pages[i].waitForSelector(`[data-seat="${i}"]`);
    await pages[i].click(`[data-seat="${i}"]`);
    await pages[i].waitForFunction(n => document.querySelector('#side').textContent.includes(n), names[i]);
  }
  await host.waitForFunction(() => document.querySelector('#side').textContent.includes('Cy'));
  await host.click('#hStart');
  await host.waitForSelector('#oLock');
  // click a province by id: use the map data in the page
  const tap = (page, id) => page.evaluate(id => { const i = M.provs.findIndex(p => p.id === id); document.querySelectorAll('#svg .hit')[i].dispatchEvent(new MouseEvent('click', { bubbles: true })); }, id);
  const act = (page, a) => page.click(`#actions [data-a="${a}"]`);
  // Ada: fleet Haven -> Bay; army Hold supports Gate -> Downs; army Gate -> Downs
  await tap(host, 'alp'); await act(host, 'move'); await tap(host, 'als');
  await host.waitForFunction(() => S.my.data.orders.some(o => o.text === 'F alp - als'));
  await tap(host, 'alg'); await act(host, 'move'); await tap(host, 'alm');
  await host.waitForFunction(() => S.my.data.orders.some(o => o.text === 'A alg - alm'));
  await tap(host, 'alc'); await act(host, 'sup'); await tap(host, 'alg'); await tap(host, 'alm');
  await host.waitForFunction(() => S.my.data.orders.some(o => o.text === 'A alc S alg - alm'));
  await host.screenshot({ path: shot + '-orders.png' });
  // secrecy: Bo's page must not contain Ada's orders anywhere
  const boSees = await pages[1].evaluate(() => JSON.stringify(S));
  if (boSees.includes('alp - als') || boSees.includes('alm')) errors.push('SECRECY: another player can see orders before resolution');
  await host.click('#oLock');
  await pages[1].waitForSelector('#oLock'); await pages[1].click('#oLock');
  await pages[2].waitForSelector('#oLock');
  await tap(pages[2], 'coc'); await act(pages[2], 'move'); await tap(pages[2], 'col');
  await pages[2].waitForFunction(() => S.my.data.orders.some(o => o.text === 'A coc - col'));
  await pages[2].click('#oLock');
  await host.waitForFunction(() => S.label === 'F1M', null, { timeout: 15000 });
  const state = await host.evaluate(() => ({ label: S.label, units: S.units.map(u => M.locs[u.l].id).sort().join(' '), last: last ? last.orders.map(o => o.text + ':' + o.res).join(' | ') : null, code: S.code }));
  console.log(JSON.stringify(state));
  await host.screenshot({ path: shot + '-after.png' });
  // history view + reload keeps identity
  await host.click('[data-h="0"]');
  await host.waitForSelector('#vBack');
  await host.reload(); await host.waitForSelector('#oLock');
  const me = await host.evaluate(() => S.me.name + ':' + S.me.power);
  console.log('after reload', me);
  // import the code as a new game
  const p4 = await mk(); await p4.goto(base);
  await p4.fill('#iText', state.code); await p4.click('#iGo'); await p4.waitForSelector('#jName');
  console.log('import ok', await p4.evaluate(() => S.label + ' stage ' + S.stage));
  console.log('errors', JSON.stringify(errors));
  await browser.close();
})().catch(e => { console.error('E2E FAILED', e); process.exit(1); });
