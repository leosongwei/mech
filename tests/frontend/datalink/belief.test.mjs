import test from 'node:test';
import assert from 'node:assert/strict';

import { Beliefs } from '../../../frontend/src/app/systems/belief.js';
import { testApp, vehicle } from './support.mjs';

test('tracked belief survives a missing row, walks, resumes and releases', () => {
  const target = vehicle(2, 1, 100, { vx: 10 });
  const { app, own } = testApp([target]);
  app.net.meta.lock_target = 2;
  const beliefs = new Beliefs();

  beliefs.update(app, own, 0.1);
  assert.equal(beliefs.get(2).seen, true);
  assert.equal(beliefs.get(2).x, 100);

  app.map.heightAt = () => 100; // direct line is blocked
  target.x = 120;
  target.px = 120;
  target.link = true;
  target.vx = 20;
  beliefs.update(app, own, 0.1);
  assert.equal(beliefs.get(2).seen, true, 'datalink refreshes a blocked target');
  assert.equal(beliefs.get(2).x, 120);
  assert.equal(beliefs.get(2).vx, 20);

  app.net.entities.delete(2);
  beliefs.update(app, own, 0.5);
  const blind = beliefs.get(2);
  assert.ok(blind, 'a tracked id must keep its belief when the row disappears');
  assert.equal(blind.seen, false);
  assert.equal(blind.x, 130, 'the missing row walks at its last velocity');

  target.x = 200;
  target.px = 200;
  target.link = false;
  app.net.entities.set(2, target);
  app.map.heightAt = () => 0;
  beliefs.update(app, own, 0.1);
  assert.equal(beliefs.get(2).seen, true);
  assert.equal(beliefs.get(2).x, 200, 'direct recovery snaps back to the true row');

  app.net.meta.lock_target = -1;
  beliefs.update(app, own, 0.1);
  assert.equal(beliefs.get(2), null, 'server release removes the belief');
});
