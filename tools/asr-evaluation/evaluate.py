"""Isolated Windows ASR evaluation. Native inference; Python only drives tests.

See README.md. Downloads and measurements stay under --root, never in app data.
"""
import argparse
import array
import base64
import ctypes as C
import hashlib
import json
import math
import os
from pathlib import Path
import queue
import random
import socket
import subprocess
import sys
import threading
import time
import urllib.request
import wave


def save(path, value):
    Path(path).write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding="utf-8")


def read_wav(path):
    with wave.open(str(path), "rb") as w:
        if (w.getnchannels(), w.getsampwidth(), w.getframerate()) != (1, 2, 16000):
            raise ValueError(f"Expected mono PCM16 16 kHz: {path}")
        return w.readframes(w.getnframes())


def write_wav(path, pcm):
    with wave.open(str(path), "wb") as w:
        w.setparams((1, 2, 16000, 0, "NONE", "not compressed"))
        w.writeframes(pcm)


def add_noise(pcm, snr_db, seed=20260914):
    """Deterministic full-clip RMS SNR stress input; not a recording of game noise."""
    samples = array.array("h", pcm)
    rms = math.sqrt(sum(float(x) * x for x in samples) / len(samples))
    rng = random.Random(seed)
    noise = [rng.gauss(0, 1) for _ in samples]
    noise_rms = math.sqrt(sum(x * x for x in noise) / len(noise))
    scale = rms / (10 ** (snr_db / 20)) / noise_rms
    mixed = [sample + value * scale for sample, value in zip(samples, noise)]
    gain = min(1, 32700 / max(1, max(abs(x) for x in mixed)))
    return array.array("h", (round(x * gain) for x in mixed)).tobytes()


class Option(C.Structure):
    _fields_ = [("name", C.c_char_p), ("value", C.c_char_p)]


class Line(C.Structure):
    # Moonshine v0.1.5, header ABI 30000. Native transcript owns all pointers.
    _fields_ = [("text", C.c_char_p), ("audio", C.c_void_p), ("count", C.c_size_t),
                ("start", C.c_float), ("duration", C.c_float), ("id", C.c_uint64),
                ("complete", C.c_int8), ("updated", C.c_int8), ("new", C.c_int8),
                ("changed", C.c_int8), ("speaker_changed", C.c_int8),
                ("speakers", C.c_void_p), ("speaker_count", C.c_uint64),
                ("latency", C.c_uint32), ("words", C.c_void_p), ("word_count", C.c_uint64)]


class Transcript(C.Structure):
    _fields_ = [("lines", C.POINTER(Line)), ("count", C.c_uint64)]


