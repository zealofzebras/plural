"""SmartMike+ real-time bridge: SPP audio -> CELT decode -> any Windows output device.

To use the mic as a normal Windows microphone, install a virtual audio cable
(e.g. VB-Audio Virtual Cable) and send the audio to its playback side
("CABLE Input"); apps then record from "CABLE Output".

Examples:
    python Tools/smartmike_bridge.py --list-devices
    python Tools/smartmike_bridge.py 28:36:38:34:0D:5E --device "CABLE Input"
    python Tools/smartmike_bridge.py 28:36:38:34:0D:5E --device Speakers --mono --set agc=1
    python Tools/smartmike_bridge.py 28:36:38:34:0D:5E --seconds 10 --wav out.wav --volume 0

Stop with Ctrl+C.
"""
import argparse
import os
import sys
import threading
import time
import wave

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import smartmike_client as client  # noqa: E402
from qcelt_decode import Celt071, parse_header  # noqa: E402


class JitterBuffer:
    """Thread-safe sample FIFO with bounded latency and clock-drift compensation.

    The mic's sample clock and the sound card's clock differ slightly (measured
    ~0.2 %), so pull() resamples by a ratio steered by the buffer level
    (clamped to +/-0.5 %, inaudible) to hold the level near the target.
    """

    MAX_RATIO_DEV = 0.005

    def __init__(self, channels, target_samples, max_samples):
        self.ch = channels
        self.target = target_samples
        self.max = max_samples
        self.buf = np.zeros((0, channels), dtype=np.float32)
        self.pos = 0.0  # fractional read position into buf
        self.lock = threading.Lock()
        self.started = False
        self.underruns = 0
        self.dropped = 0
        self.ratio = 1.0

    @property
    def level(self):
        return max(0, int(len(self.buf) - self.pos))

    def push(self, pcm):
        with self.lock:
            self.buf = np.concatenate([self.buf, pcm.astype(np.float32)])
            excess = self.level - self.max
            if excess > 0:  # too far behind: skip ahead to the target
                skip = self.level - self.target
                self.pos += skip
                self.dropped += skip
            if not self.started and self.level >= self.target:
                self.started = True

    def pull(self, n):
        with self.lock:
            if not self.started:
                return np.zeros((n, self.ch), dtype=np.int16)
            err = (self.level - self.target) / max(self.target, 1)
            self.ratio = 1.0 + max(-self.MAX_RATIO_DEV, min(self.MAX_RATIO_DEV, 0.01 * err))
            idx = self.pos + np.arange(n) * self.ratio
            if idx[-1] + 1 >= len(self.buf):
                self.underruns += 1
                self.started = False  # refill to target before resuming
                return np.zeros((n, self.ch), dtype=np.int16)
            i0 = idx.astype(np.int64)
            frac = (idx - i0)[:, None]
            out = self.buf[i0] * (1 - frac) + self.buf[i0 + 1] * frac
            self.pos = idx[-1] + self.ratio
            whole = int(self.pos)
            if whole:
                self.buf = self.buf[whole:]
                self.pos -= whole
        return np.clip(out, -32768, 32767).astype(np.int16)


class BridgeSession(client.Session):
    def __init__(self, *args, mono=False, volume=1.0, wav_path=None, **kw):
        super().__init__(*args, **kw)
        self.mono = mono
        self.volume = volume
        self.dec = None
        self.fmt = None
        self.jb = None
        self.fmt_ready = threading.Event()
        self.wav = None
        self.wav_path = wav_path
        self.decode_errors = 0

    def _on_audio(self, name, p):
        super()._on_audio(name, p)
        body = p[4:]
        while len(body) >= 4:
            h = parse_header(body)
            if h is None:
                self.decode_errors += 1
                return
            if self.dec is None:
                self.dec = Celt071(h["rate"], h["frame_size"], h["channels"])
                self.fmt = h
                self.log(f"stream format: {h['rate']} Hz, {h['channels']} ch, {h['frame_size']} samples, "
                         f"{h['nbytes']} bytes/frame")
                self.fmt_ready.set()
            try:
                raw = self.dec.decode(body[4:4 + h["nbytes"]])
            except RuntimeError:
                self.decode_errors += 1
                raw = None
            body = body[4 + h["nbytes"]:]
            if raw is None:
                continue
            pcm = np.frombuffer(raw, dtype=np.int16).reshape(-1, h["channels"])
            if self.mono and pcm.shape[1] > 1:
                pcm = pcm[:, :1]
            if self.volume != 1.0:
                pcm = np.clip(pcm.astype(np.float32) * self.volume, -32768, 32767).astype(np.int16)
            if self.wav is not None:
                self.wav.writeframes(pcm.tobytes())
            if self.jb is not None:
                self.jb.push(pcm)


