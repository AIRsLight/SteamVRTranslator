# 与当前 SenseVoice 的直接对照

日期：2026-09-14。**当前建议继续使用 SenseVoice，不因新模型已能运行就增加正式后端。撤回上一轮“优先接入 Nemotron”的建议。**

对照后也不能说新模型完全没有优势：Moonshine 中文 Tiny 经单线程调节后有明确的 CPU/内存优势；Nemotron 有原生增量输出，在一段英文长朗读中也少了几处错误。现有证据支持保留这些实验结果，尚不足以把它们作为通用替代品加入应用。若继续投入，Moonshine 中文轻量档比 Nemotron 通用替代方案更值得小范围验证，但必须先证明其准确性可接受。

## 对照配置

同一台 Windows 11 x64 电脑：i7-14700KF、64 GB RAM、RTX 3060 12 GB，驱动 596.49。沿用上一轮完全相同的中文 5.592 秒、英文 7.152 秒、日文 7.200 秒样本，补测第二段中文、44.374 秒英文、重复中文和静音，并新增 10 dB 合成噪声输入。

SenseVoice **精确使用项目当前 `SenseVoiceAssetCatalog` 中的二进制和模型**，没有换成 sherpa-onnx 或其他实现：

- CPU/Vulkan 常驻 worker：HF revision `677c2591e0b55d58842f0f52c9c82650a031086d`，分别核对项目固定 SHA-256。
- 默认 Q8 权重：254.21 MB，revision `90c1c61912018b70ada0fcc024ea24aca62f2e63`。
- 项目已有 Q5 权重：167.12 MB，revision `d6bfce12fb0369874d357e9ebeed012e6349fd25`。
- 默认启用 FSMN VAD：另加 1.72 MB。通过与 `SenseVoiceCommandTranscriber` 相同的 `READY`、`TRANSCRIBE`/`RESULT`、`QUIT` 协议调用。