class Moonshine:
    def __init__(self, root):
        assert C.sizeof(Line) == 88 and C.sizeof(Transcript) == 16
        folder = root / "runtimes/moonshine-wheel/moonshine_voice"
        self.dll_dir = os.add_dll_directory(str(folder.resolve()))
        self.lib = C.CDLL(str(folder / "moonshine.dll"))
        signatures = {
            "get_version": (C.c_int32, []),
            "free_buffer": (None, [C.c_void_p]),
            "get_stt_dependencies": (C.c_int32, [C.c_char_p, C.POINTER(Option), C.c_uint64, C.POINTER(C.c_void_p)]),
            "load_transcriber_from_files": (C.c_int32, [C.c_char_p, C.c_uint32, C.POINTER(Option), C.c_uint64, C.c_int32]),
            "free_transcriber": (None, [C.c_int32]),
            "create_stream": (C.c_int32, [C.c_int32, C.c_uint32]),
            "free_stream": (C.c_int32, [C.c_int32, C.c_int32]),
            "start_stream": (C.c_int32, [C.c_int32, C.c_int32]),
            "stop_stream": (C.c_int32, [C.c_int32, C.c_int32]),
            "transcribe_add_audio_to_stream": (C.c_int32, [C.c_int32, C.c_int32, C.POINTER(C.c_float), C.c_uint64, C.c_int32, C.c_uint32]),
            "transcribe_stream": (C.c_int32, [C.c_int32, C.c_int32, C.c_uint32, C.POINTER(C.POINTER(Transcript))]),
            "transcribe_without_streaming": (C.c_int32, [C.c_int32, C.POINTER(C.c_float), C.c_uint64, C.c_int32, C.c_uint32, C.POINTER(C.POINTER(Transcript))]),
        }
        for name, (ret, args) in signatures.items():
            fn = getattr(self.lib, "moonshine_" + name)
            fn.restype, fn.argtypes = ret, args
            setattr(self, name, fn)
        self.handle = None

    @staticmethod
    def check(value):
        if value < 0:
            raise RuntimeError(f"Moonshine native error {value}")
        return value

    def manifest(self, language, architecture):
        opts = (Option * 1)(Option(b"model_arch", str(architecture).encode()))
        ptr = C.c_void_p()
        self.check(self.get_stt_dependencies(language.encode(), opts, 1, C.byref(ptr)))
        try:
            return json.loads(C.string_at(ptr).decode("utf-8"))
        finally:
            self.free_buffer(ptr)

    def load(self, folder, architecture, language):
        opts = (Option * 4)(Option(b"max_tokens_per_second", b"13" if language in ("zh", "ja") else b"6.5"),
                           Option(b"identify_speakers", b"false"), Option(b"word_timestamps", b"false"),
                           Option(b"ort_providers", b"CPU"))
        started = time.perf_counter()
        self.handle = self.check(self.load_transcriber_from_files(str(folder).encode("utf-8"), architecture, opts, len(opts), 30000))
        return (time.perf_counter() - started) * 1000

    def close(self):
        if self.handle is not None:
            self.free_transcriber(self.handle)
            self.handle = None

    @staticmethod
    def snapshot(ptr):
        if not ptr:
            return []
        return [dict(text=(x.text or b"").decode("utf-8"), start=x.start, duration=x.duration,
                     complete=bool(x.complete), id=x.id) for x in ptr.contents.lines[:ptr.contents.count]]

    @staticmethod
    def floats(pcm):
        ints = array.array("h", pcm)
        values = array.array("f", (x / 32768.0 for x in ints))
        return (C.c_float * len(values)).from_buffer(values)

    def infer(self, pcm, streaming=False, realtime=False):
        began = time.perf_counter()
        duration = len(pcm) / 32000
        events, first, cpu_ms = [], None, 0.0
        ptr = C.POINTER(Transcript)()
        if not streaming:
            values = self.floats(pcm)
            t = time.perf_counter()
            self.check(self.transcribe_without_streaming(self.handle, values, len(values), 16000, 0, C.byref(ptr)))
            cpu_ms = (time.perf_counter() - t) * 1000
            lines = self.snapshot(ptr)
        else:
            stream = self.check(self.create_stream(self.handle, 0))
            try:
                self.check(self.start_stream(self.handle, stream))
                last_text = None
                for offset in range(0, len(pcm), 6400):  # 200 ms, no padding/forced updates
                    chunk = pcm[offset:offset + 6400]
                    if realtime:
                        time.sleep(max(0, began + (offset + len(chunk)) / 32000 - time.perf_counter()))
                    values = self.floats(chunk)
                    t = time.perf_counter()
                    self.check(self.transcribe_add_audio_to_stream(self.handle, stream, values, len(values), 16000, 0))
                    self.check(self.transcribe_stream(self.handle, stream, 0, C.byref(ptr)))
                    cpu_ms += (time.perf_counter() - t) * 1000
                    lines = self.snapshot(ptr)
                    text = " ".join(x["text"] for x in lines).strip()
                    if text and first is None:
                        first = (time.perf_counter() - began) * 1000
                    if text != last_text:
                        events.append(dict(elapsed_ms=(time.perf_counter() - began) * 1000,
                                           audio_ms=(offset + len(chunk)) / 32, lines=lines))
                        last_text = text
                t = time.perf_counter()
                self.check(self.stop_stream(self.handle, stream))
                self.check(self.transcribe_stream(self.handle, stream, 0, C.byref(ptr)))
                cpu_ms += (time.perf_counter() - t) * 1000
                lines = self.snapshot(ptr)
            finally:
                self.check(self.free_stream(self.handle, stream))
        elapsed = (time.perf_counter() - began) * 1000
        return dict(text=" ".join(x["text"] for x in lines).strip(), lines=lines, elapsed_ms=elapsed,
                    native_call_ms=cpu_ms, rtf=elapsed / (duration * 1000), duration_sec=duration,
                    first_text_ms=first, tail_ms=max(0, elapsed - duration * 1000) if realtime else None, events=events)


