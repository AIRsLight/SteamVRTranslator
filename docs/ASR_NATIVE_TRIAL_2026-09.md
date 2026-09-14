# Nemotron 与 Moonshine Streaming 原生实测

日期：2026-09-14。已按项目需求实际下载并运行两类模型。Qwen3-ASR 根据用户已有测试结果排除，本轮不再测试其本地推理。

**后续修正：补齐当前 SenseVoice 的同机对照后，撤回本轮“优先接入 Nemotron”的初步建议，继续使用 SenseVoice。** Moonshine 中文 Tiny 经单线程调节后有资源优势，可保留为轻量候选。最新决定和 189 次对照请求见 [SenseVoice 对比报告](ASR_SENSEVOICE_COMPARISON_2026-09.md)。

下文保留最初仅测试两个新候选时的数据，包括 Moonshine 默认多线程配置的结果。后续已找到现成的单线程开关并显著降低其英文等待，不能把这里的较慢数值视为调优后的性能上限。

本轮交付独立测试工具、固定下载清单和测量记录，尚未在应用的模型选择、下载界面或字幕管线中注册新后端。

## 环境与方法

机器为 Windows 11 x64、i7-14700KF（20 核/28 线程）、64 GB 内存、RTX 3060 12 GB，显卡驱动 596.49。Nemotron 使用官方 NeMo-Speech.cpp v0.1.0 CPU/Vulkan 运行时；Moonshine 使用 v0.1.5 Windows 原生 DLL 的 CPU 执行提供程序。Python 3.13.5 只负责测试驱动，不参与模型推理计算。

结果摘要收录 37 组实验记录，其中 35 组完成转写、2 组在默认 Vulkan 配置下启动失败；完成 64 次转写请求。样本只有公开中文、英文、日文短句、第二段中文、44.37 秒英文朗读，以及构造的重复短句/纯静音。没有运行游戏、翻译或字幕显示，没有人工标注评测集，**不据此输出 CER/WER 或宣称比 SenseVoice 更准确**。

模型和二进制来源见 [固定下载清单](../tools/asr-evaluation/sources.json)。所有清单文件已完成大小与 SHA-256 校验。Moonshine 模型最初另按原生清单验证 CRC32C。

| 实际模型 | 权重总大小（十进制 MB） | 本轮执行方式 |
| --- | ---: | --- |
| Nemotron 3.5 ASR Streaming 0.6B Q8_0 | 742.09 | 独立 EXE，HTTP 完整转写 / WebSocket 流式 |
| Moonshine 中文 Tiny Streaming 34M | 32.29 | DLL C ABI，CPU |
| Moonshine 英文 Tiny Streaming 34M | 45.23 | DLL C ABI，CPU |
| Moonshine 英文 Small Streaming 123M | 142.30 | DLL C ABI，CPU |
| Moonshine 日文 Small Streaming 123M | 121.80 | DLL C ABI，CPU |

