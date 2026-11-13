// The voice budget: how many sounds the mix may hold at once, and which one it
// turns away when it cannot hold another.
//
// A slot is held for as long as a voice sounds, so the ceiling is on the
// simultaneous count rather than on the rate - which is the shape a battle has:
// dozens of short cracks a second is fine, a dozen two second rumbles at once is
// not.  A voice that is turned away here opens no node at all, which is the point
// of deciding before the chain is built rather than after.
//
// It keeps its own clock: the slot list is a list of the times each live voice
// starts being free again, pruned against the audio clock as it is read, so
// nothing has to be told when a voice ended.

import { MAX_VOICES, STEAL_VOL } from './levels.js';

export class Voices {
  constructor(audio) {
    this.a = audio;      // the facade; `a.ctx` is the audio clock
    this.end = [];       // when each live voice starts being free
  }

  // Room for one more event?  `vol` is the level it will play at and `dur` how
  // long it holds its slot.
  admit(vol, dur) {
    const now = this.a.ctx.currentTime;
    const end = this.end;
    let n = 0;
    for (let i = 0; i < end.length; i++) if (end[i] > now) end[n++] = end[i];
    end.length = n;
    if (n >= MAX_VOICES && vol < STEAL_VOL) return false;
    end.push(now + dur);
    return true;
  }

  // A dead context holds no voices: the list is stale the moment the graph is
  // torn down, and the clock it is measured against is gone with it.
  clear() {
    this.end.length = 0;
  }
}