def download_moon(root, variants=None):
    import google_crc32c
    api = Moonshine(root)
    for lang, arch in variants or (("zh", 2), ("ja", 4), ("en", 4)):
        manifest = api.manifest(lang, arch)
        folder = root / "models" / f"moonshine-{lang}-{arch}"
        folder.mkdir(exist_ok=True)
        for group in manifest["groups"]:
            for item in group["files"]:
                path = folder / item["name"]
                if Path(item["name"]).name != item["name"]:
                    raise ValueError("Unexpected model filename")
                if not path.exists():
                    print(f"Downloading {lang}/{item['name']} ({item.get('size')} bytes)", flush=True)
                    part = path.with_suffix(path.suffix + ".part")
                    subprocess.run(["curl.exe", "--fail", "--location", "--silent", "--show-error", "--retry", "2",
                                    "--max-time", "300", "--output", str(part), item["url"]], check=True)
                    part.replace(path)
                checksum = google_crc32c.Checksum()
                sha = hashlib.sha256()
                with path.open("rb") as source:
                    for chunk in iter(lambda: source.read(1024 * 1024), b""):
                        checksum.update(chunk)
                        sha.update(chunk)
                if item.get("size") is not None and path.stat().st_size != item["size"]:
                    raise RuntimeError(f"Size mismatch: {path}")
                if item.get("checksum_type") == "crc32c" and base64.b64encode(checksum.digest()).decode() != item["checksum"]:
                    raise RuntimeError(f"CRC32C mismatch: {path}")
                item["local_sha256"] = sha.hexdigest()
        save(folder / "manifest.json", manifest)
        print(f"Verified Moonshine {lang}, architecture={arch}", flush=True)


