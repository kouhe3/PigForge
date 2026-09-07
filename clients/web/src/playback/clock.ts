export interface PlaybackClock {
  playing: boolean;
  speed: number;
  tick: number;
  maxTick: number;
  play(): void;
  pause(): void;
  step(delta: number): void;
  seek(tick: number): void;
  dispose(): void;
}

export function createPlaybackClock(
  maxTick: number,
  tickRate: number,
  onTick: (tick: number) => void,
): PlaybackClock {
  let playing = false;
  let speed = 1;
  let tick = 1;
  let handle = 0;
  let last = 0;
  let accum = 0;

  const frame = (now: number): void => {
    if (!playing) {
      return;
    }
    if (last === 0) {
      last = now;
    }
    accum += (now - last) * speed;
    last = now;
    const stepMs = 1000 / tickRate;
    while (accum >= stepMs) {
      accum -= stepMs;
      if (tick < maxTick) {
        tick += 1;
        onTick(tick);
      } else {
        playing = false;
        break;
      }
    }
    if (playing) {
      handle = requestAnimationFrame(frame);
    }
  };

  return {
    get playing() {
      return playing;
    },
    get speed() {
      return speed;
    },
    set speed(value: number) {
      speed = value;
    },
    get tick() {
      return tick;
    },
    get maxTick() {
      return maxTick;
    },
    play() {
      if (playing) {
        return;
      }
      playing = true;
      last = 0;
      handle = requestAnimationFrame(frame);
    },
    pause() {
      playing = false;
      cancelAnimationFrame(handle);
    },
    step(delta: number) {
      tick = Math.min(maxTick, Math.max(1, tick + delta));
      onTick(tick);
    },
    seek(next: number) {
      tick = Math.min(maxTick, Math.max(1, next));
      onTick(tick);
    },
    dispose() {
      playing = false;
      cancelAnimationFrame(handle);
    },
  };
}
