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
    if (m.lock_progress >= 1 && m.lock_target >= 0) continue;
    if (app.denyTimer > 0) continue;
    app.denyTimer = 0.7;
    app.audio.denied();
    const acquiring = m.lock_target >= 0;
    app.message(acquiring ? 'LOCKING - HOLD SIGHTS ON TARGET'
                          : 'NO LOCK - IR SEEKER HAS NO TARGET', '#ff9a9a');
  }
}

// The IR lock's own tone and the marker on the hull it holds.  Two states, and
// the transition is the tone: acquiring is a slow pip, locked is a solid sine,
// none is silence - what a pilot hears instead of reading `lock_progress`.
export function updateLockTone(app, own) {
  const m = app.net.meta;
  if (!own || !m || m.lock_target < 0) {
    app.audio.setLock('none', 0.016);
    app.lockInfo = null;
    return;
  }
  const locked = m.lock_progress >= 1;
  app.audio.setLock(locked ? 'locked' : 'acquiring', 0.016);
  const t = app.net.entities.get(m.lock_target);
  if (!t) { app.lockInfo = null; return; }
  app.lockInfo = {
    screen: app.hud._project(t.x, t.y + (t.type === 4 ? 3.5 : t.type === 5 ? 6 : 1), t.z),
    progress: m.lock_progress,
    dist: Math.hypot(t.x - own.x, t.z - own.z),
    target: t,
  };
}