class Nemo:
    def __init__(self, root, backend):
        self.root, self.backend, self.process = root, backend, None
        self.log = None

    def start(self):
        with socket.socket() as s:
            s.bind(("127.0.0.1", 0))
            port = s.getsockname()[1]
        self.url = f"http://127.0.0.1:{port}"
        self.log = (self.root / "results" / f"{getattr(self, 'log_tag', 'nemo-' + self.backend)}-server.log").open("w", encoding="utf-8")
        exe = self.root / f"runtimes/nemo-{self.backend}/bin/nemo-speech.exe"
        args = [str(exe), "--json", "serve", "--host", "127.0.0.1", "--port", str(port),
                "--asr-model", str(self.root / "models/nemotron-3.5-asr-streaming-0.6b.q8_0.gguf"),
                "--device", self.backend, "--no-ui", "--read-timeout", "120", "--write-timeout", "120"]
        if getattr(self, "disable_batching", False):
            args.append("--asr.batching.enabled=false")
        if getattr(self, "skip_warmup", False):
            args.append("--no-warmup")
        if getattr(self, "endpointing", False):
            args.append("--asr.endpointing.enable")
        if getattr(self, "endpoint_ms", None) is not None:
            args.extend(["--asr.endpointing.stop_history_eou_ms", str(self.endpoint_ms)])
        began = time.perf_counter()
        self.process = subprocess.Popen(args, stdout=self.log, stderr=self.log, creationflags=subprocess.CREATE_NO_WINDOW)
        deadline = began + 120
        while time.perf_counter() < deadline:
            if self.process.poll() is not None:
                raise RuntimeError(f"Nemotron startup exited {self.process.returncode}; see server log")
            try:
                with urllib.request.urlopen(self.url + "/ready", timeout=1) as response:
                    self.ready = json.load(response)
                return (time.perf_counter() - began) * 1000
            except (OSError, ValueError):
                time.sleep(0.1)
        raise TimeoutError("Nemotron startup timeout")

    def close(self):
        if self.process and self.process.poll() is None:
            self.process.terminate()
            try:
                self.process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                self.process.kill()
                self.process.wait(timeout=10)
        if self.log:
            self.log.close()

    def infer(self, pcm, language, streaming=False, realtime=False):
        duration = len(pcm) / 32000
        if not streaming:
            boundary = "svt-asr-evaluation"
            wav_path = self.root / "samples/request.wav"
            write_wav(wav_path, pcm)
            body = bytearray()
            for name, value in (("language", language), ("response_format", "verbose_json")):
                body.extend(f'--{boundary}\r\nContent-Disposition: form-data; name="{name}"\r\n\r\n{value}\r\n'.encode())
            body.extend(f'--{boundary}\r\nContent-Disposition: form-data; name="file"; filename="audio.wav"\r\nContent-Type: audio/wav\r\n\r\n'.encode())
            body.extend(wav_path.read_bytes())
            body.extend(f"\r\n--{boundary}--\r\n".encode())
            req = urllib.request.Request(self.url + "/v1/audio/transcriptions", bytes(body),
                                         {"Content-Type": f"multipart/form-data; boundary={boundary}"})
            began = time.perf_counter()
            with urllib.request.urlopen(req, timeout=120) as response:
                data = json.load(response)
            elapsed = (time.perf_counter() - began) * 1000
            return dict(text=data.get("text", ""), duration_sec=duration, elapsed_ms=elapsed,
                        rtf=elapsed / (duration * 1000), response=data)
        import websocket
        ws = websocket.create_connection(self.url.replace("http:", "ws:") + "/v1/realtime", timeout=120)
        events, errors = [], []
        done = threading.Event()
        try:
            created = json.loads(ws.recv())
            ws.send(json.dumps({"type": "session.update", "session": {"sample_rate": 16000,
                          "language": language, "word_timestamps": False}}))
            updated = json.loads(ws.recv())
            if updated.get("type") != "session.updated":
                raise RuntimeError(f"Session update failed: {updated}")
            began = time.perf_counter()

            def receive():
                try:
                    while True:
                        raw = ws.recv()
                        if not raw:
                            raise RuntimeError("WebSocket closed before final result")
                        event = json.loads(raw)
                        events.append(dict(elapsed_ms=(time.perf_counter() - began) * 1000, event=event))
                        if event.get("type") == "error":
                            raise RuntimeError(str(event))
                        # A stream can finalize several utterances before commit.
                        if event.get("type") == "input_audio_buffer.committed":
                            break
                except Exception as error:
                    errors.append(str(error))
                finally:
                    done.set()

            thread = threading.Thread(target=receive, daemon=True)
            thread.start()
            for offset in range(0, len(pcm), 6400):
                chunk = pcm[offset:offset + 6400]
                if realtime:
                    time.sleep(max(0, began + (offset + len(chunk)) / 32000 - time.perf_counter()))
                ws.send_binary(chunk)
            ws.send(json.dumps({"type": "input_audio_buffer.commit"}))
            if not done.wait(120):
                raise TimeoutError("Nemotron did not finalize")
            if errors:
                raise RuntimeError(errors[0])
            elapsed = (time.perf_counter() - began) * 1000
            finals = [x["event"] for x in events if x["event"].get("type", "").endswith(".completed")]
            first = next((x["elapsed_ms"] for x in events if x["event"].get("delta") or x["event"].get("transcript") or x["event"].get("text")), None)
            return dict(text=" ".join(x.get("transcript", x.get("text", "")) for x in finals).strip(), duration_sec=duration,
                        elapsed_ms=elapsed, rtf=elapsed / (duration * 1000), first_text_ms=first,
                        tail_ms=max(0, elapsed - duration * 1000) if realtime else None, events=events,
                        session_created=created)
        finally:
            ws.close()


