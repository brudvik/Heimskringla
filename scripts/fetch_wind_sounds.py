"""Downloads CC0 wind recordings from Freesound and makes seamless sound loops for assets/audio/wind/.

Usage:
    py -3.12 scripts/fetch_wind_sounds.py [download-dir] [--analyze]

Requires numpy and soundfile (pip install numpy soundfile). The default download dir is
D:/3dmodels/zip/freesound. With --analyze, only statistics for the candidates are printed (length, level,
timbre and number of transients such as snapping twigs, birds and cars), without making the loops.

Freesound requires login for the original files, but the previews (HQ mp3, ~128 kbps) are
open. That is fine for background wind, which is noise anyway. Only CC0-licensed recordings are used,
and the license is checked on the sound's page before downloading.

Each loop is made as follows:
  1. Picks the calmest LEN-second window (fewest transients and most even level).
  2. High-pass at 35 Hz (rumble) and a gentle low-pass at 6.5 kHz (harsh hiss).
  3. Seamless loop: the last XF seconds are crossfaded over the start (equal power).
  4. Normalized to the same RMS, so the game can mix the layers at known levels.
"""
import json
import os
import re
import sys
import urllib.request

import numpy as np
import soundfile as sf

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, "assets", "audio", "wind")
UA = {"User-Agent": "Mozilla/5.0 (heimskringla-asset-script)"}
RATE = 44100
LEN = 50.0      # seconds per loop
XF = 4.0        # crossfade at the seam
TARGET_DB = -20.0

# (freesound id, file name in the game, description)
SOUNDS = [
    (139337, "wind_grass", "light wind in grass, trees in the background (felix.blume)"),
    (259968, "wind_forest", "calm wind in distant trees (lwdickens)"),
    (819351, "wind_strong", "stronger wind in forest (JW_Audio)"),
    (104316, "wind_open", "gusts high in the mountains (Proxima4)"),
]
CANDIDATES = [139337, 819351, 110919, 321412, 104316, 259968, 376534, 450372]


def get(url):
    with urllib.request.urlopen(urllib.request.Request(url, headers=UA)) as r:
        return r.read()


def info(sid):
    html = get(f"https://freesound.org/s/{sid}/").decode("utf-8", "replace")
    if "Creative Commons 0" not in html:
        raise RuntimeError(f"{sid}: not CC0, skipping")
    preview = re.search(r"https://cdn\.freesound\.org/previews/[0-9]+/[0-9_]+-hq\.mp3", html).group(0)
    title = re.search(r'og:audio:title" content="([^"]+)', html).group(1)
    artist = re.search(r'og:audio:artist" content="([^"]+)', html).group(1)
    return preview, title, artist


def download(sid, folder):
    path = os.path.join(folder, f"{sid}.mp3")
    meta_path = path + ".json"
    if os.path.exists(path) and os.path.exists(meta_path):
        with open(meta_path, encoding="utf-8") as f:
            return path, json.load(f)
    preview, title, artist = info(sid)
    print(f"  downloading {sid}: {title} ({artist})")
    data = get(preview)
    with open(path, "wb") as f:
        f.write(data)
    meta = {"id": sid, "title": title, "artist": artist}
    with open(meta_path, "w", encoding="utf-8") as f:
        json.dump(meta, f)
    return path, meta


def load(path):
    x, sr = sf.read(path, always_2d=True, dtype="float32")
    if x.shape[1] == 1:
        x = np.repeat(x, 2, axis=1)
    x = x[:, :2]
    if sr != RATE:  # linear resampling is good enough for noise
        t = np.arange(0, len(x) / sr, 1 / RATE)
        x = np.stack([np.interp(t, np.arange(len(x)) / sr, x[:, c]) for c in range(2)], axis=1).astype(np.float32)
    return x


def highpass(x, fc=35.0, lp=6500.0):
    # Soft high-pass in the frequency domain (2nd order) and a gentle low-pass (1st order) that removes
    # the harsh hiss in the treble, so the wind is more pleasant to listen to over time
    spec = np.fft.rfft(x, axis=0)
    f = np.fft.rfftfreq(len(x), 1 / RATE)[:, None]
    spec *= (f / fc) ** 2 / (1 + (f / fc) ** 2)
    spec /= np.sqrt(1 + (f / lp) ** 2)
    return np.fft.irfft(spec, n=len(x), axis=0).astype(np.float32)


def frame_stats(x, win=0.05):
    n = int(win * RATE)
    m = x.mean(axis=1)
    k = len(m) // n
    fr = m[: k * n].reshape(k, n)
    rms = np.sqrt((fr ** 2).mean(axis=1) + 1e-12)
    peak = np.abs(fr).max(axis=1)
    # High-frequency content (birds, clicks): difference energy relative to total
    hf = np.sqrt((np.diff(fr, axis=1) ** 2).mean(axis=1) + 1e-12) / rms
    return rms, peak, hf


