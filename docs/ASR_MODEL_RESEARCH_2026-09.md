# ASR 模型接入调研

调研日期：2026-09-14。范围：Windows x64 本地语音输入、游戏实时字幕，以及现有独立 ASR 服务的扩展。

最初查阅官方模型卡、论文、运行时文档与发布包清单，并检查项目接入点。随后实际试跑 Nemotron 与 Moonshine，并补齐了当前 SenseVoice Q8/Q5 的同机对照。**最新接入判断以 [SenseVoice 对比报告](ASR_SENSEVOICE_COMPARISON_2026-09.md) 为准**；仅比较两个新候选的早期记录见 [原生实测报告](ASR_NATIVE_TRIAL_2026-09.md)。

## 建议

补齐 SenseVoice 基线后，撤回“优先接入 Nemotron”的初步建议：

1. **继续使用现有 SenseVoice**：已测中英日短句的综合表现较好，Vulkan 完整转写速度和显存占用明显优于 Nemotron。
2. **Moonshine 中文 Tiny 保留为轻量候选**：现成的单线程开关带来 CPU/内存优势，但中文样本仍有反复错字。先证明真实游戏语音质量可接受，再决定是否正式接入。
3. **Nemotron 暂缓正式接入**：原生增量输出和个别英文长句有收益，但当前缺乏足以抵消资源、断句和运行时维护成本的综合证据。

**Qwen3-ASR 已由用户测试，反馈不适合直接本地运行，本轮不再投入接入验证。** 若后续重点转向中文方言和口音，可再测 Fun-ASR-Nano。当前小样本试跑还不足以改变默认模型。

## 候选比较