这些文件都按项目清单完成大小和 SHA-256 校验。[项目下载清单代码](../src/SteamVRTranslator.App/Speech/SenseVoiceDownloadService.cs)、[SenseVoice 权重来源](https://huggingface.co/FunAudioLLM/SenseVoiceSmall-GGUF)、[worker 来源](https://huggingface.co/AIRsLight/VRChatVoiceInput-Runtimes)

Nemotron 仍为 0.6B Q8_0、NeMo-Speech.cpp v0.1.0，CPU/Vulkan 均关闭批处理；Moonshine 仍为 v0.1.5，另对照其现成的 `MOONSHINE_ORT_SINGLE_THREAD=1`。此开关在加载 DLL 前仅设置到测试进程，未修改系统环境或上游二进制。[单线程开关源码](https://github.com/moonshine-ai/moonshine/blob/v0.1.5/core/ort-utils/ort-utils.cpp)

本轮共 **57 组、189 次转写请求**。每组只启动一个模型，顺序执行；短句基线每组五次，取后四次中位数。三种语言均明确指定。没有运行应用 UI、游戏或翻译，也没有评估说话人模型耗时。以下属于小样本工程对照，不是人工标注语料上的 CER/WER 排名。

## 完整短句：SenseVoice Vulkan 优势很明显

输入为同一完整 WAV，模型已常驻。单位 ms，越低越好；不含加载、录音等待、分段、翻译或显示。

| 模型 / 配置 | 中文 | 英文 | 日文 |
| --- | ---: | ---: | ---: |
| **SenseVoice Q8，CPU＋VAD** | **140** | **158** | **166** |
| SenseVoice Q5，CPU＋VAD | 159 | 201 | 206 |
| **SenseVoice Q8，Vulkan＋VAD** | **52** | **55** | **58** |
| SenseVoice Q5，Vulkan＋VAD | 55 | 57 | 60 |
| Nemotron，CPU | 465 | 671 | 650 |
| Nemotron，Vulkan | 316 | 390 | 388 |
| Moonshine，默认线程，中文 Tiny / 英日 Small | 218 | 2,567 | 1,441 |
| Moonshine，单线程，中文 Tiny / 英日 Small | 101 | 652 | 414 |
| Moonshine，英文 Tiny，默认线程 | — | 1,391 | — |
| Moonshine，英文 Tiny，单线程 | — | 298 | — |

这几段上，Nemotron Vulkan 的完整转写耗时约为 SenseVoice Q8 Vulkan 的 **6–7 倍**。因此其原生流式架构不能被解释成“识别计算更快”。

Moonshine 中文 Tiny 单线程比当前 SenseVoice Q8 CPU 快约 28%，但仍慢于 SenseVoice Vulkan；英文、日文都没有显示出整句速度优势。Q5 主要节省内存和下载量，在本机 CPU 上反而比 Q8 稍慢，不应宣传为加速档。

## 资源：Moonshine 的轻量优势成立，但需要单线程配置

工作集取短句推理后观测范围，十进制 MB；GPU 数值为请求结束后的 WDDM 独立显存快照，**不是峰值**。

| 配置 | CPU 进程工作集 | 独立显存 | 新进程模型加载 / 就绪 |
| --- | ---: | ---: | ---: |
| SenseVoice Q8 CPU | 约 275 MB | — | 约 100–103 ms |
| SenseVoice Q5 CPU | 约 183–184 MB | — | 约 74–77 ms |
| SenseVoice Q8 Vulkan | 约 115–117 MB | 约 275 MB | 约 306–333 ms |
| SenseVoice Q5 Vulkan | 约 111–139 MB | 约 192 MB | 约 247–652 ms |
| Nemotron CPU | 约 1,763–1,788 MB | — | 约 1,632–1,641 ms |
| Nemotron Vulkan | 约 160–161 MB | 约 972–973 MB | 约 1,131–1,148 ms |
| Moonshine 中文 Tiny，单线程 | 约 116 MB | — | 约 128 ms |
| Moonshine 英文 Tiny，单线程 | 约 128 MB | — | 约 122 ms |
| Moonshine 英文 Small，单线程 | 约 251 MB | — | 约 276 ms |
| Moonshine 日文 Small，单线程 | 约 239 MB | — | 约 315 ms |

Moonshine 的工作集包含 Python 测试驱动，SenseVoice/Nemotron 只统计推理子进程，因此不能把这些值直接当成应用整合后的增量。启动测试没有清空系统文件缓存。

单线程开关还改变了 CPU 消耗。中文短句单次请求的**所有线程累计 CPU 时间**中位数：

| 配置 | CPU 秒 / 请求 |
| --- | ---: |
| SenseVoice Q8 CPU | 1.008 |
| SenseVoice Q5 CPU | 1.125 |
| Moonshine 中文 Tiny，默认线程 | 6.039 |
| Moonshine 中文 Tiny，单线程 | 0.086 |

因此，“Moonshine 权重小，所以默认就省 CPU”的判断不成立；但使用其现有单线程开关后，CPU 用量明显下降。这是它最实在的潜在收益。尚未实测 VR 游戏帧时间，也未优化或重新编译 SenseVoice 的线程配置，不能把累计 CPU 秒直接换算成帧率提升。

## 质量：没有看到新模型全面胜出的证据

### 干净短句

- 中文：SenseVoice Q8/Q5、CPU/Vulkan 五次结果均为“开放时间早上9点至下午5点”；Nemotron 也正确。Moonshine 中文首轮曾正确，后续多次把“至”写成“四”，单线程没有修正此问题。
- 英文：SenseVoice 和 Nemotron 都识别出 `chieftain`、`fifty/50`。Moonshine Small 写成 `thief then`，Tiny 写成 `chief then`；Tiny 实时模式还把 `fifty` 写成 `shifty`。
- 日文：SenseVoice 和 Moonshine 写出“弁当制”，Nemotron 为“弁当性”。SenseVoice 原始结果有额外空格；数字、空格和标点差异应先规范化，再评价文字错误。
- 第二段中文：三者均识别出“甚至出现交易几乎停滞的情况”。SenseVoice Q8 CPU/Vulkan 分别约 101 / 44 ms；上一轮 Nemotron Vulkan、Moonshine 默认线程均约 232–234 ms。

还单独关闭 SenseVoice VAD 做了对照：它依然很快，但首个中文样本变成“开饭时间”。这说明应保留项目实际的 Q8＋VAD 基线，不能随意去掉前处理后据此评价现有体验。

### 长句与重复语音

44.374 秒英文采用完整 WAV 转写，每组三次，取后两次中位数：

| 配置 | 处理耗时 |
| --- | ---: |
| SenseVoice Q8 CPU | 1.09 s |
| SenseVoice Q8 Vulkan | 0.267 s |
| Nemotron Vulkan | 2.33 s |
| Moonshine 英文 Tiny，默认线程 | 7.49 s |
| Moonshine 英文 Tiny，单线程 | 约 2.3 s |

SenseVoice 把 `spring` 写成 `string`，把 `noisiest` 写成 `noist`；Nemotron 正确识别了这两个位置。Moonshine 也识别对这两个词，但把 `going direct` 写成 `going to act`，另有 `that`/`but` 等变化。**这段中 Nemotron 存在局部质量收益**，但只有一个样本，不能推导为英文整体更准。

32.96 秒重复中文的完整 WAV，SenseVoice CPU/Vulkan 都连续返回五句正确结果，约需 656 / 250 ms。Moonshine 单线程完整转写仍反复把“至”写成“四”。上一轮 Nemotron 的实时自动断句在默认 800 ms 阈值下误切，改为 2 秒后段数改善，仍有错漏字。这里同时存在整段与实时模式的差异，只能说明各自路径的实际表现，不能当作受控的流式精度排名。

### 合成噪声和静音

对三种语言的原始短句添加相同的 10 dB 高斯噪声，以整段 RMS 定义信噪比；不同后端收到的 PCM SHA-256 已核对一致。这是压力输入，不代表游戏音效。

- 中文：SenseVoice 将开头写成“派饭”，Nemotron 出现“嗨，半时间”，Moonshine 出现“快报”“早报”“十点”等更多变化。
- 英文：三者均退化。SenseVoice 的主要错误集中于 `chieftain`；Nemotron 还把 `boy` 附近写成 `government`；Moonshine 出现数词和其他名词变化。
- 日文：Moonshine Small 保持了干净样本的文字；SenseVoice 出现“持っていきない”，Nemotron 保留“弁当性”。因此 Moonshine 在这一输入上也有局部质量优势，不能概括成它所有语言都更差。

上述噪声测试使用 Moonshine 默认线程配置，一次请求用于观察文本，未用于速度排名。SenseVoice CPU/Vulkan 的 8 秒纯零值音频均没有产生文字，约 18 / 16 ms 完成；上一轮两个新模型也通过了这一静音测试。

## 原生流式的收益是什么

同一中文音频按时钟输入，文件起点包含开头静音：

| 路径 | 首次非空文字 | 最后一个源采样点到最终返回 |
| --- | ---: | ---: |
| SenseVoice Q8 Vulkan，等待整句后提交 WAV | 约 5.8 s | 约 0.2 s |
| Nemotron Vulkan，连续 PCM | 约 1.6 s | 约 0.17–0.2 s |
| Moonshine 中文 Tiny 单线程，连续 PCM | 1.823 s | 26 ms |

SenseVoice 当前 worker 接收完整文件，没有原生增量返回；新模型可以更早显示临时字幕，**这是一项确实存在的能力差异**。但临时文字可能修订，翻译也不能把每次增量都当最终句。最终确认快慢还受断句配置影响。

上表 SenseVoice 仅为整句等待模拟，没有运行应用自身的分段器。当前应用还会按约 650 ms 静音或最长 12 秒切段，并进行说话人处理；其真实端到端延迟需要另外测量。不能用这个表声称所有实际字幕都会提前固定几秒。

Moonshine 单线程也显著缩短了英文实时收尾：Small 约 152 ms，Tiny 约 158 ms；日文 Small 约 7 ms。这修正了上一轮仅使用默认线程时英文约 1–2 秒收尾的局限，但识别错误仍在。

## 是否接入

| 目标 | 当前决定 |
| --- | --- |
| 替换 SenseVoice，提升当前中英日 PTT 的综合体验 | **不接入新默认模型**。SenseVoice Vulkan 更快，资源较低，已测短句质量也好。 |
| 为当前字幕增加一个通用 Nemotron 后端 | **暂缓**。增量输出和个别英文收益存在，但要承担更多资源、已复现的默认 Vulkan 启动问题及断句验证成本。 |
| 为纯 CPU、中文、低资源设备提供 Moonshine Tiny | **保留候选，有条件验证**。资源优势已测得，是否值得正式接入取决于真实中文游戏语音的错字率能否接受。 |

现阶段保留已下载文件和可复跑测试，不增加应用模型入口或新的运行时维护负担。没有证明综合收益之前，继续以现有 SenseVoice 为主。

复现与数据：[脚本说明](../tools/asr-evaluation/README.md)、[固定来源与校验值](../tools/asr-evaluation/sources.json)、[本轮完整结果摘要](ASR_SENSEVOICE_COMPARISON_2026-09.results.json)。原始逐块事件与 worker 日志在本机 `artifacts/asr-evaluation-20260914/results`。