def transients(rms, peak):
    # Frames where the peak sticks far above the surrounding level (moving median over ~2 s)
    k = 40
    pad = np.pad(rms, k, mode="edge")
    local = np.array([np.median(pad[i:i + 2 * k]) for i in range(len(rms))])
    return (peak > local * 5.0) | (rms > local * 2.2)


def analyze(path, meta):
    x = load(path)
    rms, peak, hf = frame_stats(x)
    tr = transients(rms, peak)
    db = 20 * np.log10(np.sqrt((x ** 2).mean()) + 1e-12)
    spec = np.abs(np.fft.rfft(x[: RATE * 20].mean(axis=1)))
    freqs = np.fft.rfftfreq(min(len(x), RATE * 20), 1 / RATE)
    centroid = (spec * freqs).sum() / spec.sum()
    print(f"{meta['id']:>7}  {len(x) / RATE:6.1f}s  {db:6.1f} dBFS  centroid {centroid:6.0f} Hz  "
          f"transients {tr.mean() * 100:4.1f}%  level variation {np.std(20 * np.log10(rms)):4.1f} dB  {meta['title'][:60]}")


def best_window(x):
    rms, peak, _ = frame_stats(x)
    tr = transients(rms, peak).astype(np.float32)
    n = int(LEN / 0.05)
    if len(rms) <= n:
        return 0
    db = 20 * np.log10(rms)
    best, best_i = 1e9, 0
    for i in range(0, len(rms) - n, 20):
        score = tr[i:i + n].sum() * 3 + np.std(db[i:i + n])
        if score < best:
            best, best_i = score, i
    return int(best_i * 0.05 * RATE)


def make_loop(x):
    total = int((LEN + XF) * RATE)
    s = best_window(x)
    seg = x[s:s + total]
    if len(seg) < total:
        seg = x[:total]
    seg = highpass(seg)
    xf = int(XF * RATE)
    body = seg[: len(seg) - xf].copy()
    tail = seg[len(seg) - xf:]
    t = np.linspace(0, 1, xf, dtype=np.float32)[:, None]
    # The tail fades out over the start while the start fades in: the end of body runs straight into the tail
    body[:xf] = body[:xf] * np.sin(t * np.pi / 2) + tail * np.cos(t * np.pi / 2)
    rms = np.sqrt((body ** 2).mean())
    body *= 10 ** (TARGET_DB / 20) / max(rms, 1e-6)
    peak = np.abs(body).max()
    if peak > 0.98:
        body *= 0.98 / peak
    return body


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    folder = args[0] if args else "D:/3dmodels/zip/freesound"
    os.makedirs(folder, exist_ok=True)

    if "--analyze" in sys.argv:
        for sid in CANDIDATES:
            try:
                path, meta = download(sid, folder)
                analyze(path, meta)
            except Exception as e:  # noqa
                print(f"{sid}: {e}")
        return

    os.makedirs(OUT, exist_ok=True)
    credits = []
    for sid, name, desc in SOUNDS:
        path, meta = download(sid, folder)
        loop = make_loop(load(path))
        target = os.path.join(OUT, f"{name}.ogg")
        # libsndfile crashes (stack overflow) on large Vorbis writes in one go: write in blocks
        with sf.SoundFile(target, "w", RATE, 2, format="OGG", subtype="VORBIS") as out:
            for i in range(0, len(loop), 8192):
                out.write(np.ascontiguousarray(loop[i:i + 8192]))
        print(f"  {name}.ogg  {len(loop) / RATE:.1f}s  ({desc})")
        credits.append((sid, name, meta))

    lic = os.path.join(ROOT, "assets", "LICENSES.md")
    with open(lic, encoding="utf-8") as f:
        text = f.read()
    with open(lic, "a", encoding="utf-8", newline="\n") as f:
        if "assets/audio/wind" not in text:
            f.write("\n## assets/audio/wind/ - Freesound (CC0 1.0)\n\n")
            f.write("Wind recordings from https://freesound.org, license CC0 1.0 (no attribution required), "
                    "cut into loops by scripts/fetch_wind_sounds.py:\n")
        for sid, name, meta in credits:
            if f"freesound.org/s/{sid}" not in text:
                f.write(f"- {name}.ogg - \"{meta['title']}\" by {meta['artist']} - https://freesound.org/s/{sid}/\n")
    print("Done:", OUT)


if __name__ == "__main__":
    main()
