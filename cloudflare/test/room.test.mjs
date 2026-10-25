import {test} from 'node:test';
import assert from 'node:assert/strict';
import {RoomState} from '../src/room.js';
const pilot = (id, seen = 1000) => ({id, name: id, seen, readyEpoch: 0, ack: 0});
test('judge can start without pilots and readiness stays informational', () => {
  const room = new RoomState();
  room.control({action:'start'}, [], 1000);
  assert.equal(room.startAt, 7000); assert.equal(room.readyEpoch, 1);
  room.control({action:'cancel'}, [], 1001);
  room.control({action:'start'}, [{...pilot('a'),readyEpoch:-1,ack:-1},pilot('b')], 2000);
  assert.equal(room.snapshot([], 'late', 2100).seated, false);
  assert.equal(room.snapshot([], 'a', 2100).seated, true);
});
test('cancel invalidates readiness and persists across restoration', () => {
  const room = new RoomState(); room.control({action:'start'}, [pilot('a')], 1000);
  const restored = new RoomState(JSON.parse(JSON.stringify(room)));
  assert.equal(restored.startAt, room.startAt);
  restored.control({action:'cancel'}, [], 1100);
  assert.equal(restored.startAt, null); assert.equal(restored.seats.length, 0);
  restored.control({action:'start'}, [pilot('a')], 1200);
  assert.equal(restored.startAt, 7200);
});
test('invalid countdown cannot mutate state; waiting seats never include new connections', () => {
  const room = new RoomState();
  for(const delayMs of [0,999,60001,'6000',NaN]) assert.throws(() => room.control({action:'start',delayMs}, [pilot('a')], 1000));
  assert.equal(room.version, 0);
  room.control({action:'start',delayMs:10000}, [pilot('a')], 1000);
  assert.equal(room.startAt,11000); assert.equal(room.snapshot([], 'a-reconnected', 1200).seated,false);
});
