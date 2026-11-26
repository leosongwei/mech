import test from 'node:test';
import assert from 'node:assert/strict';

import { Lead } from '../../../frontend/src/app/systems/lead.js';
import { projectile, smokeAt, testApp, vehicle } from './support.mjs';

test('target layer works without ballistic arms and direct sight wins over link', () => {
  const friend = vehicle(2, 0, 100, { link: true });
  const direct = vehicle(3, 1, 200, { link: true });
  const linked = vehicle(4, 1, 300, { link: true });
  const old = vehicle(5, 1, 400, { link: false });
  const friendlyMissile = projectile(201, 0, 500, { link: true });
  const directMissile = projectile(202, 1, 600, { link: true });
  const linkedMissile = projectile(203, 1, 700, { link: true });
  const oldMissile = projectile(204, 1, 800, { link: false });
  const { app, own } = testApp(
    [friend, direct, linked, old],
    [friendlyMissile, directMissile, linkedMissile, oldMissile]);
  app.net.smoke = [smokeAt(300, 2, 0, 30, 3), smokeAt(400, 2, 0, 30, 3),
    smokeAt(700, 20, 0, 30, 3), smokeAt(800, 20, 0, 30, 3)];
  app.rwr.threats = []; // missile targets must come from the snapshot, not RWR
  const lead = new Lead(app);

  lead.update(own);
  assert.deepEqual(lead.world.map((t) => [t.id, t.kind, t.faction, t.source]), [
    [2, 'vehicle', 0, 'direct'],
    [3, 'vehicle', 1, 'direct'],
    [4, 'vehicle', 1, 'datalink'],
    [201, 'missile', 0, 'direct'],
    [202, 'missile', 1, 'direct'],
    [203, 'missile', 1, 'datalink'],
  ]);
  assert.ok(lead.world.every((t) => Array.isArray(t.marks) && t.marks.length === 0));

  app.aimAssist = true;
  lead.screen(own);
  assert.equal(app.aimSnap, null, 'friendly and armless targets must not snap');
  assert.deepEqual(app.leads.map((t) => [t.id, t.kind, t.faction, t.source]),
    lead.world.map((t) => [t.id, t.kind, t.faction, t.source]));
  assert.ok(app.leads.every((t) => t.marks.length === 0));
});

test('vehicle target boxes keep the 25 m - 4 km and nearest-four rules', () => {
  const entities = [vehicle(10, 1, 100), vehicle(11, 1, 200), vehicle(12, 1, 300),
    vehicle(13, 1, 400), vehicle(14, 1, 500), vehicle(15, 1, 600),
    vehicle(16, 1, 24), vehicle(17, 1, 4001)];
  const { app, own } = testApp(entities);
  const lead = new Lead(app);
  lead.update(own);
  assert.deepEqual(lead.world.map((t) => t.id), [10, 11, 12, 13]);
});

test('only enemy targets receive ballistic marks and datalink targets participate', () => {
  const friend = vehicle(2, 0, 100);
  const direct = vehicle(3, 1, 200);
  const linked = vehicle(4, 1, 300, { link: true });
  const { app, own } = testApp([friend, direct, linked]);
  own.weapon = [1, 1];
  app.defs.weapons[1] = { kind: 'bullet', vel: 500, g: 9.81, life: 10 };
  app.net.smoke = [smokeAt(300)];
  app.aimAssist = true;
  const lead = new Lead(app);

  lead.update(own);
  const byId = new Map(lead.world.map((t) => [t.id, t]));
  assert.equal(byId.get(2).marks.length, 0, 'friendly vehicle has no pre-aim marks');
  assert.equal(byId.get(3).marks.length, 2, 'direct enemy gets one mark per arm');
  assert.equal(byId.get(4).marks.length, 2, 'datalink enemy gets one mark per arm');

  lead.screen(own);
  assert.equal(app.aimSnap.id, 3, 'the direct enemy under the cross wins the snap');
  assert.notEqual(app.aimSnap.id, 2);
});

test('friendly vehicles and missiles use dashed outlines when only linked', () => {
  const friend = vehicle(2, 0, 100, { link: true });
  const missile = projectile(201, 0, 100, { link: true });
  const { app, own } = testApp([friend], [missile]);
  app.net.smoke = [smokeAt(50, 2, 0, 30, 30), smokeAt(50, 20, 0, 30, 30)];
  const lead = new Lead(app);

  lead.update(own);
  assert.deepEqual(lead.world.map((t) => [t.id, t.kind, t.faction, t.source]), [
    [2, 'vehicle', 0, 'datalink'],
    [201, 'missile', 0, 'datalink'],
  ]);
  assert.ok(lead.world.every((t) => t.marks.length === 0));
});