class SenseVoice:
    """The exact resident-worker protocol used by SenseVoiceCommandTranscriber."""
    def __init__(self, root, backend, variant, language, vad=True):
        self.root, self.backend, self.variant = root, backend, variant
        self.language, self.vad, self.process, self.log = language, vad, None, None
        self.responses = queue.Queue()

    def response(self):
        try:
            value = self.responses.get(timeout=120)
        except queue.Empty:
            raise TimeoutError("SenseVoice worker response timeout") from None
        if value is None:
            raise RuntimeError(f"SenseVoice worker closed stdout; exit={self.process.poll()}")
        return value

    def start(self):
        exe = self.root / f"runtimes/sensevoice-{self.backend}/llama-funasr-sensevoice.exe"
        model = "sensevoice-small-q8.gguf" if self.variant == "q8_0" else "sensevoice-small-q5_0.gguf"
        args = [str(exe), "-m", str(self.root / "models" / model), "--language", self.language]
        if self.backend == "vulkan":
            args.extend(["--backend", "vulkan", "--device", "0"])
        if self.vad:
            args.extend(["--vad", str(self.root / "models/fsmn-vad.gguf")])
        args.append("--worker")
        self.arguments = args
        self.log = (self.root / "results" / f"{self.log_tag}-server.log").open("w", encoding="utf-8")
        began = time.perf_counter()
        self.process = subprocess.Popen(args, cwd=self.root, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                        stderr=self.log, text=True, encoding="utf-8", bufsize=1,
                                        creationflags=subprocess.CREATE_NO_WINDOW)

        def receive():
            try:
                for line in self.process.stdout:
                    self.responses.put(line.rstrip("\r\n"))
            finally:
                self.responses.put(None)

        self.reader = threading.Thread(target=receive, daemon=True)
        self.reader.start()
        ready = self.response()
        if ready != "READY":
            raise RuntimeError(f"Unexpected SenseVoice startup response: {ready}")
        return (time.perf_counter() - began) * 1000

    def infer(self, pcm, realtime=False):
        wav_path = self.root / "samples/request.wav"
        write_wav(wav_path, pcm)
        duration = len(pcm) / 32000
        encoded = base64.b64encode(str(wav_path).encode("utf-8")).decode("ascii")
        began = time.perf_counter()
        if realtime:
            # Current worker accepts completed files; this is a whole-utterance
            # capture simulation, not native streaming or the app's segmenter.
            time.sleep(duration)
        submitted = time.perf_counter()
        self.process.stdin.write("TRANSCRIBE\t" + encoded + "\n")
        self.process.stdin.flush()
        response = self.response()
        ended = time.perf_counter()
        kind, payload = response.split("\t", 1)
        text = base64.b64decode(payload, validate=True).decode("utf-8").strip()
        no_speech = False
        if kind == "ERROR":
            if "read audio failed" not in text.lower() and any(
                    term in text.lower() for term in ("0 vad segments", "no transcription", "no speech", "没有识别到语音")):
                text, no_speech = "", True
            else:
                raise RuntimeError(f"SenseVoice error: {text}")
        elif kind != "RESULT":
            raise RuntimeError(f"Unknown SenseVoice response: {response}")
        elapsed = (ended - began) * 1000
        return dict(text=text, duration_sec=duration, elapsed_ms=elapsed,
                    request_ms=(ended - submitted) * 1000, rtf=elapsed / (duration * 1000),
                    first_text_ms=elapsed if realtime and text else None,
                    tail_ms=max(0, elapsed - duration * 1000) if realtime else None,
                    no_speech=no_speech, protocol_status=kind,
                    events=[dict(elapsed_ms=elapsed, text=text, final=True)])

    def close(self):
        if self.process:
            try:
                if self.process.poll() is None:
                    self.process.stdin.write("QUIT\n")
                    self.process.stdin.flush()
                    self.process.wait(timeout=2)
            except (OSError, subprocess.TimeoutExpired):
                self.process.kill()
                self.process.wait(timeout=10)
            finally:
                self.process.stdin.close()
                self.reader.join(timeout=2)
                self.process.stdout.close()
        if self.log:
            self.log.close()


