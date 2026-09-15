# 字幕生命周期与诊断

## 启动和停止

字幕监听捕获当前 SteamVR 场景进程及其子进程的播放音频。它不使用语音输入的麦克风，也不需要 VB-CABLE。

1. 选择本机 SenseVoice，安装所选 CPU / Vulkan 运行时和 ASR 模型；启用说话人区分时还需要对应模型。
2. 启动服务和目标 VR 应用，打开字幕窗口，点击开始监听。
3. “准备识别引擎与音频”期间，后台获取共享 SenseVoice；语音输入已经预热同配置模型时直接复用。启用了本机说话人区分时，还会准备字幕专用的分段和声纹模型。加载未完成时可以停止，其他使用者仍能继续等待共享模型加载。
4. 没有当前场景进程时显示等待；连接音频成功后才显示正在监听。
5. 停止监听会取消字幕自己的待处理音频、识别结果和翻译，并等待捕获及客户端资源释放。停止服务、禁用字幕和退出程序也会执行清理。

停止过程不能重复点击；清理完成后可以再次开始。模型加载或识别进程失败时会结束本次字幕会话并显示错误。坏进程会被移出共享模型，下一次识别请求重新加载，已有调用客户端不必重新创建。音频捕获意外退出时会自动重新连接当前场景进程。

显示设置可直接更新。识别引擎、识别语言和翻译设置使用配置快照，在当前音频段完成后应用到下一段；不会在正在识别时释放工作进程。VR 控制面板修改设置也不再反复停止和启动捕获。文件重放、实时监听和语音输入分别持有共享模型的使用权，取消其中一个不会销毁另一个正在使用的模型。

## SenseVoice 复用与取消

- 模型、VAD、选中的运行时路径、识别语言、CPU / Vulkan 后端和 Vulkan 设备序号均相同时，只加载一个常驻进程。显示样式、麦克风设置或 GPU 名称变化不会产生额外模型。
- 同配置同时启动时，共用一次加载。停止并重启字幕时，只要语音输入等其他使用者仍保留模型，就直接复用；所有使用者退出后才结束进程。
- 单个模型一次处理一条音频。下一条请求优先选择语音输入或语音命令，再处理等待中的字幕和文件重放，不打断已经开始的识别。
- 当前 worker 协议没有单请求取消命令。取消正在识别的字幕时，界面立即放弃结果，后台继续读取该次响应，再处理下一条请求，避免结果错配或重新加载模型。后台保留独立临时音频副本，读取结束后删除；队列中尚未开始的取消任务直接移除。
- 单次识别超过两分钟会结束异常进程并报错，下一条请求重建模型。最后一个使用者退出时，也会取消仍在执行的工作并等待进程结束。

## 字幕专用的分段和声纹模型

Pyannote 分段和 ERes2Net 声纹模型独立于 SenseVoice，由字幕控制器管理。语音输入和语音命令不创建这些实例；使用 VibeVoice API 或未启用说话人区分时，也不会加载本地说话人模型。

- 在首次开始本机字幕监听或文件重放时加载，连续音频段复用同一组模型。暂停后重新开始监听仍可复用。
- 实时字幕和文件重放可以共用同配置模型，原生调用串行执行；说话人编号和声纹记录分别属于各自的会话，互不混用。
- 修改显示样式、SenseVoice 设置或聚类阈值不会重载说话人模型。聚类阈值在处理下一段时更新；修改模型路径或推理线程数会安全切换实例。已有文件重放仍使用原配置时，其实例保留到重放结束。
- 关闭说话人区分在下一音频段生效并释放该会话的模型引用；关闭字幕功能、停止服务或退出程序会等待会话结束后释放常驻模型。
- 当前 sherpa-onnx 原生推理是同步调用。停止期间界面继续响应，但会等当前推理结束再释放，避免模型仍在使用时被卸载；取消后的结果不会提交到字幕历史。

## 没有字幕时

- **一直等待进程**：确认服务已启动，目标应用正在作为 SteamVR 场景运行。
- **启动后报错**：查看窗口错误和安装目录 `logs` 中最新日志的 `[subtitles]`、`[asr]` 记录，确认运行时、模型和所选 CPU / Vulkan 引擎可用。
- **正在监听但没有文字**：确认发声的是当前场景进程或其子进程。普通桌面播放器或独立 Overlay 的音频不属于这个捕获范围。使用文件重放可以单独检查识别链路。
- **背景音或静音**：未识别到语音是正常结果，监听会继续。目标应用停止提交音频包约 650 ms 后，也会自动结束当前有效音频段，不需要点击停止；持续输出静音包时仍按音频分段规则处理。

