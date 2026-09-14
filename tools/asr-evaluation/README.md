# Windows 原生 ASR 试跑

对照项目当前 SenseVoice，试跑 Nemotron 3.5 ASR Streaming 0.6B 和 Moonshine Streaming。最新结果见 [SenseVoice 对比报告](../../docs/ASR_SENSEVOICE_COMPARISON_2026-09.md)，早期试跑见 [实测报告](../../docs/ASR_NATIVE_TRIAL_2026-09.md)。这些脚本是开发测试工具，尚未注册为应用内 ASR 后端。

推理分别由 `nemo-speech.exe` 和 `moonshine.dll` 完成。Python 仅负责下载、提供音频、调用接口和记录结果，不加载 PyTorch 模型。最终应用可由 C# 调用同一 HTTP/WebSocket 或 C ABI，无须让普通用户安装 Python。

## 准备

在项目根目录用 PowerShell 7 执行，需要 Windows x64、64 位 Python 3.11+、`curl.exe`；本次使用 Python 3.13.5。Vulkan 测试还需要支持 Vulkan 的显卡驱动。

```powershell
python tools/asr-evaluation/prepare.py
```

`sources.json` 固定了运行时、模型和样本的 URL、大小与 SHA-256。首次下载合计约 1.63 GB，解压还需要额外空间。文件保存在 `artifacts/asr-evaluation-20260914`，辅助 Python 包安装到该目录的 `python-libs`。再次执行会校验已有文件并重新解压运行时；应先结束本工具的测试。`--skip-dependencies` 可跳过辅助包安装。

准备过程使用以下固定版本：

- SenseVoice CPU/Vulkan worker、Q8/Q5 和 FSMN VAD，精确匹配项目 `SenseVoiceAssetCatalog` 的 revision、大小和 SHA-256。
- NVIDIA NeMo-Speech.cpp v0.1.0，Windows CPU 和 Vulkan ZIP。
- Nemotron 3.5 ASR Streaming 0.6B Q8_0，Hugging Face revision `ea30d66debe3740a08b573244286791d423d6b3e`。
- Moonshine Voice v0.1.5 的官方 Windows wheel，解压获得原生 DLL；没有安装其 Python SDK。
- Moonshine 中文 Tiny、日文 Small、英文 Tiny/Small 的流式量化权重；具体文件固定在清单中。
- `psutil==7.2.2`、`websocket-client==1.9.0`、`google-crc32c==1.7.1`，仅用于测试驱动。

Moonshine 模型清单最初通过原生 `moonshine_get_stt_dependencies` 获取，下载后验证官方大小与 CRC32C，再记录 SHA-256。`prepare.py` 重放固定清单；`evaluate.py download-moon` 是重新查询当前 DLL 清单的辅助命令，复现本报告请使用 `prepare.py`。

## 短句与流式测试

```powershell
# 模型加载一次，连续转写同一完整 WAV 三次
python tools/asr-evaluation/evaluate.py run --engine sensevoice --backend cpu --language zh --mode offline --runs 5 --tag baseline
python tools/asr-evaluation/evaluate.py run --engine sensevoice --backend vulkan --language zh --mode offline --runs 5 --tag baseline
python tools/asr-evaluation/evaluate.py run --engine sensevoice --backend cpu --sense-variant q5_0 --language zh --mode offline --runs 5 --tag baseline
python tools/asr-evaluation/evaluate.py run --engine nemo --backend cpu --language zh --mode offline --runs 3
python tools/asr-evaluation/evaluate.py run --engine nemo --backend vulkan --language zh --mode offline --runs 3 --disable-batching
python tools/asr-evaluation/evaluate.py run --engine moonshine --language zh --mode offline --runs 3

# v0.1.5 支持的单线程开关，在此电脑上明显降低 Moonshine CPU 耗时
python tools/asr-evaluation/evaluate.py run --engine moonshine --language zh --mode offline --runs 5 --moon-single-thread --tag baseline

# 每 200 ms 提交音频，模拟实时采集；结束后等待最终结果
python tools/asr-evaluation/evaluate.py run --engine nemo --backend vulkan --language en --mode realtime --runs 1 --disable-batching
python tools/asr-evaluation/evaluate.py run --engine moonshine --language en --architecture 2 --mode realtime --runs 1

# 五次中文短句，句间增加 1 秒静音，合计 32.96 秒
python tools/asr-evaluation/evaluate.py run --engine nemo --backend vulkan --language zh --mode realtime --runs 1 --disable-batching --repeat-audio 5 --endpointing --endpoint-ms 2000 --tag continuous-eou2000
python tools/asr-evaluation/evaluate.py run --engine moonshine --language zh --mode realtime --runs 1 --repeat-audio 5 --tag continuous

# 纯静音和自选音频；自选 WAV 必须为单声道、16 kHz、PCM16
python tools/asr-evaluation/evaluate.py run --engine moonshine --language zh --mode stream --silence 8 --runs 1 --tag silence
python tools/asr-evaluation/evaluate.py run --engine nemo --backend vulkan --language en --mode stream --runs 1 --disable-batching --audio artifacts/asr-evaluation-20260914/samples/en-long.wav --tag long-english
```

已有上一轮下载文件时，只补齐 SenseVoice：

```powershell
python tools/asr-evaluation/prepare.py --family sensevoice --skip-dependencies
```