Nemotron 一套权重覆盖多语言，本轮明确指定 `zh-CN`、`en-US`、`ja-JP`。Moonshine 按语言使用独立权重；本轮未验证自动语言检测或同一句混合语言识别。[Nemotron 模型卡](https://huggingface.co/nvidia/nemotron-3.5-asr-streaming-0.6b)、[Moonshine 模型列表](https://moonshine-voice.readthedocs.io/en/latest/models/available-models/)

## 完整短句转写

同一模型保持加载，每段请求三次，下表取后两次的中位数。计时从完整音频就绪后开始，不含加载、录音、翻译和 UI。中文 5.592 秒，英文 7.152 秒，日文 7.200 秒。单位毫秒，数值越低越快。

| 引擎 | 中文 | 英文 | 日文 |
| --- | ---: | ---: | ---: |
| Nemotron CPU | 456 | 665 | 642 |
| Nemotron Vulkan，关闭批处理 | 313 | 390 | 405 |
| Moonshine CPU，中文 Tiny / 英日 Small | 218 | 3,002 | 1,432 |
| Moonshine CPU，英文 Tiny | — | 976 | — |

第二段 4.204 秒中文，Nemotron Vulkan 为 232 ms，Moonshine 中文 Tiny 为 234 ms，均识别出“甚至出现交易几乎停滞的情况”；Nemotron 原始返回在汉字间插入了空格，后续接入须验证中文文本规范化。

44.374 秒英文朗读采用**不等待音频时钟的流式提交**，单次测得 Nemotron Vulkan 2.49 秒、Moonshine 英文 Tiny 20.60 秒。计算耗时/音频时长分别为 0.056、0.464；它们是这段素材上的吞吐结果，不是实时首字延迟，也不含实际游戏负载。

## 模拟实时输入

每 200 ms 提交一块 PCM。首字从**文件开始**计算，包含原文件开头静音；收尾从**源音频最后一个采样点**到最终返回计算，不等同于人停止说话到完整字幕出现。下表每项为一次样本测试，不能当作延迟分布或 p95。

| 引擎与语言 | 首次非空文字 | 输入结束后收尾 |
| --- | ---: | ---: |
| Nemotron CPU：中文 / 英文 / 日文 | 1,640 / 1,637 / 1,073 ms | 215 / 219 / 215 ms |
| Nemotron Vulkan：英文，关闭批处理 | 1,625 ms | 181 ms |
| Moonshine 中文 Tiny | 1,877 ms | 54 ms |
| Moonshine 英文 Small | 2,309 ms | 1,925 ms |
| Moonshine 英文 Tiny | 2,006 ms | 1,070 ms |
| Moonshine 日文 Small | 1,477 ms | 545 ms |

Nemotron Vulkan 中文另用跳过预热的配置成功跑了两次，收尾为 209 / 97 ms。该配置不同，不混入上表。其低延迟优势应继续在统一配置和更多音频上复核。

`realtime` 模式总耗时含等待采集的时间，因此日志中的 RTF 约为 1 是正常的；不能据此说它算不过实时，也不能拿它与前面的计算 RTF 比较。

## 资源和加载

- **Nemotron CPU**：完整转写测试进程工作集约 1.76–1.79 GB，流式短句约 0.98 GB。新进程启动到 `/ready` 约 2.24 秒。
- **Nemotron Vulkan，关闭批处理**：进程工作集约 0.16 GB；推理后 WDDM 快照显示独立显存约 0.966–0.973 GB，另有约 64–66 MB 共享 GPU 内存。启动到可用约 1.13–1.16 秒。
- **Moonshine 中文 Tiny**：进程工作集约 125–128 MB，模型加载约 0.18–0.22 秒。
- **Moonshine 英文 Tiny**：短句工作集约 142 MB，44 秒输入后约 151 MB；加载约 0.19 秒。
- **Moonshine 英日 Small**：工作集约 252–268 MB，加载约 0.42–0.49 秒。

工作集均为测试进程观测值，Moonshine 还包含 Python 驱动；显存是推理后快照，未测峰值。启动测试没有清空 Windows 文件缓存，不属于冷磁盘加载测试。没有 AMD/Intel GPU 可供实测，也没有测游戏帧时间。

## 实际发现的问题

### Nemotron Vulkan 默认配置启动崩溃

最初默认 Vulkan 配置曾成功运行一次，后续独立启动复现两次失败，退出码 `3221226505`（`0xC0000409`），上游日志包含：

```text
GGML_ASSERT(ne3 == ne13) failed
ggml-cpu.c:1270
```

**加入 `--asr.batching.enabled=false` 后，本轮 11 次独立进程测试全部成功。** 保留预热即可约 1.14 秒启动。`--no-warmup` 也曾规避此问题，但会把部分初始化移入首次请求。这里记录的是可复现的规避方式，尚未确定上游根因，也不代表所有 Vulkan 设备均已稳定。

应用原型宜固定 v0.1.0、默认关闭批处理、保留 CPU 回退并隔离服务器进程。版本和接口仍较新，应在升级运行时后重跑相同用例。[上游发布说明](https://github.com/NVIDIA/NeMo-Speech.cpp/releases/tag/v0.1.0)

### 连续字幕仍需断句与质量验证

将 5.592 秒中文短句重复五次，每次末尾增加一秒静音，构成 32.96 秒输入：

| 配置 | 返回结果 | 观察 |
| --- | --- | --- |
| Nemotron，开启默认 800 ms token 静默断句 | 11 个最终段，含末尾空段 | 出现句中截断，“下午”变成“我”等 |
| Nemotron，关闭自动断句 | 1 个最终段，持续输出增量 | 仍有“至”变“日”和漏字，最终文字需客户端提交后才确认 |
| Nemotron，将断句阈值改为 2,000 ms | 5 个最终段 | 段数恢复合理，仍有“开放”变“太放”等错误 |
| Moonshine 中文 Tiny | 返回五句文字 | 首句正确，后续反复出现“九点四日下午”等错误 |

2 秒阈值只是在这个例子上减少误切，会延迟最终确认，并非适合所有人的默认值。Nemotron 的 token 静默不等于音频真正静音；上游支持配合独立 VAD 的断句，但本轮没有下载该 VAD，也未验证 VAD 驱动路径。[断句配置文档](https://github.com/NVIDIA/NeMo-Speech.cpp/blob/v0.1.0/docs/asr/configuration.md#endpointing)

这些重复片段是状态与断句边界测试，不能替代自然长对话。下一步应在真实游戏语音上比较现有分段器、运行时 VAD 和客户端主动提交的效果。

### 短句质量和静音

- 中文首个样本中，Moonshine 完整转写首轮正确，后续复用相同模型时把“至”写成“四”；重新运行可再次观察到。流式单句曾正确，不能概括为所有模式都错，也尚不能认定是状态复用实现缺陷。
- 英文样本的 `chieftain`，Nemotron 正确，Moonshine Small 写成 `thief then`、Tiny 写成 `chief then`；Tiny 实时结果还把 `fifty` 写成 `shifty`。
- 日文样本 Moonshine 给出“弁当制”，Nemotron 给出“弁当性”；其他位置还存在数字写法和标点差异，不能简单逐字符视为识别错误。
- 两种模型各完成一次 **8 秒纯零值音频**的快速流式测试，均返回空文字。尚未覆盖音乐、游戏音效、呼吸、键盘和远端语音噪声。

## 接入判断

**Nemotron：后续对照后暂缓正式接入。** 本轮证实其 CPU/Vulkan 可以处理短句、同一权重覆盖中英日，WebSocket 可逐块收 PCM、输出增量并最终提交。但它没有在后续 SenseVoice 对照中显示整句速度或资源优势；原生流式和个别英文质量收益需结合实际需求判断。

**Moonshine：保留中文 Tiny 轻量候选。** 权重和内存小，后续单线程对照也证实 CPU 用量优势，但准确性仍需验证；日文、英文需各自选择模型。现有 Windows 原生 DLL 可供 C# P/Invoke，部署不需要 Python。官方 C ABI 将同一 transcriber 的调用串行化，因此共享权重时也要安排公平调度，并为 PTT/字幕分别建立 stream。[C API 文档](https://moonshine-voice.readthedocs.io/en/latest/api/c-api/)

本轮只验证了顺序请求中的模型常驻与每轮流状态创建/释放。PTT 与字幕并发、加载中取消、设备重连、异常退出恢复、30 分钟以上持续运行和游戏负载均未验证。新 ASR 不应默认加载第二套说话人模型；现有字幕专属常驻分段/说话人组件应按各自职责继续管理。

复现入口：[测试说明](../tools/asr-evaluation/README.md)、[测试驱动](../tools/asr-evaluation/evaluate.py)、[保留的结果摘要 JSON](ASR_NATIVE_TRIAL_2026-09.results.json)。完整逐块事件、服务器日志和下载文件在本机 `artifacts/asr-evaluation-20260914`。
