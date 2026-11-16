// The two pieces of feedback that watch the machine itself rather than the world:
// the red rim when it takes damage, and the refusal when a seeker has nothing to
// lock on.
//
// Both run every frame off the pilot's own hull, and both are silent about their
// own timing: the flash is decayed by the frame, the refusal is rate limited here.

// taking a hit is shown, never sounded: the red rim flash is the whole of the
// feedback (a tone for every round that lands is a tone per second under fire)
export function checkDamage(app, own) {
  if (!own) { app.lastHpSum = -1; return; }
  const sum = own.hp[0] + own.hp[1] + own.hp[2] + own.hp[3];
  if (app.lastHpSum >= 0 && sum < app.lastHpSum) {
    app.hitFlash = Math.min(1, app.hitFlash + 0.55);
  }
  app.lastHpSum = sum;
}

// an IR missile cannot leave the tube without a solid lock: tell the pilot
// instead of silently swallowing the trigger
export function noLockFeedback(app, own, dt) {
  if (!own) return;
  app.denyTimer = (app.denyTimer || 0) - dt;
  const fire = [app.input.fireL, app.input.fireR];
  for (let arm = 0; arm < 2; arm++) {
    if (!fire[arm]) continue;
    const wd = app.defs.weapons[own.weapon[arm]];
    if (!wd || !wd.lock) continue;
    const m = app.net.meta || {};
    if (m.lock_target >= 0) continue;
    if (app.denyTimer > 0) continue;
    app.denyTimer = 0.7;
    app.audio.denied();
    const beam = wd.kind === 'saclos';
    app.message(beam ? 'NO LOCK - LASER HAS NO TARGET'
                     : 'NO LOCK - IR SEEKER HAS NO TARGET', '#ff9a9a');
  }
}

// The lock's own tone, and the marker on the hull it holds.
//
// The *edge* is the tone: a lock is taken in the tick the circle arrives on the
// hull (`Systems/Locking.cs`), so what the ear hears is the chime, and then
// silence while it is held.  A solid sine for as long as a channel is held is a
// machine that never stops talking - with four of them it is a drone under the
// whole battle - so what says a lock is still there is the marker drawn on the
// hull (in the reticle's own colour) until it dies or the channel goes.
const LOCK_TONE_S = 1.0;

export function updateLockTone(app, own, dt) {
  const m = app.net.meta;
  if (!own || !m || m.lock_target < 0) {
    app.audio.setLock('none', 0.016);
    app.lockInfo = null;
    app.lockExtra = null;
    app.lockRung = -1;
    app.lockRingT = 0;
    return;
  }
  const id = m.lock_target;
  // chimed once per hull: the edge is what rings, so a held lock sits silent
  if (app.lockRung !== id) { app.lockRung = id; app.lockRingT = LOCK_TONE_S; }
  app.lockRingT = Math.max(0, (app.lockRingT || 0) - dt);
  app.audio.setLock(app.lockRingT > 0 ? 'locked' : 'none', dt);
  const t = app.net.entities.get(m.lock_target);
  app.lockExtra = lockExtras(app, m);
  if (!t) { app.lockInfo = null; return; }
  const thin = !!(app.lockExtra && app.lockExtra.some((e) => e.id === id));
  app.lockInfo = {
    screen: app.hud._project(t.x, t.y + (t.type === 4 ? 3.5 : t.type === 5 ? 6 : 1), t.z),
    dist: Math.hypot(t.x - own.x, t.z - own.z),
    target: t,
    // the candidate is already a formal target (a shot claimed its line), so it is
    // drawn as one: the thin diamond, not the bold one
    thin,
  };
}

// The lines the designator is holding: the formal targets, one thin diamond each.  A
// beam rider carries four channels and drops one the moment its hull leaves the
// sight's window (`Systems/Locking.cs`), so the pilot who wants to know what his
// machine is still tracking reads this.  The candidate is in here too when a shot
// has already claimed a line for it - it is a formal target then, and `lockInfo`
// says so (`thin`), which is what stops it being drawn twice.
function lockExtras(app, m) {
  const list = m.locks;
  if (!list || !list.length) return null;
  const out = [];
  for (const l of list) {
    const t = app.net.entities.get(l.id);
    if (!t) continue;
    const screen = app.hud._project(t.x, t.y + (t.type === 4 ? 3.5 : t.type === 5 ? 6 : 1), t.z);
    if (!screen) continue;
    out.push({ id: l.id, screen, target: t });
  }
  return out.length ? out : null;
}
