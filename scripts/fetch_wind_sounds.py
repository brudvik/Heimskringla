"""Laster ned CC0-vindopptak fra Freesound og lager sømløse lydløkker til assets/audio/wind/.

Bruk:
    py -3.12 scripts/fetch_wind_sounds.py [nedlastingsmappe] [--analyze]

Krever numpy og soundfile (pip install numpy soundfile). Standard nedlastingsmappe er
D:/3dmodels/zip/freesound. Med --analyze skrives bare statistikk for kandidatene (lengde, nivå,
klang og antall transienter som knekk, fugler og biler), uten at løkkene lages.

Freesound krever innlogging for originalfilene, men forhåndsvisningene (HQ mp3, ~128 kbps) er
åpne. Det holder fint for bakgrunnsvind, som uansett er støy. Bare opptak med CC0-lisens brukes,
og lisensen sjekkes på lydsiden før nedlasting.

Hver løkke lages slik:
  1. Velger det roligste utsnittet av LEN sekunder (færrest transienter og jevnest nivå).
  2. Høypass ved 35 Hz (rumling) og svak lavpass ved 6.5 kHz (skarpt sus).
  3. Sømløs løkke: de siste XF sekundene krysstones inn over starten (likeeffekt).
  4. Normaliseres til samme RMS, så spillet kan blande lagene med kjente nivåer.
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
LEN = 50.0      # sekunder per løkke
XF = 4.0        # krysstoning i skjøten
TARGET_DB = -20.0

# (freesound-id, filnavn i spillet, beskrivelse)
SOUNDS = [
    (139337, "wind_grass", "lett vind i gress, trær i bakgrunnen (felix.blume)"),
    (259968, "wind_forest", "rolig vind i trær på avstand (lwdickens)"),
    (819351, "wind_strong", "kraftigere vind i skog (JW_Audio)"),
    (104316, "wind_open", "vindkast høyt til fjells (Proxima4)"),
]
CANDIDATES = [139337, 819351, 110919, 321412, 104316, 259968, 376534, 450372]


def get(url):
    with urllib.request.urlopen(urllib.request.Request(url, headers=UA)) as r:
        return r.read()


def info(sid):
    html = get(f"https://freesound.org/s/{sid}/").decode("utf-8", "replace")
    if "Creative Commons 0" not in html:
        raise RuntimeError(f"{sid}: ikke CC0, hoppes over")
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
    print(f"  laster ned {sid}: {title} ({artist})")
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
    if sr != RATE:  # lineær omsampling holder for støy
        t = np.arange(0, len(x) / sr, 1 / RATE)
        x = np.stack([np.interp(t, np.arange(len(x)) / sr, x[:, c]) for c in range(2)], axis=1).astype(np.float32)
    return x


def highpass(x, fc=35.0, lp=6500.0):
    # Myk høypass i frekvensdomenet (2. ordens) og en svak lavpass (1. ordens) som tar bort
    # det skarpe suset i diskanten, så vinden blir behageligere å høre på over tid
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
    # Høyfrekvent innhold (fugler, knepp): differanse-energi i forhold til total
    hf = np.sqrt((np.diff(fr, axis=1) ** 2).mean(axis=1) + 1e-12) / rms
    return rms, peak, hf


def transients(rms, peak):
    # Rammer der toppen stikker langt over nivået rundt (glidende median over ~2 s)
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
          f"transienter {tr.mean() * 100:4.1f}%  nivåvariasjon {np.std(20 * np.log10(rms)):4.1f} dB  {meta['title'][:60]}")


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
    # Halen tones ut over starten, starten tones inn: slutten av body går rett over i halen
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
        # libsndfile krasjer (stack overflow) på store Vorbis-skrivinger i én bit: skriv i blokker
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
            f.write("Vindopptak fra https://freesound.org, lisens CC0 1.0 (ingen kreditering påkrevd), "
                    "klippet til løkker av scripts/fetch_wind_sounds.py:\n")
        for sid, name, meta in credits:
            if f"freesound.org/s/{sid}" not in text:
                f.write(f"- {name}.ogg - \"{meta['title']}\" av {meta['artist']} - https://freesound.org/s/{sid}/\n")
    print("Ferdig:", OUT)


if __name__ == "__main__":
    main()