Windows 进程音频捕获需要 build 20348 或更新版本。日志会记录捕获 PID、音频格式、连接失败及重试、识别启动和识别异常。

## 详细流程日志

详细日志默认启用，写入程序目录的 `logs/steamvr-translator-yyyyMMdd.log`，不需要开启调试选项。实时监听和文件重放均有独立的 `[session=live-…]` / `[session=replay-…]`；重连音频会增加 `[capture=…]`，开始新会话才更换 session。实时音频段使用会话内递增的 `[segment=000001]`，说话人分段增加 `[part=1/2]`，字幕历史记录使用 `[entry=…]`。

日志中的 `stage` 表示处理阶段，`status` 表示开始、完成、取消、跳过或失败；`elapsedMs` 是阶段耗时，`waitMs` 是队列等待耗时。新增流程元数据记录字符数，不主动输出识别原文、译文、API 密钥或完整配置；原有异常记录仍保留错误详情和调用栈。

| 阶段 | 可诊断的信息 |
| --- | --- |
| `state`、`asr-acquire`、`diarization-acquire` | 等待场景进程、准备共享 ASR、获取字幕常驻说话人模型及取消/失败 |
| `capture-connect`、`audio-activate`、`audio-initialize` | 当前捕获 PID、每次重连、Windows 音频接口激活和格式初始化 |
| `audio` | 首包、累计包数/帧数、Windows 静音标志、超过成段音量阈值的包数、音频不连续、RMS/峰值及距上次收包的时间 |
| `segment` | 成段起止时间、字节数、触发原因；不足 280 ms 的短段会记录丢弃原因 |
| `queue` | 入队、出队、等待时间、剩余排队数量；队列容量 4，满时逐条记录被丢弃的最旧音频段 |
| `audio-file`、`audio-read`、`audio-analyze` | 临时 WAV 写入、读取和转换为 16 kHz 单声道、分析后的分段数与时长 |
| `diarization-segment`、`speaker-embedding` | 本地分段、声纹推理和共享模型等待；未启用时明确记录跳过 |
| `asr`、`model-queue` | ASR 请求、共享 SenseVoice 队列优先级和等待、字符数、空结果、无语音、失败和取消 |
| `translation-slot`、`translation-request`、`translation` | 翻译提供商/模型、并发槽等待、请求耗时、字符数、空响应、取消和失败；关闭翻译时明确记录跳过 |
| `history-add`、`history-state`、`history-translation` | Dispatcher 上的字幕历史添加/译文更新、记录 ID、显示开关、历史数量与裁剪数量 |
| `capture-dispose`、`cleanup`、`session` | 取消请求、资源释放、总耗时及各音频段去向汇总 |

采集日志在首包到达时记录一次，此后每 5 秒输出一次状态，停止时输出最终统计。即使完全没有收到音频包也会每 5 秒记录。`packets`、`silentPackets`、`audiblePackets` 为累计数；`rms`、`peak` 是本次记录窗口的电平（0–1），`audiblePackets` 仅表示超过成段阈值，不代表模型判定有人声。RMS 阈值为 0.0025。

实时成段原因：`ending-silence` 是累计静音达到约 650 ms；`packet-gap` 是目标停止提交音频包约 650 ms；`maximum-duration` 是达到 12 秒上限；`capture-stop` / `capture-failed` 是关闭或异常时排出尾段。停止后排出的尾段会标记为 `abandoned`，不会继续识别。

正常链路示例（省略时间、会话和捕获前缀）：

```text
[segment=000001] stage=segment status=ready reason=ending-silence ...
[segment=000001] stage=queue status=enqueue
[segment=000001] stage=queue status=dequeue waitMs=2 pending=0
[segment=000001] stage=audio-analyze status=complete ... parts=1 ...
[segment=000001] [part=1/1] stage=asr status=complete ... chars=18 empty=False
[segment=000001] [part=1/1] stage=history-add status=complete ... entry=...
[segment=000001] [part=1/1] [entry=...] stage=translation status=complete ... chars=12
[segment=000001] stage=segment-process status=complete ...
```

快速判断：

