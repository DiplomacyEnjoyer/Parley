// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
//
// Automated playtest: whole multi-player games through the web client, one browser per
// player, on several maps. Finds bugs before any human playtest.
//
//   dotnet build web/Parley.Web
//   node web/e2e/playtest.js [map ...] [--years N] [--shots DIR]
//
// Default maps: standard modern ancmed ring5 (run scripts/fetch-reference-maps.sh first for
// the first three). The script starts its own server on a free port with a throw-away save
// folder, and stops it at the end. Exit code 0 = every check passed.
//
// Each game walks through, in order:
//   ready-up; timer running out with partial sheets (one submitted, one entered but never
//   submitted, one toggled on/off/on, one empty); early finish when everyone submits; host
//   pause and resume; a player dropping (units hold, not waited for) and rejoining by
//   personal link; a cheating client that tries to open different orders than it sealed;
//   secrecy checks on everything the server sends and stores; the host dropping (host role
//   moves on); roll-back; resuming from a game code; the server losing the game and a
//   browser resuming it from its own copy; then random play through retreats and builds.
'use strict';
const { chromium } = require('playwright');
const { spawn } = require('child_process');
const crypto = require('crypto');
const fs = require('fs');
const os = require('os');
const path = require('path');
const net = require('net');

const argv = process.argv.slice(2);
const opt = (name, def) => { const i = argv.indexOf(name); if (i < 0) return def; const v = argv[i + 1]; argv.splice(i, 2); return v; };
const YEARS = +opt('--years', '2');
const SHOTS = opt('--shots', null);
const MAPS = argv.length ? argv : ['standard', 'modern', 'ancmed', 'ring5'];
const ROOT = path.resolve(__dirname, '..', '..');
const DLL = path.join(ROOT, 'web', 'Parley.Web', 'bin', 'Debug', 'net8.0', 'Parley.Web.dll');
const AWAY = 6;

const failures = [];
let checks = 0;
function check(ok, what) { checks++; if (!ok) { failures.push(what); console.log('  FAIL: ' + what); } return ok; }
const sleep = ms => new Promise(r => setTimeout(r, ms));
const log = s => console.log(new Date().toISOString().slice(11, 19) + ' ' + s);