def run(args, root):
    import psutil
    is_moon = args.engine == "moonshine"
    is_sense = args.engine == "sensevoice"
    if is_moon:
        # Supported by pinned v0.1.5 ort-utils.cpp; set before loading the DLL.
        os.environ["MOONSHINE_ORT_SINGLE_THREAD"] = "1" if args.moon_single_thread else "0"
    backend = (Moonshine(root) if is_moon else
               SenseVoice(root, args.backend, args.sense_variant, args.sense_language or args.language,
                          not args.no_sense_vad) if is_sense else Nemo(root, args.backend))
    backend.disable_batching = args.disable_batching
    backend.skip_warmup = args.skip_warmup
    backend.endpointing = args.endpointing
    backend.endpoint_ms = args.endpoint_ms
    rows = []
    suffix = (f"-silence{args.silence}" if args.silence else "") + (f"-repeat{args.repeat_audio}" if args.repeat_audio > 1 else "")
    if args.architecture is not None:
        suffix += f"-arch{args.architecture}"
    if is_moon and args.moon_single_thread:
        suffix += "-singlethread"
    if is_sense:
        suffix += "-" + args.sense_variant + ("-no-vad" if args.no_sense_vad else "-vad")
        if args.sense_language:
            suffix += "-lang-" + args.sense_language
    if args.noise_snr_db is not None:
        suffix += f"-noise{args.noise_snr_db:g}db"
    if args.tag:
        if not all(c.isalnum() or c in "-_" for c in args.tag):
            raise ValueError("Tag may contain only letters, digits, hyphens, and underscores")
        suffix += "-" + args.tag
    stem = f"{args.engine}-{args.backend}-{args.language}-{args.mode}{suffix}"
    output = root / "results" / (stem + ".json")
    backend.log_tag = stem
    load_ms = None
    try:
        arch = args.architecture if args.architecture is not None else {"zh": 2, "ja": 4, "en": 4}[args.language]
        load_ms = backend.load(root / f"models/moonshine-{args.language}-{arch}", arch, args.language) if is_moon else backend.start()
        pid = os.getpid() if is_moon else backend.process.pid
        process = psutil.Process(pid)
        pcm = read_wav(args.audio or root / "samples" / f"{args.language}.wav")
        if args.silence:
            pcm = bytes(16000 * 2 * args.silence)
        if args.repeat_audio > 1:
            pcm = (pcm + bytes(32000)) * args.repeat_audio
        if args.noise_snr_db is not None:
            pcm = add_noise(pcm, args.noise_snr_db)
        for iteration in range(args.runs):
            before = process.memory_info().rss
            cpu_before = process.cpu_times()
            if is_moon:
                result = backend.infer(pcm, args.mode != "offline", args.mode == "realtime")
            elif is_sense:
                result = backend.infer(pcm, args.mode == "realtime")
            else:
                locale = {"zh": "zh-CN", "ja": "ja-JP", "en": "en-US"}[args.language]
                result = backend.infer(pcm, locale, args.mode != "offline", args.mode == "realtime")
            memory = process.memory_info()
            cpu_after = process.cpu_times()
            result.update(iteration=iteration, rss_before_bytes=before, rss_after_bytes=memory.rss,
                          process_cpu_seconds=cpu_after.user + cpu_after.system - cpu_before.user - cpu_before.system,
                          peak_rss_bytes=getattr(memory, "peak_wset", memory.rss))
            rows.append(result)
            save(output, dict(engine=args.engine, backend=args.backend, language=args.language,
                              mode=args.mode, load_ms=load_ms, runs=rows, pid=pid,
                              architecture=arch if is_moon else None,
                              moon_single_thread=args.moon_single_thread if is_moon else None,
                              sense_variant=args.sense_variant if is_sense else None,
                              sense_vad=not args.no_sense_vad if is_sense else None,
                              sense_language=(args.sense_language or args.language) if is_sense else None,
                              noise_snr_db=args.noise_snr_db,
                              noise_seed=20260914 if args.noise_snr_db is not None else None,
                              audio=str(args.audio or root / "samples" / f"{args.language}.wav"),
                              submitted_pcm_sha256=hashlib.sha256(pcm).hexdigest(),
                              repeat_audio=args.repeat_audio, silence_sec=args.silence,
                              disable_batching=args.disable_batching, skip_warmup=args.skip_warmup,
                              endpointing=args.endpointing,
                              endpoint_ms=args.endpoint_ms,
                              readiness=getattr(backend, "ready", None)))
            print(json.dumps(dict(engine=args.engine, backend=args.backend, language=args.language,
                                  iteration=iteration, load_ms=load_ms, **{k: result.get(k) for k in
                                  ("text", "duration_sec", "elapsed_ms", "first_text_ms", "tail_ms", "rtf", "rss_after_bytes")}),
                             ensure_ascii=False), flush=True)
        if not is_moon and args.backend == "vulkan":
            # WDDM counters include per-process GPU allocations missing from RSS.
            query = ("$gpuRows=@(Get-CimInstance Win32_PerfFormattedData_GPUPerformanceCounters_GPUProcessMemory | "
                     f"Where-Object Name -Like 'pid_{pid}_*' | "
                     "Select-Object Name,DedicatedUsage,SharedUsage); ConvertTo-Json -InputObject $gpuRows -Compress")
            try:
                gpu = subprocess.run(["pwsh", "-NoProfile", "-Command", query], capture_output=True,
                                     text=True, encoding="utf-8", timeout=15, creationflags=subprocess.CREATE_NO_WINDOW)
                payload = json.loads(output.read_text(encoding="utf-8"))
                payload["gpu_memory_after_runs"] = json.loads(gpu.stdout) if gpu.returncode == 0 else {"error": gpu.stderr}
                save(output, payload)
            except (OSError, ValueError, subprocess.TimeoutExpired) as error:
                print(f"GPU memory unavailable: {error}", flush=True)
    except Exception as error:
        save(output, dict(engine=args.engine, backend=args.backend, language=args.language, mode=args.mode,
                          load_ms=load_ms, runs=rows, error=str(error)))
        raise
    finally:
        backend.close()


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("command", choices=["download-moon", "run"])
    parser.add_argument("--root", default="artifacts/asr-evaluation-20260914")
    parser.add_argument("--engine", choices=["moonshine", "nemo", "sensevoice"], default="moonshine")
    parser.add_argument("--backend", choices=["cpu", "vulkan"], default="cpu")
    parser.add_argument("--language", choices=["zh", "ja", "en"], default="zh")
    parser.add_argument("--mode", choices=["offline", "stream", "realtime"], default="offline")
    parser.add_argument("--runs", type=int, default=3)
    parser.add_argument("--audio")
    parser.add_argument("--tag", default="")
    parser.add_argument("--architecture", type=int, choices=[2, 4, 5])
    parser.add_argument("--disable-batching", action="store_true")
    parser.add_argument("--skip-warmup", action="store_true")
    parser.add_argument("--endpointing", action="store_true")
    parser.add_argument("--endpoint-ms", type=int)
    parser.add_argument("--repeat-audio", type=int, default=1)
    parser.add_argument("--silence", type=int, default=0)
    parser.add_argument("--sense-variant", choices=["q8_0", "q5_0"], default="q8_0")
    parser.add_argument("--sense-language", choices=["auto", "zh", "en", "ja"])
    parser.add_argument("--no-sense-vad", action="store_true")
    parser.add_argument("--noise-snr-db", type=float)
    parser.add_argument("--moon-single-thread", action="store_true")
    args = parser.parse_args()
    if args.runs < 1 or args.repeat_audio < 1 or args.silence < 0:
        parser.error("Runs/repeat-audio must be positive; silence must be nonnegative.")
    if args.endpoint_ms is not None and (args.endpoint_ms <= 0 or not args.endpointing):
        parser.error("Positive endpoint-ms requires --endpointing.")
    if args.engine == "moonshine" and args.backend != "cpu":
        parser.error("This Moonshine trial uses the CPU provider only.")
    if args.engine == "sensevoice" and args.mode == "stream":
        parser.error("The current SenseVoice worker takes completed files, not native streaming PCM.")
    if args.noise_snr_db is not None and not (-20 <= args.noise_snr_db <= 60):
        parser.error("Noise SNR must be finite and between -20 and 60 dB.")
    root = Path(args.root).resolve()
    (root / "results").mkdir(parents=True, exist_ok=True)
    sys.path.insert(0, str(root / "python-libs"))
    sys.stdout.reconfigure(encoding="utf-8")
    if args.command == "download-moon":
        download_moon(root, [(args.language, args.architecture)] if args.architecture is not None else None)
    else:
        run(args, root)