- `packets=0` / `lastPacketAgoMs=none`：连接后没有收到目标进程音频。
- 收到包但 `audiblePackets` 不增长：声音为静音或低于成段阈值；结合当前窗口 RMS/峰值判断。
- 有段但迟迟未出队，或出现 `status=dropped reason=capacity`：处理跟不上音频速度。
- 某阶段只有 `status=start`：执行或等待停留在该阶段；结合后续取消、失败和清理记录判断。
- `asr status=complete` 且 `chars=0`，或 `status=no-speech`：模型未生成有效字幕。
- `history-add status=complete`：历史数据已在 Dispatcher 上提交，**不等于已确认 VR 纹理或头显画面显示成功**。继续检查显示开关、历史裁剪和 Overlay 状态。
- 共享 SenseVoice 的 `location=worker-selected` 取消后，底层可能继续读取原请求响应；`delivered=False` 表示旧响应已丢弃，没有提交给已取消的字幕请求。

停止会话的汇总满足 `received = processed + dropped + abandoned + canceled + failed`。`processed` 表示该音频段处理结束，其中也可能包含无语音、零分段或翻译失败；具体结果需查看该段的阶段日志。

排查时保留从点击“开始监听”到点击“停止监听”的完整日志，可按 `[subtitles]` 筛选，再用 session 和 segment 追踪。PowerShell 示例：

```powershell
Get-Content -LiteralPath '.\logs\steamvr-translator-20260914.log' | Select-String -SimpleMatch '[subtitles]'
```

## 回归验证

`SubtitleLifecycleTests` 使用可控音频源和识别实例复现：连接期间停止再启动、捕获断开重连、模型启动失败与取消、识别途中修改设置、识别失败、无语音、外部取消及文件重放隔离。

详细日志回归额外验证：完整链路的编号关联、重连/重启/重放的编号隔离、队列溢出和停止时每个音频段的去向、翻译失败/空响应/取消、等待进程日志去重，以及正常流程不输出原文或密钥。`SubtitleAudioDiagnosticsTests` 验证无包与静音的区别、五秒限频、RMS/峰值窗口、音频异常标志和成段触发原因。

`SubtitleWindowLifecycleTests` 在真实 WPF Dispatcher 上验证关闭窗口不会同步阻塞字幕清理，并检查错误与停止状态的按钮行为。`SenseVoiceRuntimeTests` 验证尚未 READY 的常驻进程能够取消并退出。

`SenseVoiceModelPoolTests` 验证加载合并、配置区分、语音输入优先、等待与执行中的取消、临时音频寿命、最后一个使用者释放、异常和超时恢复，以及语音输入预热后字幕反复启停不重载。该测试还使用真实子进程和标准输入输出管道，验证取消后的旧响应不会成为下一位使用者的识别结果。

`SubtitleDiarizationLifecycleTests` 验证连续复用、暂停后重启、阈值更新、模型切换、实时与重放隔离、取消与卸载顺序、失败恢复，以及本地说话人区分未启用时不加载模型。实机测试通过官方模型下载器获取并校验资产，可重复使用指定缓存目录：

```powershell
dotnet test tests/SteamVRTranslator.App.Tests/SteamVRTranslator.App.Tests.csproj --filter FullyQualifiedName~RealModelsProcessRepeatedSpeechWithoutReloadingWhenExplicitlyEnabled --environment SVT_RUN_DIARIZATION_TEST=1 --environment "SVT_DIARIZATION_TEST_ROOT=D:\model-test-cache" --environment "SVT_DIARIZATION_TEST_AUDIO=D:\test-speech.wav"
```

已使用真实 Pyannote 3.0 INT8 和 ERes2Net 模型连续处理两次 15 秒语音，在切换聚类阈值的情况下只初始化一次，分段和声纹提取均通过验证。此结果验证模型复用，不代表复杂游戏混音中的说话人区分准确率。

真实 Windows 进程音频捕获测试需主动开启，会播放短测试音；它连续两次启动捕获，并要求停止播放后、停止监听前收到有效音频段：

```powershell
dotnet test tests/SteamVRTranslator.App.Tests/SteamVRTranslator.App.Tests.csproj --environment SVT_RUN_AUDIO_CAPTURE_TEST=1 --filter FullyQualifiedName~ProcessLoopbackAudioCaptureTests
```

这些测试不代替实际 SenseVoice 模型与目标游戏的完整识别测试。