SenseVoice 默认 Q8、启用 FSMN VAD；使用与应用相同的 `--worker`、`READY`、`TRANSCRIBE`/`RESULT` Base64 协议，同一进程顺序复用权重。`--no-sense-vad` 用于单独对照关闭 VAD；`--sense-language auto` 可单独测试自动语言。本轮主要比较明确指定的中英日语言。

SenseVoice 的 `realtime` 只模拟等待整句音频后提交 WAV，不能作为原生流式测试。它不调用应用的 650 ms 静音分段器、说话人模型、翻译或 UI。SenseVoice 的 `stream` 模式会被拒绝。

Moonshine `--moon-single-thread` 在加载 DLL 前设置本测试进程的 `MOONSHINE_ORT_SINGLE_THREAD=1`，不改系统环境变量。开关由固定 v0.1.5 原生源码支持，会将 ORT 会话的算子内/算子间线程数设为 1；无此参数则显式使用默认多线程配置。对比报告同时保留两种配置，不能把默认配置的较慢结果当作调优后的上限。[固定源码](https://github.com/moonshine-ai/moonshine/blob/v0.1.5/core/ort-utils/ort-utils.cpp)

`--noise-snr-db 10` 可在同一 WAV 上叠加确定性的高斯噪声：随机种子 `20260914`，以整段音频 RMS 定义 SNR，并整体缩放避免削波。这是合成压力输入，不代表游戏噪声。结果保存实际提交 PCM 的 SHA-256，可核对不同后端是否接收同一输入。

`--language` 支持 `zh`、`en`、`ja`。Moonshine 默认中文 Tiny（架构 2），日文与英文 Small（架构 4），英文 Tiny 需指定 `--architecture 2`。这次固定模型清单不包含架构 5 的 Medium 权重。

两种引擎必须逐个运行，避免 CPU/GPU 争用污染计时。工具的结果文件名由引擎、设备、语言、模式和 `--tag` 等组成；相同名字会覆盖，变更配置时使用不同 `--tag`。两个工具均支持 `--root` 指定测试目录。

## 指标和范围

- `offline`：整段音频已就绪后的完整转写耗时；不含模型加载。最初试跑采用三次请求中后两次的中位数；SenseVoice 对照短句采用五次请求中后四次的中位数，都不是 p95。
- `stream`：尽快提交 200 ms 音频块，测流式计算吞吐。此模式的 `first_text_ms` **不是实时采集到首字的延迟**。
- `realtime`：按音频时钟提交块。`first_text_ms` 从文件起点计时，包括开头静音；`tail_ms` 从最后一个源音频采样点到最终返回。它既不是语音实际停止点到字幕显示的耗时，也不含翻译。
- `realtime` 的 `elapsed_ms`/RTF 含等待音频输入的时间，不能与离线计算 RTF 直接比较。
- `load_ms`：Moonshine 的原生加载调用耗时；Nemotron 为启动独立进程至 `/ready`。未清空系统文件缓存，不能称为冷磁盘加载速度。
- `rss_after_bytes`：推理后进程工作集；Moonshine 包含 Python 测试驱动，Nemotron 只取服务器。GPU 的 `DedicatedUsage`/`SharedUsage` 为请求结束后的 WDDM 进程计数，**不是峰值显存**。内存值不可直接当作应用整合后的增量。
- `process_cpu_seconds`：请求期间该推理进程消耗的用户态＋内核态 CPU 秒数，是所有线程的合计；不等于墙钟耗时，也不是游戏帧时间。SenseVoice 只取 worker，不含 Python 驱动。
- `events`：保留原始增量/最终文字与计时。Nemotron 收齐 `input_audio_buffer.committed`，不会在第一句 `.completed` 后丢弃后续句子。

每个试验中的模型保持加载状态，Moonshine 每轮创建独立 stream，Nemotron 每轮创建新 WebSocket；结束后释放本工具持有的句柄或服务器进程。这只是顺序复用测试，尚未覆盖 PTT 与字幕并发、加载中取消、设备重连或长时间泄漏测试。

## 已复现的限制

NeMo-Speech.cpp v0.1.0 Vulkan 在默认批处理/预热配置下曾启动失败，返回 `0xC0000409`，日志含 `GGML_ASSERT(ne3 == ne13) failed`。这台 RTX 3060 上禁用批处理后，多次独立启动成功：

```text
--asr.batching.enabled=false
```

本工具的 `--disable-batching` 会传入上述参数。布尔值需使用 `=false`；传成独立的 `false` 参数会导致 CLI 用法错误。`--skip-warmup` 也曾规避崩溃，但会把部分初始化工作移入首次识别；报告优先使用保留预热、关闭批处理的结果。

自动断句默认关闭；开启后的默认 token 静默阈值为 800 ms。本次连续中文样本误切成 11 段，改成 2000 ms 后得到 5 段，但仍有错漏字。提高阈值会增加确认等待，不能视为普遍适用的修复。

样本来自公开模型/项目仓库。`zh-long.wav` 实际仅 4.20 秒，是第二段中文样本；它来自 Qwen 的公开示例音频，本次没有运行 Qwen 模型。`en-long.wav` 为 Moonshine 项目的 44.37 秒朗读。重复短句与纯静音是人工构造的边界输入，没有游戏混音或人工标注语料，因此不输出 CER/WER 排名。