def find_device(sd, name):
    if name is None:
        return None
    if name.isdigit():
        return int(name)
    # prefer WASAPI/DirectSound/MME matches in that order of appearance in hostapi list
    for i, d in enumerate(sd.query_devices()):
        if d["max_output_channels"] > 0 and name.lower() in d["name"].lower():
            return i
    raise SystemExit(f"no output device matching '{name}' (use --list-devices)")


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("mac", nargs="?")
    ap.add_argument("--list-devices", action="store_true")
    ap.add_argument("--device", help="output device name substring or index (default: system default)")
    ap.add_argument("--channel", type=int, help="RFCOMM channel (default: resolve SPP UUID)")
    ap.add_argument("--bitrate", default="high", help="high/medium/low or bytes per frame")
    ap.add_argument("--mono", action="store_true", help="output 1 channel (the mic is mono anyway)")
    ap.add_argument("--latency-ms", type=float, default=60, help="jitter buffer target")
    ap.add_argument("--volume", type=float, default=1.0, help="linear output gain (0 = silent test)")
    ap.add_argument("--set", action="append", default=[], metavar="NAME=VALUE", help="device setting, repeatable")
    ap.add_argument("--seconds", type=float, default=0, help="stop after N seconds (0 = until Ctrl+C)")
    ap.add_argument("--wav", help="also save decoded audio to this WAV file")
    a = ap.parse_args()

    import sounddevice as sd
    if a.list_devices:
        print(sd.query_devices())
        return
    if not a.mac:
        ap.error("mac is required")
    mac = a.mac.upper().replace("-", ":")
    dev = find_device(sd, a.device)

    s = BridgeSession(mac, a.channel, client._capture_dir("bridge", mac), mono=a.mono, volume=a.volume)
    bitrate = client.BITRATES.get(a.bitrate) or int(a.bitrate)
    stream = None
    try:
        s.connect()
        s.handshake()
        client.apply_settings(s, a.set)
        s.send_param("AUDIO_BEGIN", 4, bitrate, f"AUDIO_BEGIN bitrate={bitrate}")
        if not s.fmt_ready.wait(5):
            raise SystemExit("no audio received within 5 s")
        rate = s.fmt["rate"]
        ch = 1 if a.mono else s.fmt["channels"]
        target = int(rate * a.latency_ms / 1000)
        s.jb = JitterBuffer(ch, target, target * 3 + s.fmt["frame_size"])
        if a.wav:
            s.wav = wave.open(a.wav, "wb")
            s.wav.setnchannels(ch)
            s.wav.setsampwidth(2)
            s.wav.setframerate(rate)

        def callback(outdata, frames, t, status):
            outdata[:] = s.jb.pull(frames)

        stream = sd.OutputStream(device=dev, samplerate=rate, channels=ch, dtype="int16",
                                 blocksize=256, latency="low", callback=callback)
        stream.start()
        name = sd.query_devices(stream.device)["name"] if dev is not None else "default output"
        s.log(f"bridging to '{name}' — Ctrl+C to stop")
        t_end = time.monotonic() + a.seconds if a.seconds else None
        last = time.monotonic()
        while s.running and (t_end is None or time.monotonic() < t_end):
            time.sleep(0.5)
            s.send_heartbeat()
            if time.monotonic() - last >= 5:
                last = time.monotonic()
                s.log(f"buffer {s.jb.level * 1000 // rate} ms, underruns {s.jb.underruns}, "
                      f"dropped {s.jb.dropped * 1000 // rate} ms, frames {s.audio_frames}, "
                      f"decode errors {s.decode_errors}")
    except KeyboardInterrupt:
        pass
    finally:
        if stream is not None:
            stream.stop()
            stream.close()
        try:
            if s.sock:
                s.send_param("AUDIO_END", 4, bitrate, "AUDIO_END")
                time.sleep(0.2)
        except OSError:
            pass
        if s.wav is not None:
            s.wav.close()
        s.close()
        if s.jb:
            print(f"underruns {s.jb.underruns}, dropped {s.jb.dropped} samples, "
                  f"frames {s.audio_frames}, decode errors {s.decode_errors}")


if __name__ == "__main__":
    main()
