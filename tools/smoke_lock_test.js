// Browser-side smoke test for the weapon range.
//
// The range server launches one IR top-attack missile from the hill top every
// 10 s (backend/server/modes/range/threats.py) and logs a line whenever the
// smoke takes its lock away (backend/server/systems/projectiles.py, logger
// "mech.smoke").  This script closes the loop from inside the page: it waits
// for each launch, points the mech at the hill, pulls the smoke and reports
// whether the seeker went blind and whether the hull took the hit.
//
// Usage: open http://127.0.0.1:8011/, deploy, paste this file into the
// console, then watch it.  `__smokeTest.summary()` prints the tally, and
// `__smokeTest.stop()` ends the run.

(() => {
  const PULL_DELAY = 6000;    // ms after a launch before the smoke is pulled:
                              // late enough that the seeker has been tracking
  const WATCH = 14000;        // ms a single missile is followed for
  const PULL_EVERY = 3;       // pull on every third missile.  A screen lasts 5 s
                              // and is pulled 6 s after a launch, so it also
                              // covers the launch that follows: the run should
                              // read BLIND, BLIND (stale smoke), HIT (control)
  const WID_TOPATTACK = 5;

  const g = window.__game;
  if (!g || !g.net) {
    console.error('[smoke-test] open the game first (window.__game missing)');
    return;
  }

  const state = {
    running: true,
    launches: 0,
    blind: 0,          // the smoke broke the lock
    hit: 0,            // the round got through
    spent: 0,          // went off without damaging the hull
    rows: [],
    summary() {
      console.table(this.rows);
      console.log(`[smoke-test] launches ${this.launches} · lock broken by smoke ` +
                  `${this.blind} · hull hit ${this.hit} · spent ${this.spent}`);
      return this.rows;
    },
    stop() {
      this.running = false;
      console.log('[smoke-test] stopped');
    },
  };
  window.__smokeTest = state;

  const missiles = new Map();      // projectile id -> the run of one missile

  const hp = () => {
    const m = g.net.meta || {};
    return (m.hp || [0, 0, 0, 0]).reduce((a, b) => a + b, 0);
  };

  const aimAt = (x, z) => {
    const own = g.ownEntity();
    if (!own) return;
    g.input.yaw = Math.atan2(z - own.z, x - own.x);
    g.input.freeYaw = 0;
  };

  const pullSmoke = () => {
    // The render loop is what normally turns a key into an input message, and
    // it does not run while the tab is in the background, so the pull goes
    // straight onto the wire - byte for byte what game.js would have sent.
    g.net.send({
      t: 'in', yaw: g.input.yaw, pitch: g.input.pitch, mx: 0, mz: 0,
      fl: false, fr: false, z: g.zoom || 1,
      a: g.glCanvas.clientWidth / Math.max(1, g.glCanvas.clientHeight),
      q: true,
    });
    const m = g.net.meta || {};
    console.log(`[smoke-test] Q - smoke out (${m.smoke} pulls left, cd ${m.smoke_cd}s)`);
    // the rack holds three: top it up so a long run keeps the wall up
    if (!m.smoke) {
      g.net.send({ t: 'buy_smoke' });
      console.log('[smoke-test] rack empty - buying a refill (x10)');
    }
  };

  const finish = (m, verdict, note) => {
    missiles.delete(m.id);
    state.rows.push({ id: m.id, dist: m.dist, smoked: m.smoked, verdict, note,
                      hpBefore: m.hp, hpAfter: hp() });
    if (verdict === 'BLIND') state.blind += 1;
    else if (verdict === 'HIT') state.hit += 1;
    else state.spent += 1;
    console.log(`[smoke-test] missile ${m.id}: ${verdict} - ${note}`);
  };

  const baseFx = g._fx.bind(g);
  g._fx = (events) => {
    for (const ev of events) {
      if (ev.k === 'shot' && ev.wid === WID_TOPATTACK && ev.eid === -1 && state.running) {
        const own = g.ownEntity();
        const dist = own ? Math.hypot(ev.x - own.x, ev.z - own.z) : 0;
        state.launches += 1;
        const smoked = state.launches % PULL_EVERY === 1;
        const m = { id: ev.proj, dist: Math.round(dist), hp: hp(), smoked };
        missiles.set(m.id, m);
        console.log(`[smoke-test] launch ${m.id} from the hill, ${m.dist} m out` +
                    (smoked ? ' - smoke on the way' : ' - no smoke, control group'));
        if (smoked) {
          aimAt(ev.x, ev.z);          // the pull has to face the threat
          setTimeout(pullSmoke, PULL_DELAY);
        }
        setTimeout(() => {
          const live = missiles.get(m.id);
          if (!live) return;
          const gone = hp() < live.hp;
          finish(live, gone ? 'HIT' : 'NONE',
                 gone ? 'hit the hull' : 'spent, no damage');
        }, WATCH);
      }
      if (ev.k === 'boom' && missiles.has(ev.proj)) {
        // the round went off: close enough to the hull to count as a hit.
        // Damage is not a usable signal here, god mode is usually on
        const own = g.ownEntity();
        const d = own ? Math.hypot(ev.x - own.x, ev.z - own.z) : 999;
        const m = missiles.get(ev.proj);
        finish(m, d < 40 ? 'HIT' : 'NONE',
               d < 40 ? `detonated ${Math.round(d)} m from the hull` : 'fell wide');
      }
      if (ev.k === 'seeker' && missiles.has(ev.proj)) {
        finish(missiles.get(ev.proj), 'BLIND',
               `lock broken ${Math.round(ev.dist)} m from the hull`);
      }
    }
    return baseFx(events);
  };

  console.log('[smoke-test] running - one missile every ~10 s. ' +
              'Server log: grep "mech.smoke" /tmp/mech-range.log  (or .run/mech-range.log)');
  console.log('[smoke-test] __smokeTest.summary() for the tally, __smokeTest.stop() to end');
})();
