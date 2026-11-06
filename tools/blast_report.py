"""
What a blast sounds like on open, flat ground - the reference the impact cues in
`frontend/src/audio.js` are aimed at.

A charge detonating in free air is a Friedlander wave: the shock jumps to P,
decays linearly to zero over the positive phase T+, swings slightly negative as
the air springs back, and stops.  T+ is the only length in the problem and it
scales with the cube root of the charge:

    30 mm round landing    ~50 g   T+  1.0 ms
    rocket/105 warhead      ~2 kg  T+  2.8 ms
    a hull going up        ~50 kg  T+ 10.0 ms

Everything the ear hears follows from that and from what the air and the ground
do to it on the way.  The output is the three numbers a cue can be tuned
against - spectral centroid, share of the energy above 2 kHz, and the -6 dB span
(the length the ear actually hears):

    python3 tools/blast_report.py

The headline: an explosion's energy lives under 200 Hz because T+ is short and
the spectrum falls 6 dB per octave from 1/(2*pi*T+), which for a 30 mm round is
160 Hz.  A muffled cue is not one with too much bass - it is one that is too
long, or one whose transient has been filtered away.  The -6 dB span is the
number to watch: a 50 g round on open ground is a crack under a millisecond
long, and 420 ms of swept noise is a hiss where a crack belongs.

The ground term is the mirrored source: on flat ground the direct wave and its
reflection arrive 2*h_src*h_ear/d apart, so they cancel first at c/(2*delta) -
about 1.1 kHz for a hull-height burst heard from a cockpit 100 m away.  It is
why a real explosion has a notch in the low mids and why adding a lift there
sounds wrong.
"""
import numpy as np

SR = 48000
C = 343.0


def friedlander(t_plus, n=SR):
    """Shock, linear decay over T+, negative lobe three times as long."""
    d = np.arange(n) / SR / t_plus
    p = np.zeros(n)
    m = d <= 1.0
    p[m] = (1 - d[m]) * np.exp(-1.5 * d[m])
    m = (d > 1.0) & (d <= 4.0)
    p[m] = -0.3 * np.exp(-2.0 * (d[m] - 1.0)) * np.sin(np.pi * (d[m] - 1.0) / 3.0)
    return p


def air_absorption(f, dist_m):
    """ISO 9613-1 at 20 C / 70 % RH (~0.4 dB/km at 125 Hz, 23 at 4 kHz)."""
    f = np.maximum(f, 1.0)
    a = np.interp(np.log(f), [np.log(125), np.log(1000), np.log(4000)],
                  [0.4, 5.0, 23.0])
    a = a * (f / 1000.0) ** 1.2
    return 10 ** (-a * dist_m / 20.0)


def ground_comb(f, src_h, ear_h, dist):
    """Direct plus the mirror image off flat ground, as a magnitude."""
    delta = 2.0 * src_h * ear_h / max(dist, 1.0)
    return np.abs(1 - np.exp(-2j * np.pi * f * delta / C))


def analyse(p, label, dist, src_h=1.2, ear_h=5.0):
    spec = np.abs(np.fft.rfft(p * np.hanning(len(p))))
    f = np.fft.rfftfreq(len(p), 1.0 / SR)
    g = ground_comb(f, src_h, ear_h, dist)
    s = (spec * air_absorption(f, dist) * (g / g.max())) ** 2
    # the net positive area of a blast is a pressure offset: it cannot
    # propagate, so the analysis takes it out below 20 Hz
    s[f < 20] = 0
    centroid = (s * f).sum() / s.sum()
    hi = s[f > 2000].sum() / s.sum() * 100
    env = np.abs(p)
    k = np.where(env > env.max() * 0.5)[0]
    dur = (k[-1] - k[0]) / SR if len(k) else 0.0
    print(f"  {label:<26} centroid {centroid:6.0f} Hz   >2 kHz {hi:5.1f} %   "
          f"-6 dB span {dur * 1000:6.2f} ms")


CHARGES = [
    ("30 mm round (50 g)", 0.0010),
    ("rocket/105 warhead (2 kg)", 0.0028),
    ("hull going up (50 kg)", 0.0100),
]

for dist in (20, 200, 800):
    print(f"\n=== {dist} m, flat ground ===")
    for label, tp in CHARGES:
        analyse(friedlander(tp), label, dist)

print("\nEnergy sits low on purpose; the transient is what reads as an explosion.")