| 候选 | 已核实的变化与能力 | 本项目价值 | 接入判断 |
| --- | --- | --- | --- |
| Qwen3-ASR 0.6B / 1.7B | 2026-01 发布；30 种语言、22 种中文方言，包含中英日；官方提供离线与流式方案 | 保留调研资料 | 用户已测试并排除直接本地运行，本轮不再验证。[官方仓库](https://github.com/QwenLM/Qwen3-ASR) |
| Nemotron 3.5 ASR Streaming 0.6B | 2026-06 发布；缓存式 FastConformer-RNNT，80–1120 ms 可选音频块 | 原生增量输出候选 | SenseVoice 对照后暂缓正式接入；40 个语言/地区中，32 个可直接转写，另 8 个需微调。[模型卡](https://huggingface.co/nvidia/nemotron-3.5-asr-streaming-0.6b) |
| Fun-ASR-Nano-2512 | 约 800M；中英日、中文方言；新增原生 GGUF 部署路径 | 中文口语质量备选，部署不必绑定 Python | 第二批对照；31 语言的 MLT-Nano 是另一套权重。[官方仓库](https://github.com/QwenAudio/Fun-ASR) |
| Moonshine Streaming | 英文 34M/123M/245M；中文有 34M，日文有 34M/123M 流式模型 | 低资源、低延迟候选 | 适合作为轻量选项；不同语言需不同模型，不能直接承担自动混合语言识别。[模型列表](https://moonshine-voice.readthedocs.io/en/latest/models/available-models/) |
| Cohere Transcribe 2B | 2026-03；14 语言包含中英日；Conformer 编码器与较小解码器 | 单一已知语言的质量对照 | 暂列后备；官方明确没有自动语言检测、原生时间戳和说话人分离。[官方文档](https://docs.cohere.com/docs/transcribe) |
| Voxtral Mini 4B Realtime | 2026-02；原生流式，13 语言包含中英日 | 高性能设备或独立服务器字幕候选 | 参数规模较大；官方建议的 480 ms 延迟设置不等于游戏端总延迟。[模型卡](https://huggingface.co/mistralai/Voxtral-Mini-4B-Realtime-2602) |
| FireRedASR2S | 2026-02 发布系统；ASR 支持中英、方言和混说，另配 VAD/LID/标点模块 | 中文专项对照 | 优先级较低：ASR 的语言覆盖不能用其 LID/VAD 的“100+ 语言”代替，也没有日语覆盖承诺。[官方仓库](https://github.com/FireRedTeam/FireRedASR2S) |

GLM-ASR-Nano-2512 也有低声量、中文方言方面的研究价值，但在已有上述候选的情况下，暂不扩大第一轮验证范围。[官方介绍](https://github.com/zai-org/GLM-ASR)

## 最新发布：Qwen-Audio-3.0-ASR

2026-09-07 提交的技术报告介绍了新的 Qwen-Audio-3.0-ASR，包括流式变体、热词、上下文和实体识别。它与 1 月开源的 **Qwen3-ASR-0.6B/1.7B** 应分别看待。[技术报告](https://arxiv.org/abs/2609.07549)

官方项目页目前给出 Flash、Filetrans、Flash-Streaming 三种服务入口；本次未确认可供本地分发的同名权重。应先作为云端可选后端评估，不能列成已具备一键本地下载条件的模型。[项目页](https://qwenaudio.github.io/qwen-audio-3.0-asr/)

Flash-Streaming 有 WebSocket API，C# 可直接实现连接，但协议需要单独适配，并需要用户提供 API Key、联网和承担服务费用；本报告未评估具体价格。[官方接口](https://help.aliyun.com/zh/model-studio/fun-asr-realtime-websocket-api)

## 原生部署的关键边界

### Qwen3-ASR：保留原接入调查，当前暂停验证

本项目的 `VibeVoiceRuntimeManager` 固定使用 CrispASR **v0.8.15**。该版本的源码文档已经列出 `qwen3` / `qwen3-1.7b`，并提供 Vulkan 设备选择。CrispASR 是第三方原生移植，不是 Qwen 官方推理实现；这说明有现成接入路径，不代表当前下载的每种二进制都已完成模型验证。[固定版本文档](https://github.com/CrispStrobe/CrispASR/blob/v0.8.15/README.md)

官方 `qwen-asr` 的流式功能目前通过 vLLM 提供。CrispASR 中的分块处理、token 输出和编码器增量计算应分别核实；功能表中的 Qwen3 未声明真正的 token 级流式输出，不能承诺接入后自动具备原生实时字幕。[官方流式说明](https://github.com/QwenLM/Qwen3-ASR#streaming-inference)、[CrispASR 功能表](https://github.com/CrispStrobe/CrispASR/blob/main/docs/feature-matrix.md)

以上为原有接入路径调查，用户反馈后暂不实施。精确字词时间戳的 ForcedAligner 是额外模型；若未来重新评估，普通 PTT 文本输出也不需要默认加载它。[官方模型卡](https://huggingface.co/Qwen/Qwen3-ASR-0.6B)

### Nemotron：官方 Windows 原生方案已存在

NeMo-Speech.cpp **v0.1.0（2026-08-19）** 是首个正式版本，发布页列出原生 C API、HTTP 转写及实时 WebSocket，以及 CPU/CUDA/Vulkan/Metal 支持。已通过 GitHub 发布资产 API 核实 Windows x86-64 的 CPU、CUDA、Vulkan ZIP 均存在，模型单独下载。该版本仍明确提示接口可能变化。[发布页](https://github.com/NVIDIA/NeMo-Speech.cpp/releases/tag/v0.1.0)

因此不能因为模型来自 NVIDIA 就推断它只能在 NVIDIA 显卡运行；实际 AMD/Intel Vulkan 的速度、算子覆盖和稳定性仍需测量。接入建议采用独立进程与固定版本，优先测试其本机 WebSocket PCM 转写入口。[安装文档](https://github.com/NVIDIA/NeMo-Speech.cpp/blob/v0.1.0/docs/install.md)、[服务文档](https://github.com/NVIDIA/NeMo-Speech.cpp/blob/v0.1.0/docs/server.md)

语言卡将日语、英语列为 transcription-ready，普通话列为 broad-coverage。它们都能直接转写，但中文效果应独立评价。80 ms 是音频块配置，不是包含采集、识别、断句、翻译和显示在内的延迟保证；H100 高并发数据也不能直接外推到游戏电脑。[模型卡](https://huggingface.co/nvidia/nemotron-3.5-asr-streaming-0.6b)

### Fun-ASR-Nano：注意总大小与“流式”的含义

官方 GGUF 包的 **484 MB 是 Q4 解码器文件**，还要配约 470 MB 编码器，总计约 954 MB，再加 VAD。Q8 解码器约 805 MB，配套合计约 1.28 GB。文件大小不是推理时内存或显存占用。[GGUF 模型卡](https://huggingface.co/FunAudioLLM/Fun-ASR-Nano-GGUF)

原生 `--stream` 支持常驻模型和 PARTIAL/LOCKED 输出，其说明是周期性解码当前语音窗口，与 Nemotron 缓存编码器状态的方式不同。FunASR 的 Vulkan 发布说明明确提到的是 SenseVoiceSmall 加速，尚不能据此认定 Nano 的整条编码器/解码器路径都已验证 Vulkan。[原生实现说明](https://github.com/QwenAudio/Fun-ASR/blob/main/runtime/llama.cpp/README.md)、[运行时发布说明](https://github.com/modelscope/FunASR/releases/tag/runtime-llamacpp-v0.2.6)

### Moonshine：适合轻量档，需按语言选择

当前模型列表已提供 MIT 许可的中文、日文流式模型，不能沿用“非英语都不可商用”或“只有英语流式”的旧结论。旧的非英语非流式模型仍有不同许可；分发时需要锁定具体文件。其 ONNX Runtime 格式及 C API 有利于 Windows 集成，但本次未做 .NET 调用验证。[模型与许可](https://moonshine-voice.readthedocs.io/en/latest/models/available-models/)、[C API](https://moonshine-voice.readthedocs.io/en/latest/api/c-api/)

## 本项目实际需要改什么

以下是代码检查后的工程判断，不是上游已经提供的功能。

| 当前接入点 | 限制 | 建议 |
| --- | --- | --- |
| `src/SteamVRTranslator.VibeVoice.Server/VibeVoiceRuntimeManager.cs` | 固定 VibeVoice 模型文件、下载地址与启动方式 | 抽出模型目录项和运行时能力，保留下载校验、独立进程及常驻复用 |
| `src/SteamVRTranslator.App/Subtitles/VibeVoiceApiTranscriber.cs` | 固定请求 `model=vibevoice-asr-q4_k`、`verbose_json` | 参数化模型；按后端适配字段和返回格式，不能只换 URL |
| `src/SteamVRTranslator.App/Subtitles/SubtitleProcessingContext.cs` | 本地 SenseVoice 与 VibeVoice API 两条路径 | 引入明确的后端能力：完整音频转写、持续流式、时间戳、说话人标签 |
| `src/SteamVRTranslator.App/Speech/SenseVoiceModelPool.cs` | 面向 SenseVoice 文件转写、单任务推理队列 | 复用生命周期设计；新模型需自己的适配器，持续流式还要独立会话状态 |
| `src/SteamVRTranslator.App/Subtitles/ProcessLoopbackAudioCapture.cs` | 当前字幕主流程消费已完成的音频段 | 真正边说边出需新增连续 PCM 消费路径及 partial/final 更新 |

共享模型权重与共享流式状态是两回事。PTT 和字幕可以复用模型，但每个流的缓存、断句、取消、时间轴必须隔离；不能把两路音频写进同一个流式上下文。也不能让一条无限持续的字幕请求长期占住当前串行队列，导致 PTT 饿死。

字幕中的 Pyannote/ERes2Net 承担分段和说话人处理，换 ASR 不会自动替代它们。保留字幕专属按需常驻；普通语音输入只加载所选 ASR。若采用运行时附带的其他说话人模型，应作为独立变化验证，避免重复加载两套。

原生运行时和权重可继续使用程序内的下载入口，按固定版本与 SHA-256 安装到便携目录；用户不应为普通本地模式自行安装 Python、vLLM 或编译工具链。

## 建议的验证门槛

SenseVoice、Nemotron 与 Moonshine 的公开样本、重复语音、纯静音和合成噪声对照已完成，结果见 [对比报告](ASR_SENSEVOICE_COMPARISON_2026-09.md)。下面是成为默认后端前的完整验证门槛，目前尚未完整执行；Qwen3-ASR 不再列入本轮计划。

- **音频**：同一批中英日短句、游戏音乐/特效背景、轻声、口音、人名与游戏术语、纯静音、连续说话和多人交叠；PTT 使用约 2–10 秒片段，字幕另测至少 30 分钟连续音频。
- **质量**：中文/日文 CER、英文 WER；额外记录专有名词错误、漏句、静音幻觉、重复文字；统一文本规范化规则，保留原始输出。人工标注游戏样本，不能只引用朗读语料榜单。
- **延迟**：冷启动、热启动、PTT 松键到最终文字、流式首个可见文字、句末最终确认、翻译后显示，分别记录 p50/p95；不能用服务器批处理吞吐量代替这些指标。
- **资源**：CPU、独显和 AMD/Intel 集显 Vulkan 分开测；记录模型下载量、工作集、峰值显存、持续运行增长，以及游戏帧时间变化。
- **生命周期**：加载中取消、快速开关、音频设备重连、模型故障恢复、字幕与 PTT 同开；确认模型复用、结果归属、队列上限和退出释放。

成为默认模型前，应在目标硬件上证明目标语言质量提升，且不会产生持续积压或明显影响游戏。可把“处理耗时/音频时长小于 1”作为基本实时门槛，并预留游戏负载余量；若某个模型只在质量或轻量方面有优势，应提供明确的可选模式。

本次结论：**保留 SenseVoice 主力后端，暂不增加正式模型入口。Moonshine 中文 Tiny 有资源优势，可保留为有条件验证的轻量候选；Nemotron 的局部收益尚不足以支持当前正式接入。**
