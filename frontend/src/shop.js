// Client half of the rearm shop: the draft loadout a pilot edits in the panel
// (B) and the price preview that goes with it.
//
// This mirrors backend/server/shop.py line for line - the server prices the
// draft again when CONFIRM is pressed and that bill is final, but the two must
// agree or the panel would show one number and charge another.  Both round
// money with floor(v + 0.5).

const ARM_NAMES = ['L-ARM', 'R-ARM'];

export function emptyDraft() {
  return { arms: [null, null], rearm: [false, false], repair: false, smoke: false };
}

export function isEmpty(draft) {
  if (!draft) return true;
  return draft.arms[0] === null && draft.arms[1] === null
    && !draft.rearm[0] && !draft.rearm[1] && !draft.repair && !draft.smoke;
}

const r = (v) => Math.floor(v + 0.5);

// Defence for a draft that came out of a stale panel (an arm was shot off, the
// mech died): both sides clamp to what is actually there.
function armState(meta, arm) {
  const arms = (meta && meta.arms) || [];
  const a = arms[arm];
  if (!a) return { wid: 0, hp: 1, hpmax: 1, mag: 0, reserve: 0, rounds: 0 };
  return { wid: a.wid, hp: a.hp, hpmax: Math.max(1, a.hpmax), mag: a.mag,
           reserve: a.reserve, rounds: a.mag + a.reserve };
}

/**
 * Price a draft the way the server will.
 *
 * `dur` is the work it books, in seconds - a job holds the mech still and
 * silent while it runs (server `systems/rearm.py`), so the panel shows it next
 * to the bill.
 *
 * @param {{weapons:object, buyable:number[], rearm:object}} cfg  from init
 * @param {object} meta  the live meta payload (money, arms, smoke, smoke_cd)
 * @param {object} draft from emptyDraft()
 * @returns {{lines:{text:string,delta:number,dur:number}[], total:number,
 *            dur:number, ok:boolean}}
 */
export function quote(cfg, meta, draft) {
  const weapons = (cfg && cfg.weapons) || {};
  const prices = (cfg && cfg.rearm) || {};
  const refund = prices.sell_refund === undefined ? 0.5 : prices.sell_refund;
  const perHp = prices.repair_cost === undefined ? 1 : prices.repair_cost;
  const smokeCost = prices.smoke_cost === undefined ? 250 : prices.smoke_cost;
  const smokeFull = prices.smoke_charges === undefined ? 10 : prices.smoke_charges;
  // seconds at the wrench: a fit 2 s, a full load of rounds 4 s per arm,
  // armour 2 s per 100 hp
  const swapS = prices.swap_s === undefined ? 2 : prices.swap_s;
  const ammoS = prices.ammo_s === undefined ? 4 : prices.ammo_s;
  const repairS = prices.repair_s === undefined ? 2 : prices.repair_s;

  const lines = [];
  let total = 0;
  let dur = 0;

  for (let arm = 0; arm < 2; arm++) {
    const cur = armState(meta, arm);
    const wid = draft.arms[arm];
    if (wid === null || wid === undefined || wid === cur.wid) continue;
    const nwd = weapons[wid];
    const cwd = weapons[cur.wid];
    if (!nwd || !cwd) continue;
    const hardware = r(cwd.cost * refund * (cur.hp / cur.hpmax));
    const rounds = r(cur.rounds * (cwd.ammo_cost || 0) * refund);
    const load = r((nwd.mag + nwd.reserve) * (nwd.ammo_cost || 0));
    const delta = nwd.cost + load - hardware - rounds;
    // a weapon change is a fit *and* the racks it brings
    const d = swapS + ammoS;
    total += delta;
    dur += d;
    lines.push({
      text: `${ARM_NAMES[arm]}: ${cwd.name} (${cur.rounds}) -> ${nwd.name}`,
      delta,
      dur: d,
    });
  }

  for (let arm = 0; arm < 2; arm++) {
    if (draft.arms[arm] !== null && draft.arms[arm] !== undefined) continue;
    if (!draft.rearm[arm]) continue;
    const cur = armState(meta, arm);
    const wd = weapons[cur.wid];
    if (!wd) continue;
    const missing = (wd.mag + wd.reserve) - cur.rounds;
    if (missing <= 0) continue;
    const delta = r(missing * (wd.ammo_cost || 0));
    total += delta;
    dur += ammoS;
    lines.push({ text: `${ARM_NAMES[arm]}: ${wd.name} REARM ${missing} rds`, delta, dur: ammoS });
  }

  if (draft.repair && meta) {
    const hp = meta.hp || [];
    const max = (cfg && cfg.mech && cfg.mech.hp) || [0, 0, 0, 0];
    let need = 0;
    for (let i = 0; i < 4; i++) need += Math.max(0, (max[i] || 0) - (hp[i] || 0));
    const cost = r(need * perHp);
    if (cost > 0) {
      const d = (need * repairS) / 100;
      total += cost;
      dur += d;
      lines.push({ text: `REPAIR ${Math.round(need)} hp`, delta: cost, dur: d });
    }
  }

  if (draft.smoke && meta) {
    const charges = meta.smoke === undefined || meta.smoke === null ? smokeFull : meta.smoke;
    if (charges < smokeFull || (meta.smoke_cd || 0) > 0) {
      total += smokeCost;
      lines.push({ text: `REARM SMOKE x${smokeFull}`, delta: smokeCost, dur: 0 });
    }
  }

  return { lines, total, dur, ok: lines.length > 0 && total <= (meta ? meta.money : 0) };
}

/**
 * The draft behind the REPAIR & REARM button: weld every module back to full
 * and top both arms up, nothing else - no weapon change (which would cost a
 * trade-in and a fit) and no smoke rack.
 */
export function quickDraft() {
  const d = emptyDraft();
  d.rearm = [true, true];
  d.repair = true;
  return d;
}

// The message the server expects: -1 for "leave this arm alone".
export function toMessage(draft) {
  return {
    t: 'shop',
    arms: [draft.arms[0] === null ? -1 : draft.arms[0],
           draft.arms[1] === null ? -1 : draft.arms[1]],
    rearm: [!!draft.rearm[0], !!draft.rearm[1]],
    repair: !!draft.repair,
    smoke: !!draft.smoke,
  };
}
