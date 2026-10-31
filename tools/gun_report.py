#!/usr/bin/env python
"""What a real gun's report actually looks like, in numbers.

The 105 mm cue in `frontend/src/audio.js` (`HEAVY_REPORT`) is not tuned by ear -
it is the measurement this tool prints, layer for layer.  Point it at a
recording (a video works, ffmpeg pulls the audio out) and give it the times of
the shots; it then reports, per shot:

  * the third-octave spectrum in five windows (0-60 ms .. 1.2-2.5 s), which is
    *what* arrives when - the crack is broadband, the boom is 110-250 Hz and
    arrives tens of milliseconds later, the tail is 400-800 Hz;
  * the total envelope in dB every 50 ms out to 2.5 s, i.e. how long the thing
    is audible at all;
  * per band, when it peaks and when it is 6 and 20 dB down, which is the
    attack/decay a layer has to be given.

`--synth` renders the same table for the cue as `HEAVY_REPORT` describes it, so
the two can be compared without a browser:

  .venv/bin/python tools/gun_report.py plan_imgs/m1fire.mkv --at 90.43,97.32
  .venv/bin/python tools/gun_report.py plan_imgs/m1fire.mkv --at 90.43 --synth

The synthesis is a plain Python mirror of the WebAudio graph (RBJ biquads swept
blockwise, the same gain breakpoints) - good enough to check the shape, not a
sample-exact copy of the browser.
"""
import argparse
import os
import subprocess
import sys
import tempfile

import numpy as np

SR = 48000
BANDS = [(25, 60), (60, 110), (110, 250), (250, 600), (600, 2500), (2500, 16000)]

# the cue as `HEAVY_REPORT` builds it: dur, f0, f1, q, type, gain, delay, env
HEAVY_REPORT = [
    (0.45, 2800, 900, 0.6, 'bp', 0.34, 0.00,
     [(0, 1), (0.02, 1), (0.09, 0.4), (0.25, 0.08), (0.45, 0.02)]),
    (0.45, 1200, 420, 0.6, 'bp', 0.34, 0.00,
     [(0, 0.9), (0.04, 1), (0.15, 0.45), (0.35, 0.12), (0.45, 0.02)]),
    (2.0, 400, 110, 0.5, 'lp', 3.0, 0.03,
     [(0, 0.25), (0.06, 0.9), (0.12, 1), (0.3, 0.78), (0.7, 0.5), (1.1, 0.28), (1.6, 0.1), (2.0, 0.02)]),
    (1.8, 600, 180, 0.5, 'lp', 1.5, 0.02,
     [(0, 0.2), (0.08, 0.9), (0.16, 1), (0.4, 0.7), (0.8, 0.4), (1.2, 0.2), (1.8, 0.02)]),
    (2.0, 160, 38, 0.4, 'lp', 2.7, 0.05,
     [(0, 0.1), (0.1, 0.8), (0.2, 1), (0.6, 0.5), (1.1, 0.22), (2.0, 0.02)]),
]


def load(path):
    """Audio of `path` as mono float at SR - a video goes through ffmpeg."""
    with tempfile.TemporaryDirectory() as tmp:
        wav = os.path.join(tmp, 'a.wav')
        subprocess.run(['ffmpeg', '-v', 'error', '-y', '-i', path, '-vn',
                        '-ac', '1', '-ar', str(SR), '-c:a', 'pcm_s16le', wav],
                       check=True)
        import wave
        with wave.open(wav, 'rb') as w:
            data = np.frombuffer(w.readframes(w.getnframes()), dtype=np.int16)
    return data.astype(np.float32) / 32768.0


def rbj(kind, fc, q):
    w = 2 * np.pi * fc / SR
    c, s = np.cos(w), np.sin(w)
    a = s / (2 * q)
    if kind == 'lp':
        b = [(1 - c) / 2, 1 - c, (1 - c) / 2]
    elif kind == 'hp':
        b = [(1 + c) / 2, -(1 + c), (1 + c) / 2]
    else:
        b = [a, 0, -a]
    a0 = 1 + a
    return np.array(b) / a0, np.array([a0, -2 * c, 1 - a]) / a0


def sweep(x, kind, f0, f1, dur, q):
    """`kind` over white noise, its corner sweeping f0 -> f1 over `dur`."""
    y = np.zeros_like(x)
    zi = np.zeros(2)
    for i in range(0, len(x), 32):
        t = i / SR
        fr = f0 * (f1 / f0) ** min(1.0, t / dur) if dur > 0 else f0
        b, a = rbj(kind, fr, q)
        seg = x[i:i + 32]
        for k in range(len(seg)):
            y[i + k] = b[0] * seg[k] + zi[0]
            zi[0] = b[1] * seg[k] - a[1] * y[i + k] + zi[1]
            zi[1] = b[2] * seg[k] - a[2] * y[i + k]
    return y


