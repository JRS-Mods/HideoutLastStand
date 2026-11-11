"""Makes the siege's sounds into assets/sounds. All synthesised, so nothing is taken from the game or anywhere else.
Standard library only.   python tools/make_sounds.py"""
import math, os, random, struct, wave

RATE = 44100
OUT = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "assets", "sounds")
random.seed(1991)


def low_pass(samples, amount):
    out, y = [], 0.0
    for x in samples:
        y += amount * (x - y)
        out.append(y)
    return out


def write(name, samples):
    os.makedirs(OUT, exist_ok=True)
    peak = max(abs(s) for s in samples) or 1
    with wave.open(os.path.join(OUT, name + ".wav"), "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(RATE)
        w.writeframes(b"".join(struct.pack("<h", int(s / peak * 30000)) for s in samples))


def saw(phase):
    return 2 * ((phase / (2 * math.pi)) % 1) - 1


def alarm():
    """A rising and falling air-raid siren that loops cleanly: 8 seconds, returning to where it started."""
    n, out, phase = RATE * 8, [], 0.0
    for i in range(n):
        t = i / n
        sweep = .5 - .5 * math.cos(2 * math.pi * t)
        f = 290 + 330 * sweep
        phase += 2 * math.pi * f / RATE
        s = .55 * saw(phase) + .35 * math.sin(phase) + .1 * saw(phase * 2.01)
        out.append(s * (.55 + .45 * sweep))
    return low_pass(out, .22)


def breach():
    """A door charge: a sharp crack, a deep boom, and debris falling."""
    n = int(RATE * 3.4)
    rumble, y = [], 0.0
    for i in range(n):
        t = i / RATE
        y += .018 * (random.uniform(-1, 1) - y)
        rumble.append(y * 9 * math.exp(-t * 1.4))
    crack = [random.uniform(-1, 1) * math.exp(-i / RATE * 28) for i in range(n)]
    thump = [math.sin(2 * math.pi * (46 + 70 * math.exp(-i / RATE * 9)) * i / RATE) * math.exp(-i / RATE * 3.2) for i in range(n)]
    debris = [0.0] * n
    for _ in range(90):
        at = random.randint(int(RATE * .35), n - 4000)
        size = random.randint(300, 2600)
        gain = random.uniform(.03, .14) * (1 - at / n)
        for j in range(size):
            debris[at + j] += random.uniform(-1, 1) * gain * (1 - j / size)
    return [c * .8 + r + th * 1.1 + d for c, r, th, d in zip(crack, rumble, thump, debris)]


def wave_stinger():
    """A low brass-like hit announcing a wave."""
    n = int(RATE * 2.2)
    out, p1, p2 = [], 0.0, 0.0
    for i in range(n):
        t = i / RATE
        env = min(1, t / .02) * math.exp(-t * 1.7)
        p1 += 2 * math.pi * 65.4 / RATE
        p2 += 2 * math.pi * 98.0 / RATE
        out.append((saw(p1) + .8 * saw(p2) + .6 * saw(p1 * 2)) * env)
    out = low_pass(out, .09)
    hit = [math.sin(2 * math.pi * 52 * i / RATE) * math.exp(-i / RATE * 6) * 1.2 for i in range(n)]
    return [a + b for a, b in zip(out, hit)]


def victory():
    """Three rising chords, restrained: you survived, nobody's celebrating."""
    chords = [(146.8, 220.0, 293.7), (164.8, 246.9, 329.6), (196.0, 293.7, 392.0)]
    out = []
    for index, chord in enumerate(chords):
        n = int(RATE * (1.0 if index < 2 else 2.6))
        for i in range(n):
            t = i / RATE
            env = min(1, t / .06) * math.exp(-t * (1.2 if index < 2 else .8))
            out.append(sum(math.sin(2 * math.pi * f * t) + .25 * math.sin(4 * math.pi * f * t) for f in chord) * env)
    return low_pass(out, .25)


def beep():
    """The charge counting down: one short electronic pip."""
    n = int(RATE * .14)
    return [math.sin(2 * math.pi * 1900 * i / RATE) * min(1, (n - i) / 300, i / 60) for i in range(n)] + [0.0] * 800


def camera_static():
    """A security camera's feed cutting out."""
    n = int(RATE * .7)
    return low_pass([random.uniform(-1, 1) * (.6 + .4 * math.sin(i / RATE * 90)) * min(1, (n - i) / 2000) for i in range(n)], .55)


def powerdown():
    """The generator dying: a hum sliding down to nothing, with a thunk at the start."""
    n = int(RATE * 1.8)
    out, phase = [], 0.0
    for i in range(n):
        t = i / n
        f = 120 * (1 - t) ** 2 + 18
        phase += 2 * math.pi * f / RATE
        out.append((saw(phase) * .7 + math.sin(phase * 2) * .3) * (1 - t) ** 1.5)
    out = low_pass(out, .12)
    thunk = [math.sin(2 * math.pi * 70 * i / RATE) * math.exp(-i / RATE * 14) * 1.4 for i in range(n)]
    return [a + b for a, b in zip(out, thunk)]


def heavy_shot():
    """One round from a heavy machine gun indoors: a hard crack, a chest-deep thump, and the room ringing after it."""
    n = int(RATE * .55)
    crack = [random.uniform(-1, 1) * math.exp(-i / RATE * 60) for i in range(n)]
    thump = [math.sin(2 * math.pi * (55 + 90 * math.exp(-i / RATE * 30)) * i / RATE) * math.exp(-i / RATE * 14) for i in range(n)]
    ring, y = [], 0.0
    for i in range(n):
        y += .05 * (random.uniform(-1, 1) - y)
        ring.append(y * 5 * math.exp(-i / RATE * 7))
    return low_pass([c * 1.1 + th * 1.3 + r for c, th, r in zip(crack, thump, ring)], .55)


write("powerdown", powerdown())
write("powerup", list(reversed(powerdown())))
write("beep", beep())
write("static", camera_static())
write("alarm", alarm())
write("breach", breach())
write("wave", wave_stinger())
write("victory", victory())
write("turret", heavy_shot())
print("sounds written to", OUT)