// ------------------------------------------------------------------ server
let server = null, base = '', dataDir = '';
const freePort = () => new Promise(res => { const s = net.createServer(); s.listen(0, '127.0.0.1', () => { const p = s.address().port; s.close(() => res(p)); }); });
async function startServer(fresh) {
  if (fresh || !dataDir) dataDir = fs.mkdtempSync(path.join(os.tmpdir(), 'parley-playtest-'));
  const port = base ? +base.split(':').pop() : await freePort();
  base = 'http://127.0.0.1:' + port;
  server = spawn('dotnet', [DLL], { cwd: ROOT, env: Object.assign({}, process.env, { PARLEY_URL: base, PARLEY_DATA: dataDir, PARLEY_AWAY_SECONDS: String(AWAY) }), stdio: ['ignore', 'pipe', 'pipe'] });
  let out = '';
  server.stdout.on('data', d => out += d); server.stderr.on('data', d => out += d);
  for (let i = 0; i < 100; i++) { try { await fetch(base + '/api/maps'); return out; } catch { await sleep(200); } }
  throw new Error('server did not start: ' + out);
}
async function stopServer() { if (!server) return; server.kill(); await new Promise(r => server.once('exit', r)); server = null; }
async function api(p, body) {
  const r = await fetch(base + p, body === undefined ? {} : { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
  const t = await r.text(); let d; try { d = JSON.parse(t); } catch { d = t; }
  return { ok: r.ok, status: r.status, data: d };
}

// ------------------------------------------------------------------ browsers
let browser;
const pageErrors = [];
async function newPlayer(label) {
  const ctx = await browser.newContext({ viewport: { width: 1400, height: 950 } });
  const page = await ctx.newPage();
  page.on('pageerror', e => pageErrors.push(label + ': ' + e));
  page.on('console', m => { if (m.type() === 'error' && !/Failed to load resource/.test(m.text())) pageErrors.push(label + ': ' + m.text()); });
  return { ctx, page, label };
}
const state = p => p.page.evaluate(() => S);
async function waitFor(p, fn, arg, ms = 60000) {
  try { return await p.page.waitForFunction(fn, arg, { timeout: ms, polling: 250 }); }
  catch (e) {
    let info = '';
    try { info = await p.page.evaluate(() => JSON.stringify({ stage: S.stage, label: S.label, left: S.secondsLeft, my: S.my && { sub: S.my.submitted, sealed: S.my.sealedOrders, opened: S.my.opened, needs: S.my.needs }, seats: S.seats.map(x => [x.name, x.present ? 'here' : 'away', x.needs ? 'needs' : '', x.submitted ? 'sub' : '', x.opened ? 'open' : '', x.ready ? 'ready' : ''].filter(Boolean).join(' ')), flash })); } catch { }
    throw new Error(`${p.label}: timed out waiting for ${String(fn).slice(0, 80)} | page: ${info}`);
  }
}
async function joinAndSit(p, url, name, seat) {
  await p.page.goto(url); await p.page.waitForSelector('#jName');
  await p.page.fill('#jName', name); await p.page.click('#jGo');
  await p.page.waitForSelector(`[data-seat="${seat}"]`); await p.page.click(`[data-seat="${seat}"]`);
  await waitFor(p, s => S.me && S.me.power === s, seat);
  p.token = await p.page.evaluate(() => token); p.seat = seat; p.name = name;
}
async function tap(p, provId) {
  await p.page.evaluate(id => { const i = M.provs.findIndex(q => q.id === id); document.querySelectorAll('#svg .hit')[i].dispatchEvent(new MouseEvent('click', { bubbles: true })); }, provId);
}
// a random legal sheet, chosen from the options the page was given, entered through the page's own sheet functions
async function randomSheet(p, moveShare = 0.6) {
  return p.page.evaluate(share => {
    const pick = a => a[Math.floor(Math.random() * a.length)];
    const o = S.my && S.my.options; myOrders = [];
    if (!o || !S.my.needs) return 0;
    if (S.phase === 0) for (const u of o.units) {
      const r = Math.random();
      if (r < share && u.moves.length) { const m = pick(u.moves); myOrders.push({ k: 'm', u: u.l, a: m.l, b: -1, via: m.via }); }
      else if (r < share + 0.25 && u.sup.length) { const s = pick(u.sup); if (s.to.length) { const t = pick(s.to); myOrders.push(t < 0 ? { k: 's', u: u.l, a: s.p, b: -1 } : { k: 't', u: u.l, a: s.p, b: t }); } }
    }
    else if (S.phase === 1) for (const d of o.dis) { if (d.to.length && Math.random() < 0.8) myOrders.push({ k: 'r', u: d.l, a: pick(d.to), b: -1 }); }
    else if (S.my.delta > 0) { const used = new Set(); for (const b of o.builds.sort(() => Math.random() - .5)) { if (myOrders.length >= S.my.delta) break; const pv = M.locs[b.l].prov; if (used.has(pv)) continue; used.add(pv); myOrders.push({ k: b.t === 1 ? 'bf' : 'ba', u: -1, a: b.l, b: -1 }); } }
    else { for (const l of o.disbands.slice().sort(() => Math.random() - .5).slice(0, -S.my.delta)) myOrders.push({ k: 'd', u: -1, a: l, b: -1 }); }
    saveSheet(); render();
    return myOrders.length;
  }, moveShare);
}
async function toggleSubmit(p, on) {
  await p.page.waitForSelector('#oToggle');
  const cur = await p.page.evaluate(() => S.my.submitted);
  if (cur !== on) { await p.page.click('#oToggle'); await waitFor(p, v => !S.my || S.my.submitted === v || (S.stage !== 1 && S.stage !== 2), on, 15000); }
}
async function readyAll(players) {
  await waitFor(players[0], () => S.stage === 7, null, 90000);
  check((await state(players[0])).secondsLeft === -1, 'clock does not run before everyone is ready');
  for (const p of players) { await p.page.waitForSelector('#aReady'); const r = await p.page.evaluate(() => S.seats[S.me.power].ready); if (!r) await p.page.click('#aReady'); }
  for (const p of players) await waitFor(p, () => S.stage === 1 && S.my, null, 20000);
}
// waits for the phase that is open now to be resolved and the next one to be open (or ready-up)
async function nextPhase(p, ms = 120000) {
  const serial = (await state(p)).serial;
  await waitFor(p, s => S.serial !== s && (S.stage === 1 || S.stage === 7 || S.stage === 3 || S.stage === 2), serial, ms);
  return state(p);
}
async function lastResults(p) { const s = await state(p); return (await api(`/api/games/${s.id}/history/${s.historyCount - 2}`)).data; }

// ------------------------------------------------------------------ the cheating client: plays a seat through the API only
function sealOf(salt, power, key, text) { return crypto.createHash('sha256').update(`PS1|${salt}|${power}|${key}|${text}`, 'utf8').digest('hex'); }

// ------------------------------------------------------------------ secrecy: nothing the server sends or stores holds orders of the open phase
async function secrecyCheck(gid, players, sheets) {
  const sent = [];
  for (const p of players) if (p.token) sent.push(JSON.stringify((await api(`/api/games/${gid}/state?token=${p.token}`)).data));
  const archive = (await api(`/api/games/${gid}/archive`)).data;
  const saved = fs.readFileSync(path.join(dataDir, gid + '.json'), 'utf8');
  const openEntry = String(archive).split(';').slice(-2, -1)[0] || '';
  check(!openEntry.includes('+'), 'the archive holds no orders for the phase being played');
  for (const sheet of sheets) {
    if (!sheet || sheet.length < 5) continue;
    check(!sent.some(t => t.includes(sheet)), 'no state sent to any player contains a sheet before the reveal');
    check(!saved.includes(sheet), "the server's save file does not contain a sheet before the reveal");
  }
}

// ------------------------------------------------------------------ one whole game
async function playGame(mapId) {
  log(`=== ${mapId} ===`);
  const maps = (await api('/api/maps')).data;
  const info = maps.find(m => m.id === mapId);
  if (!check(!!info, `map ${mapId} is installed`)) return;
  const nBrowsers = Math.min(4, info.powers);
  const cheatSeat = info.powers > nBrowsers ? nBrowsers : -1;

  // the host creates the game through the home page
  const players = [];
  const host = await newPlayer(mapId + '/P0'); players.push(host);
  await host.page.goto(base);
  if (mapId === 'standard') check(await host.page.$eval('#nMap', s => s.value) === 'standard', 'the classic map is preselected');
  await host.page.selectOption('#nMap', mapId);
  await host.page.fill('#nTitle', 'Playtest ' + mapId);
  await host.page.fill('#nSched', '0.75, 0.5');          // 45 s for the first movement phase, then 30 s
  await host.page.fill('#nRet', '0.25'); await host.page.fill('#nAdj', '0.25');
  await host.page.selectOption('#nAnn', 'fast');
  await host.page.click('#nGo'); await host.page.waitForSelector('#jName');
  const url = host.page.url(); const gid = new URL(url).searchParams.get('g');
  await joinAndSit(host, url, 'Ada', 0);
  for (let i = 1; i < nBrowsers; i++) { const p = await newPlayer(`${mapId}/P${i}`); players.push(p); await joinAndSit(p, url, ['Bo', 'Cy', 'Dee'][i - 1], i); }
  let cheat = null;
  if (cheatSeat >= 0) {
    const j = await api(`/api/games/${gid}/join`, { name: 'Mallory' });
    cheat = { token: j.data.token, seat: cheatSeat };
    check((await api(`/api/games/${gid}/seat`, { token: cheat.token, power: cheatSeat })).ok, 'API-only player takes a seat');
  }
  // keep the API player "present"
  let cheatAlive = true;
  const cheatPoll = (async () => { while (cheatAlive && cheat) { await api(`/api/games/${gid}/state?token=${cheat.token}`).catch(() => { }); await sleep(1500); } })();
  const cheatState = async () => (await api(`/api/games/${gid}/state?token=${cheat.token}`)).data;
  const cheatReady = async () => { if (cheat) await api(`/api/games/${gid}/ready`, { token: cheat.token, value: true }); };

  await host.page.click('#hStart');
  await waitFor(host, () => S.stage === 7);
  await cheatReady();
  await readyAll(players);
  let s = await state(host);
  check(Math.abs(s.secondsLeft - 45) < 4, 'first movement phase gets the first schedule entry (45 s): ' + s.secondsLeft);

  // ---- phase 1: timer runs out with partial sheets
  log('timer expiry with partial sheets');
  const unitsBefore = s.units.map(u => u.p + ':' + u.l).sort().join(' ');
  const n1 = await randomSheet(players[0], 1); const sheet0 = await players[0].page.evaluate(() => encodeSheet(myOrders, S.my.power)); await toggleSubmit(players[0], true);           // submitted
  const n2 = nBrowsers > 1 ? await randomSheet(players[1], 1) : 0;                            // entered, never submitted
  const sheet2 = nBrowsers > 1 ? await players[1].page.evaluate(() => encodeSheet(myOrders, S.my.power)) : '';
  if (nBrowsers > 2) {                                                                         // toggled on, off, edited, on
    await randomSheet(players[2], 1); await toggleSubmit(players[2], true);
    check(!(await players[2].page.evaluate(() => canEdit())), 'a submitted sheet cannot be edited');
    await toggleSubmit(players[2], false);
    check(await players[2].page.evaluate(() => canEdit()), 'switching submit off allows editing again');
    await randomSheet(players[2], 0.3); await toggleSubmit(players[2], true);
  }
  let cheatSheet = '';
  if (cheat) { // the cheat seals an empty sheet now and will try to open a different one
    const cs = await cheatState(); const salt = crypto.randomBytes(16).toString('hex');
    cheat.salt = salt; cheat.text = '';
    check((await api(`/api/games/${gid}/seal`, { token: cheat.token, sealKey: cs.sealKey, seal: sealOf(salt, cheat.seat, cs.sealKey, '') })).ok, 'API player seals');
    check(!(await api(`/api/games/${gid}/seal`, { token: cheat.token, sealKey: cs.sealKey, seal: 'f'.repeat(64) })).ok, 'a second seal while submitted is refused');
    check(!(await api(`/api/games/${gid}/seal`, { token: cheat.token, sealKey: 'stale', seal: 'f'.repeat(64) })).ok, 'a seal for another phase is refused');
  }
  check((await state(host)).stage === 1, 'still taking orders when the secrecy check runs');
  await secrecyCheck(gid, players, [sheet2, sheet0]);
  if (SHOTS) await host.page.screenshot({ path: path.join(SHOTS, mapId + '-orders.png') });
  // the cheat waits for the reveal and tries to swap its (empty) sheet for real moves
  if (cheat) {
    const cs = await cheatState();
    const u = cs.units.find(x => x.p === cheat.seat);
    const enc = v => '0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz'[Math.floor(v / 62)] + '0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz'[v % 62];
    cheatSheet = u ? enc(u.l) + 'm' + enc(u.l) : '';
    let t0 = Date.now();
    while (Date.now() - t0 < 60000 && (await cheatState()).stage !== 5) await sleep(150);
    check((await cheatState()).stage === 5, 'the phase reaches the opening stage');
    const bad = await api(`/api/games/${gid}/open`, { token: cheat.token, salt: cheat.salt, orders: cheatSheet });
    check(!bad.ok && /do not match/.test(bad.data.error || ''), 'opening different orders than were sealed is refused');
    const bad2 = await api(`/api/games/${gid}/open`, { token: cheat.token, salt: crypto.randomBytes(16).toString('hex'), orders: '' });
    check(!bad2.ok, 'opening with a different salt is refused');
    check((await api(`/api/games/${gid}/open`, { token: cheat.token, salt: cheat.salt, orders: '' })).ok, 'opening the sealed sheet is accepted');
  }
  s = await nextPhase(host);
  const r1 = await lastResults(host);
  check(r1 && r1.label.endsWith('M'), 'the first movement phase was resolved');
  if (nBrowsers > 1 && n2 > 0) check(r1.orders.some(o => o.p === 1 && !/ H$| holds/.test(o.text) && o.k !== 0), 'an entered but never submitted sheet became orders at zero');
  if (nBrowsers > 3) check(r1.orders.filter(o => o.p === 3).every(o => o.k === 0), 'a player with nothing entered: every unit held');
  check(n1 >= 0, 'sheet entered');

  // ---- phase 2: pause and resume, then early finish
  if (s.stage === 7) { await cheatReady(); await readyAll(players); }
  s = await state(host);
  if (s.phase === 0 && s.stage === 1) {
    log('pause, resume, early finish');
    check(Math.abs(s.secondsLeft - 30) < 4, 'second movement phase gets the second schedule entry (30 s): ' + s.secondsLeft);
    await host.page.click('#hPause'); await waitFor(host, () => S.stage === 2);
    const a = (await state(host)).secondsLeft; await sleep(3000); const b = (await state(host)).secondsLeft;
    check(a === b && a > 0, 'the clock does not move while paused');
    for (const p of players) { await randomSheet(p); await toggleSubmit(p, true); }   // sheets still work while paused
    if (cheat) { const cs = await cheatState(); cheat.salt = crypto.randomBytes(16).toString('hex'); await api(`/api/games/${gid}/seal`, { token: cheat.token, sealKey: cs.sealKey, seal: sealOf(cheat.salt, cheat.seat, cs.sealKey, '') }); }
    await sleep(2000);
    check((await state(host)).stage === 2, 'nothing ends while paused, not even with everyone submitted');
    // the host lets the cheat's honest opening happen automatically from here
    await host.page.click('#hResume');
    await waitFor(host, () => S.stage !== 2, null, 10000);
    const left = (await state(host)).secondsLeft;
    if (cheat) openWhenRevealed(gid, cheat);
    await waitFor(host, () => S.stage !== 1, null, 30000);
    const after = await state(host);
    check(after.stage !== 1 && after.closeReason === 1, `everyone submitted: the phase ended early (reason ${after.closeReason}, ${left}s were left)`);
    s = await nextPhase(host, 180000);
  }

  // ---- a player drops and comes back
  if (nBrowsers >= 3) {
    if (s.stage === 7) { await cheatReady(); await readyAll(players); s = await state(host); }
    log('player drops and rejoins');
    const gone = players[2];
    const posBefore = s.units.filter(u => u.p === gone.seat).map(u => u.l).sort().join();
    await gone.ctx.close();
    await waitFor(host, seat => !S.seats[seat].present, gone.seat, (AWAY + 10) * 1000);
    check(true, 'a player who left is shown as away');
    for (const p of players.filter(x => x !== gone)) { await randomSheet(p); if ((await state(p)).my && (await state(p)).my.needs) await toggleSubmit(p, true); }
    if (cheat) await sealHonest(gid, cheat);
    s = await nextPhase(host, 180000);
    const res = await lastResults(host);
    if (res.label.endsWith('M') && res.orders.some(o => o.p === gone.seat)) {
      check(res.orders.filter(o => o.p === gone.seat).every(o => o.k === 0), 'away player: every unit held');
      const st = (await state(host));
      check(st.announce && st.announce.status[gone.seat] !== '', 'the announcement says the away player sent no orders');
    }
    // back through the personal link, in a fresh browser
    const back = await newPlayer(mapId + '/P2-back');
    await back.page.goto(`${url}&t=${gone.token}`);
    await waitFor(back, () => S && S.me && S.me.power >= 0, null, 20000);
    Object.assign(back, { token: gone.token, seat: gone.seat, name: gone.name });
    players[2] = back;
    await waitFor(host, seat => S.seats[seat].present, back.seat, 20000);
    check(true, 'the player is back in their seat after rejoining');
    void posBefore;
  }

  // ---- the host drops: the host role moves on
  if (nBrowsers >= 2) {
    log('host drops');
    s = await state(players[1]);
    await host.ctx.close();
    await waitFor(players[1], () => S.isHost, null, (AWAY + 15) * 1000).catch(() => { });
    s = await state(players[1]);
    check(s.isHost && s.host === players[1].name, 'the host role passed to another player: ' + s.host);
    players.shift();
  }
  const H = players[0];

  // ---- roll-back by the (new) host
  s = await state(H);
  if (s.historyCount >= 3) {
    log('roll-back');
    const target = 1;
    const label = (await api(`/api/games/${gid}/history/${target}`)).data.label;
    await H.page.click(`[data-h="${target}"]`); await H.page.waitForSelector('#vRoll');
    H.page.once('dialog', d => d.accept());
    await H.page.click('#vRoll');
    await waitFor(H, l => S.label === l && S.stage === 2, label, 20000);
    s = await state(H);
    check(s.historyCount === target + 1, 'roll-back discards the later phases');
    await H.page.click('#hResume'); await waitFor(H, () => S.stage === 1);
  }

  // ---- resume from a game code in a new game
  {
    log('resume from a game code');
    s = await state(H);
    const p = await newPlayer(mapId + '/importer');
    await p.page.goto(base); await p.page.fill('#iText', s.code); await p.page.click('#iGo');
    await p.page.waitForSelector('#jName');
    const t = await state(p);
    check(t.label === s.label && t.stage === 2 && t.code === s.code, 'a game code resumes the exact position, paused');
    await p.ctx.close();
  }

  // ---- random play through retreats and builds, faster announcement
  log('random play');
  await H.page.evaluate(() => { $('#tN').value = 'off'; $('#tS').value = '0.5'; $('#tR').value = '0.3'; $('#tA').value = '0.3'; });
  await H.page.click('#hTimers');
  const seen = new Set();
  const startYear = (await state(H)).year;
  for (let step = 0; step < 40; step++) {
    s = await state(H);
    if (s.stage === 3 || s.year >= startYear + YEARS) break;
    if (s.stage === 7) { await cheatReady(); await readyAll(players); continue; }
    if (s.stage === 2) { await H.page.click('#hResume').catch(() => { }); await sleep(500); continue; }
    if (s.stage !== 1) { await sleep(500); continue; }
    seen.add(s.label.slice(-1));
    for (const p of players) { const st = await state(p); if (st.my && st.my.needs && !st.my.submitted) { await randomSheet(p); if (Math.random() < 0.8) await toggleSubmit(p, true); } }
    if (cheat) await sealHonest(gid, cheat);
    await nextPhase(H, 90000);
  }
  log('phase types played in random play: ' + [...seen].join(''));

  // ---- the server loses the game; a browser still has it
  log('server loses the game; resume from a browser copy');
  s = await state(H);
  cheatAlive = false; await cheatPoll;
  await stopServer();
  await startServer(true);                       // fresh, empty save folder
  await H.page.goto(url);
  await H.page.waitForSelector('#kResume', { timeout: 20000 });
  await H.page.click('#kResume');
  await H.page.waitForSelector('#jName', { timeout: 20000 });
  const back = await state(H);
  check(back.label === s.label, `the browser's own copy resumed the game at ${back.label} (was ${s.label})`);
  check(back.historyCount === s.historyCount, 'with its whole history');
  if (SHOTS) await H.page.screenshot({ path: path.join(SHOTS, mapId + '-resumed.png') });
  for (const p of players) await p.ctx.close().catch(() => { });
}

async function sealHonest(gid, cheat) {
  const cs = (await api(`/api/games/${gid}/state?token=${cheat.token}`)).data;
  if (!(cs.my && cs.my.needs && !cs.my.submitted && (cs.stage === 1 || cs.stage === 2))) return;
  cheat.salt = crypto.randomBytes(16).toString('hex');
  await api(`/api/games/${gid}/seal`, { token: cheat.token, sealKey: cs.sealKey, seal: sealOf(cheat.salt, cheat.seat, cs.sealKey, '') });
  openWhenRevealed(gid, cheat);
}
function openWhenRevealed(gid, cheat) {
  (async () => {
    for (let i = 0; i < 400; i++) {
      const cs = (await api(`/api/games/${gid}/state?token=${cheat.token}`)).data;
      if (cs.stage === 5) { if (cs.my && cs.my.sealedOrders && !cs.my.opened) await api(`/api/games/${gid}/open`, { token: cheat.token, salt: cheat.salt, orders: '' }); return; }
      if (cs.stage === 6 || cs.stage === 7) return;
      await sleep(250);
    }
  })();
}

(async () => {
  if (!fs.existsSync(DLL)) { console.error('build first: dotnet build web/Parley.Web'); process.exit(2); }
  if (SHOTS) fs.mkdirSync(SHOTS, { recursive: true });
  await startServer(true);
  browser = await chromium.launch();
  for (const m of MAPS) {
    try { await playGame(m); }
    catch (e) { check(false, `${m}: ${e.message.split('\n')[0]}`); }
    if (!server) await startServer(true);
  }
  await browser.close();
  await stopServer();
  for (const e of pageErrors) check(false, 'page error: ' + e);
  console.log(`\n${checks} checks, ${failures.length} failed`);
  if (failures.length) { console.log(failures.map(f => ' - ' + f).join('\n')); process.exit(1); }
})().catch(async e => { console.error('PLAYTEST CRASHED', e); await stopServer(); process.exit(1); });