def synth(seconds=6.0, seed=3):
    """The 105 mm cue, built the way `HEAVY_REPORT` builds it."""
    n = int(seconds * SR)
    white = np.random.default_rng(seed).standard_normal(n)
    t = np.arange(n) / SR
    mix = np.zeros(n)
    for dur, f0, f1, q, kind, gain, delay, env in HEAVY_REPORT:
        y = sweep(white, kind, f0, f1, dur, q)
        g = np.zeros(n)
        for i in range(len(env) - 1):
            (ta, ga), (tb, gb) = env[i], env[i + 1]
            a, b = int((ta + delay) * SR), min(int((tb + delay) * SR), n)
            if b > a:
                g[a:b] = ga * (gb / ga) ** (np.arange(b - a) / (b - a))
        mix += y * g * gain
    # the thump: 120 Hz down to 42, swelling in over 20 ms, 0.75 of a layer
    thump_gain = 0.75
    dt = t - 0.03
    fr = np.where(dt < 0, 120, 120 * (42 / 120) ** np.clip(dt / 0.9, 0, 1))
    o = np.sin(2 * np.pi * np.cumsum(fr) / SR)
    g = np.zeros(n)
    on = dt >= 0
    d = dt[on]
    g1 = np.where(d < 0.02, 0.0001 * (thump_gain / 0.0001) ** np.clip(d / 0.02, 0, 1),
                  thump_gain)
    late = d >= 0.02
    g1[late] = thump_gain * (0.001 / thump_gain) ** np.clip((d[late] - 0.02) / 0.9, 0, 1)
    g[on] = g1
    mix += o * g
    return mix / np.abs(mix).max()


def third_octave(x):
    fc = []
    f = 31.5
    while f < 16000:
        fc.append(f)
        f *= 2 ** (1 / 3)
    spec = np.abs(np.fft.rfft(x * np.hanning(len(x)))) ** 2
    fr = np.fft.rfftfreq(len(x), 1 / SR)
    return fc, np.array([10 * np.log10(spec[(fr >= c / 2 ** (1 / 6))
                                            & (fr < c * 2 ** (1 / 6))].sum() + 1e-20)
                         for c in fc])


def envelope(x, hop=480):
    m = len(x) // hop
    return 20 * np.log10(np.sqrt((x[:m * hop].reshape(m, hop) ** 2).mean(axis=1))
                         / np.abs(x).max() + 1e-9), hop / SR


def band(x, lo, hi):
    spec = np.fft.rfft(x)
    fr = np.fft.rfftfreq(len(x), 1 / SR)
    spec[(fr < lo) | (fr > hi)] = 0
    return np.fft.irfft(spec, len(x))


def report(x, label):
    x = x[:int(3.0 * SR)]
    print(f"\n=== {label} ===")
    print("third octave per window (dB re the window's own peak)")
    fc, _ = third_octave(x[:4800])
    for a, b in [(0, .06), (.06, .2), (.2, .5), (.5, 1.2), (1.2, 2.5)]:
        _, v = third_octave(x[int(a * SR):int(b * SR)])
        print(f" {int(a * 1000):5d}-{int(b * 1000):5d} ms " +
              " ".join(f"{q - v.max():6.1f}" for q in v))
    print(" " + " " * 12 + " ".join(f"{c:6.0f}" for c in fc))

    env, dt = envelope(x)
    print("total envelope (dB re peak), every 100 ms")
    print("  " + " ".join(f"{env[int(t / dt)]:6.0f}"
                          for t in np.arange(0, 2.5, 0.1) if int(t / dt) < len(env)))

    print("per band: peak / -6 dB / -20 dB (ms)")
    for lo, hi in BANDS:
        y = band(x, lo, hi)
        hop = 480
        m = len(y) // hop
        r = np.sqrt((y[:m * hop].reshape(m, hop) ** 2).mean(axis=1))
        print(f"  {lo:5d}-{hi:5d} Hz  peak@{np.argmax(r) * hop / SR * 1000:5.0f}"
              f"  -6dB@{np.argmax(r <= r.max() * .5) * hop / SR * 1000:5.0f}"
              f"  -20dB@{np.argmax(r <= r.max() * .1) * hop / SR * 1000:5.0f}")


def main():
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument('path', help='audio or video file')
    ap.add_argument('--at', default='', help='shot times in seconds, comma separated')
    ap.add_argument('--synth', action='store_true', help='also render the game cue')
    args = ap.parse_args()

    audio = load(args.path)
    if not args.at:
        print("no --at: pass the times of the shots to analyse", file=sys.stderr)
    for s in [t for t in args.at.split(',') if t]:
        t0 = float(s)
        report(audio[int(t0 * SR):], f"{args.path} @ {t0:.2f}s")
    if args.synth:
        report(synth(), "105 mm cue (HEAVY_REPORT, as the browser builds it)")


if __name__ == '__main__':
    main()
