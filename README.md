# SteamVR Translator

[项目仓库](https://github.com/AIRsLight/SteamVRTranslator) · [版本发布](https://github.com/AIRsLight/SteamVRTranslator/releases)

独立运行的 SteamVR 图像框选与翻译原型，不引用 `VRChatVoiceInput` 的程序集、配置或模型运行时。

## 当前功能

- 与 VRChat Voice Input 一致的原生 WPF 侧栏控制窗口与本地日志。
- SteamVR 动作输入与空间框选。
- 左右手柄位置作为矩形两个对角点，在追踪空间内生成面向用户的二维选择平面。
- 按住双手扳机调整；松开任一扳机立即捕获，并在原位置生成与捕获范围一致的截图层。
- 选择阶段启用 SteamVR Overlay 高优先级动作集，尽可能阻止扳机继续传给场景应用。
- 头显相对透明叠加层、网格选择框和振动反馈；操作提示紧贴选择框外侧上边缘，详细状态同步到桌面管理器。
- 选择框、截图、流式结果和扩展窗口共享一个 D3D11 设备；每个持久对象由静态主体、工具/描边、关闭进度和光标透明层组成，统一管理变换、层级和显隐生命周期。光标移动只更新 `64x64` 子层的位置，关闭进度只刷新 `256x256` 子层，不再重绘图片或 HTML 主纹理。
- 空间纹理按实际平面比例使用 `512 / 1024 / 2048` 长边挡位：普通截图和 WPF 窗口最高 `1024`，HTML 排版结果原生使用 `2048`，不再填充为正方形透明画布。
- 通过 OpenVR `GetMirrorTextureD3D11` 直接读取 SteamVR 指定单眼的合成纹理，投影裁剪并保存质量 92 的 JPEG。
- 捕获可固定为左眼或右眼；框选时闭上另一只眼睛，可让选择框与截图使用同一个视点。
- 内置不可删除的模拟 Provider，以及 OpenAI 兼容的视觉 `chat/completions` Provider。
- 可保存多个具名 OpenAI 兼容 Provider，并在同一列表中显式选择启用项；每个兼容 Provider 配置 Base URL、API Key 和独立最大并发数，模型列表由标准 `GET /models` 返回，并可在可编辑下拉框中实时筛选。
- 自定义命令使用 WASAPI 麦克风录音和 SenseVoice 常驻 worker 转写，再由当前启用的 Provider 结合截图回答；模拟 Provider 会把转写原文重复十遍并流式显示。
- 可一次下载并校验 SenseVoice CPU/Vulkan 常驻运行时、Q5/Q8 模型和 FSMN VAD，支持 Hugging Face 官方站与 HF Mirror；引擎下拉框会列出本机可用的 NVIDIA、AMD 和 Intel Vulkan GPU。
- 内置独立 SteamVR VRChat 语音输入动作：按住 PTT 录音、松开识别，并通过 UTF-8 OSC `/chatbox/input` 发送至 VRChat。
- 触碰或抓住截图后，短按左摇杆翻译，长按则录制自定义语音命令。普通翻译结果沿用截图尺寸；自定义命令使用固定 `0.30 x 0.44 m` 的竖屏聊天窗口，用户问题与助手回答以消息气泡展示，后续追问继续追加到同一窗口。
- 抓握键让截图、结果和 WPF 窗口完整跟随手部平移及旋转；双手接触会分别描边，左右手可以各拿起一个不同窗口。左手持有时工具栏显示在窗口右侧，右手持有时显示在左侧，按钮顺序和命中区域同步镜像。双持期间工具栏、指针、滚动和其他单窗口操作暂时禁用。右摇杆短按全局显示/隐藏空间对象；持续触碰或持有单个窗口并长按右摇杆一秒可关闭，松开窗口或按键都会取消。
- 抓起任意空间对象时显示离屏 WPF 对象工具栏：截图提供 Markdown 翻译、按原图排版的 HTML 翻译和关闭按钮，录音按钮独立位于对应侧下角并在录音时变红；自定义命令结果可继续追问，并提供 OSC 发送和关闭按钮。超过 144 字符时自动节流分段发送，段间隔可在 VRChat 语音设置中热更新，避免连续 UDP 包只显示最后一段。
- 一只手触碰或抓住对象时，另一只手会在对象内显示高对比光标；左右扳机分别作为对应手的左键，按下后锁定目标直到松开。
- `IWpfSpatialOverlayHost` 可把任意同进程 WPF `Window` 注册为空间窗口，并把光标移动、左键和滚轮作为窗口消息传入；管理器内置“在 VR 中显示管理器”入口用于实机验证。
- 截图和结果对象不限制数量。已有结果仍在生成时也可开始下一次框选、截图和请求；每个 Provider 按自己的最大并发数独立排队，不同 Provider 互不占用并发槽。进入框选后会暂时隐藏全部已有对象，截图完成后恢复，避免叠加层进入下一次捕获。
- 服务运行中可热更新捕获眼睛、结果滚动方向、目标语言、当前启用 Provider 和下载镜像源；语音启用、麦克风、OSC 目标及 ASR 运行时相关设置仍需停止服务后修改。
- 管理器和 SteamVR 覆盖层支持中文、English 与日本語。首次启动按 Windows 显示语言选择，不支持的语言回退为英语；后续手动选择会写入配置。识别语言可设为自动、明确语种或跟随界面语言，跟随模式在下一次启动服务时应用到 SenseVoice 常驻 worker。
- 实验性字幕可在本机 SenseVoice 与独立 VibeVoice API 后端之间切换。必须先在桌面管理器手动启用，程序会校验所选 ASR 和可选说话人模型；未启用时 VR 控制面板不显示字幕入口。发布包附带 `vibevoice-service` 后台服务和 Windows 原生管理器，可分别下载 CrispASR 运行时与 VibeVoice Q4_K；主程序只通过 HTTP 调用，因此服务也可部署到另一台 Windows 或 Linux x64 机器。
- 实验性 Android 手机镜像通过 ADB 与官方 scrcpy server 传输 H.264，使用本地 FFmpeg 解码并映射 VR 光标为触摸输入。必须先在桌面管理器手动启用，程序会校验 scrcpy、ADB 和所选解码器；未启用时 VR 控制面板不显示手机入口。桌面管理器和 VR 控制面板都可选择 USB/无线 ADB 设备及 `720p / 900p / 1080p`、`30 / 60 / 90 / 120 FPS`、码率；运行时按需下载，不进入发布包。
- 普通翻译和自定义命令使用 WPF Markdown 结果窗口；排版翻译请求完整 HTML，并由离屏 WebView2 按截图分辨率渲染为静态 PNG。结果只保留 PNG 和可见文本，完成后立即销毁 DOM。解析或渲染失败时只将该结果回退为纯文本窗口。
- OpenAI 兼容接口默认请求 SSE 流式响应；Markdown 与自定义命令逐段刷新，HTML 排版翻译持续接收但只在完整文档返回后渲染。请求只在连续 90 秒没有任何新数据时超时，接口明确拒绝流式参数时自动回退到完整响应。
- 翻译异常显示在对应的空间结果层并写入本地日志，不关闭控制窗口。

## 构建

要求 Windows x64 和 .NET 8 SDK。

```powershell
./build.ps1
```

默认生成依赖已安装 .NET 8 Desktop Runtime 的版本：

```text
artifacts/win-x64/SteamVRTranslator.exe
artifacts/win-x64/vibevoice-service/SteamVRTranslator.VibeVoice.Server.exe
artifacts/SteamVRTranslator-win-x64.zip
```

需要自包含版本时：

```powershell
./build.ps1 -SelfContained
```

`fetch-openvr.ps1` 从固定 Valve OpenVR 提交下载原生 DLL，并校验文件长度和 SHA-256。原生 DLL、构建输出和截图均不会进入 Git。

## 使用

1. 启动 SteamVR 和 `SteamVRTranslator.exe`。
2. 选择左眼或右眼，并在框选时闭上另一只眼睛。
3. 首次测试在“模型提供商”中启用内置“模拟提供商”，它不会访问网络。使用云端时添加兼容端点，点击“测试并获取”，再设为启用。
4. 点击“启动”，再通过“SteamVR 按键绑定”确认翻译键、框选扳机和光标点击扳机绑定。
5. 在没有触碰空间对象时短按左摇杆进入选择模式；长按约 650 ms 打开或关闭固定尺寸的 VR 控制面板。
6. 同时按住左右扳机；两只手柄的位置就是矩形的两个对角点，移动双手调整空间平面。
7. 松开任一扳机立即捕获；完成后，原位置会出现与捕获区域同尺寸的截图层。
8. 手接近截图或抓住截图时，短按左摇杆直接翻译；按住左摇杆说话，松开后提交自定义命令。结果会以相同尺寸生成在当前视线前方；触碰该自定义命令结果后再次按住左摇杆，可在同一窗口继续追问。
9. 手接近任意截图、结果或 WPF 窗口时按住抓握键，可以让它完整跟随手部平移和旋转；抓起后用另一只手的扳机点击工具栏。左右手也可各拿起一个不同窗口，但双持期间所有单窗口操作无效，松开其中一个后恢复。截图的翻译按钮生成 Markdown，版面按钮生成 HTML 排版结果，麦克风按钮按下录音、松开提交自定义命令；关闭按钮只移除当前对象，结果另有 OSC 发送按钮。松开抓握键后工具栏隐藏。
10. 未接触窗口时短按右摇杆可全局隐藏或重新显示空间对象；持续接触或持有单个窗口并长按一秒会显示删除进度环，松开窗口或摇杆都会取消。触碰或持有结果层时，右摇杆上下平滑滚动长文本；方向不符合习惯时，在控制窗口勾选“反转右摇杆滚动方向”。
11. 测试 WPF 交互时点击“在 VR 中显示管理器”，用一只手触碰或抓住窗口，另一只手指向控件；扳机点击，右摇杆滚动。
12. 使用手机镜像时先在管理器下载镜像运行时并通过 ADB 连接设备。之后可直接在 VR 控制面板的手机图标页切换设备和质量，连接后手机窗口支持光标点击、拖动和滚动。

可以先启动本工具的服务，再由用户自行启动 SteamVR。服务只探测已运行的 `vrserver` 和 `vrcompositor`，并以不会启动 SteamVR 的 OpenVR Background 客户端被动连接；SteamVR 退出后，服务会等待其进程完全结束，再监听下一次由用户发起的启动。工具不会自动拉起 SteamVR，也不会为了重连阻止 SteamVR 关闭。

需要 VRChat 语音输入时，在“VRChat 语音”页下载 SenseVoice、选择麦克风和 CPU/本机 Vulkan GPU 后开启功能。CPU 保持默认；GPU 速度依设备而异，也可选择空闲的集成显卡降低 CPU 压力。Touch 默认按住左手 X，Index 默认按住右手 A，其他控制器默认使用右手菜单键；所有绑定都可以在 SteamVR 按键绑定页面修改。VRChat 默认 OSC 地址为 `127.0.0.1:9000`；结果工具栏的发送按钮也使用这里配置的主机、端口和立即发送选项，即使未启用语音 PTT 也可使用。长文本按 144 个 Unicode 字符切分，首段立即发送，后续段落使用可热更新的分段间隔，默认 1100 毫秒。语音翻译选择“先显示原文”时，“立即发送”开启会自动提交原文，译文完成后只更新 Chatbox 输入框；关闭“立即发送”时，原文和译文都只更新输入框。

默认操作键：Touch、Index 和 WMR 使用左手摇杆按下进入选择或翻译当前截图；Vive 手柄使用左触控板。左右抓握键移动空间对象，另一只手的扳机点击空间窗口，右手摇杆或触控板负责滚动和全局显隐。绑定可在 SteamVR 中修改。

模拟 Provider 仍会执行真实 SteamVR 单眼纹理捕获和裁剪，将输出 JPEG 保存到安装目录的 `captures` 子目录；普通翻译返回可流式滚动的 Markdown，版面翻译返回完整 HTML。这可以在不配置云端接口的情况下验证空间框选、两类结果窗口、接触抓取、流式结果和滚动链路。

长按阈值默认是 350 ms。程序在手接触截图并按下左摇杆时立即暂存音频，因此不会丢失语音开头；达到阈值时手柄会振动，松开后调用 SenseVoice。短按产生的临时录音会被删除，普通翻译在 SenseVoice 未安装时仍可使用。截图在松开任一框选扳机后立即捕获，翻译阶段复用该截图，不会重复截取画面。

SenseVoice 使用与 `VRChatVoiceInput` 相同的常驻 worker 协议，但项目之间没有程序集依赖。默认文件布局为：

```text
runtimes/llama-funasr-sensevoice.exe
runtimes/sensevoice-vulkan/llama-funasr-sensevoice.exe
models/sensevoice-small-q8.gguf
models/fsmn-vad.gguf
```

这些二进制和模型不会进入源码仓库或默认构建包，管理器下载的文件统一保存在安装目录的 `runtimes` 与 `models` 子目录。麦克风可以选择 Windows 默认通讯设备或指定 VR 串流软件提供的远程麦克风。

无头显环境可运行 Android 与 SteamVR Null Driver 的联合压力测试。测试会依次使用 720p 与 1080p 手机流，持续执行滑动操作，并输出解码帧率、WPF 纹理上传、SteamVR 主循环、输入队列、CPU、内存和 NVIDIA GPU 指标：

```powershell
./tools/Test-AndroidMirrorSteamVr.ps1
```

SteamVR Compositor 仍要求当前 Windows 会话具有活动 DXGI 显示输出；断开的 RDP 会话会在测试前被明确拒绝，避免生成无效性能数据。

字幕页选择 `VibeVoice API` 后，可连接发布目录中的独立服务或另一台机器上的服务。本机优先打开原生管理器，远程和兼容场景仍可使用浏览器管理页；两者都能下载 CPU、Vulkan 或 CUDA 版 CrispASR 运行时和 VibeVoice Q4_K 权重，并在转写请求到来时保持模型常驻。远程监听必须配置 API Key；完整部署方式和接口协议见 [VibeVoice 独立服务](docs/VIBEVOICE_SERVICE.md)。

程序会在运行期间持续持有所选眼睛的 SteamVR 镜像纹理。首次创建纹理时等待至少两个合成帧，并在检测到纯黑初始化帧时自动重试。裁切使用同一 `Compositor_FrameTiming` 中的 HMD 姿态，避免用新的头部姿态投影较旧的镜像画面。

## 翻译接口

OpenAI 兼容 Provider 先通过 `{Base URL}/models` 获取模型列表，翻译时只把框选后的 JPEG 发送到 `{Base URL}/chat/completions`。API Key 作为 Bearer Token 使用；本地无鉴权兼容服务可以留空。每个 Provider 独立保存 Base URL、API Key、模型和最大并发请求数，配置文件中只记录当前启用 Provider，不再另存一套后端模式。普通翻译和自定义命令要求返回 Markdown 并默认启用 `stream`；兼容 SSE 的服务会逐段显示结果，不支持时保留非流式回退。自定义命令结果可在同一窗口继续语音追问，请求会带上该窗口的历史问答和一份原截图。排版翻译要求返回无脚本、无外部资源的完整单文件 HTML，也允许通过 SSE 持续接收，但不会显示或渲染不完整的 HTML。只要流仍有字节到达就会刷新空闲计时，连续 90 秒无数据才会超时。HTML 会移除危险节点和事件属性、阻断网络资源，再由 WebView2 生成 PNG 并释放浏览器文档；WebView2 不可用或渲染异常时显示提取后的纯文本。OSC 发送始终使用 Markdown/HTML 解析后的可见文字，不发送排版控制符。直接翻译、排版翻译、自定义命令、语音翻译和字幕翻译使用各自的系统提示词、任务提示词与生成参数；兼容的 Qwen 端点会按该类型的设置发送 `enable_thinking`。

管理器的“提示词”页面分别提供直接翻译、排版翻译、自定义命令、语音翻译和字幕翻译五组编辑器，每组包含 system 消息与任务说明。中文、英语和日语各自保存独立提示词，切换界面语言只切换当前编辑集合，不覆盖其他语言。每个标签页下方都有默认收起的高级生成设置，可独立配置思考开关、Temperature、Top P 与最大输出 Token；最大输出填 `0` 时沿用提供商默认值。修改会在 600 ms 停顿后自动保存，并热应用到下一次请求；不重启 SteamVR 服务。目标语言与语音转写命令始终由程序追加，因此自定义任务说明不需要维护占位符。每个标签页都可以独立恢复当前语言的内置默认值。内置排版提示词只重建海报、封面、书页、标签、报纸、菜单、告示、控制面板或屏幕界面等主要文字主体，并忽略人物、房间、墙面、家具、风景与光影背景。

API Key 会保存在安装目录内的本地配置文件中，不写入日志或源码仓库。便携目录包含配置、运行时、模型、截图、日志和缓存，移动整个目录即可保留运行状态。

配置保存到安装目录：

```text
.\appsettings.json
```

截图保存到安装目录的独立子目录：

```text
.\captures\capture-YYYYMMDD-HHMMSS-fff.jpg
```

日志保存到程序目录：

```text
./logs/steamvr-translator-YYYYMMDD.log
```

WebView2 用户数据、SteamVR 清单和临时音频统一位于 `./runtime-data`；手机镜像运行时位于 `./runtimes`；内置 VibeVoice 服务数据位于 `./vibevoice-service/data`。首次启动新版时会把旧 `%LOCALAPPDATA%\SteamVRTranslator` 中未冲突的数据迁移进安装目录，已有目标文件不会被覆盖。

实机诊断时按同一个 `op` 筛选即可串起一次完整操作；提交后的日志还会带 `request`：

```text
[op=0001 request=0001] [input|selection|audio|asr|capture|backend|stream|overlay|submit]
```

关键记录包括按键持续时间、空间平面位置与角度、麦克风格式和录音字节数、SenseVoice worker PID/耗时/stderr 摘要、SteamVR 合成器帧号、单眼覆盖率、截图文件、流式分块数量、队列延迟和结果层渲染次数。日志只保留识别及返回文本的短预览，避免长流式结果反复写入文件。

## 当前边界

- SteamVR 镜像纹理目前支持常见的 RGBA8/BGRA8 UNorm、sRGB 与 Typeless 格式；若启用 HDR 后返回其他像素格式，程序会记录明确错误而不会回退到桌面截图。
- 镜像纹理仍由 SteamVR 合成器提供，最终时序取决于具体头显驱动；日志会记录纹理生命周期、合成帧号及其对应姿态，便于继续校准。
- SteamVR 对场景应用的输入覆盖仍属于实验能力，需要在 SteamVR 开发者设置中启用 Overlay Input Overrides；未启用时选择可用，但游戏也可能收到扳机输入。
- 相框角度以 HMD 当前视线为基准；框内水平轴也跟随 HMD 本地右轴，不再依赖房间世界轴，因此平视和低头时的手部前后移动保持一致。平面法线与视线夹角超过 35° 时边框变红且不会锁定；双手几乎重合，或宽度、高度任一方向小于 10 cm 时同样不会锁定。
- WPF 空间窗口通过 `RenderTargetBitmap` 镜像 WPF 视觉树，并通过窗口消息驱动标准 WPF 控件。包含 WebView2、视频或其他独立原生子 HWND 的 `HwndHost` 内容不保证能被 WPF 离屏渲染；这类扩展需要实现专用纹理源。

## 工程结构

```text
src/SteamVRTranslator.Core     状态机、空间几何和可测试投影数学
src/SteamVRTranslator.OpenVR   固定版本的 Valve OpenVR C# 绑定
src/SteamVRTranslator.App      WPF 控制窗口、SteamVR 运行时、捕获和翻译
src/SteamVRTranslator.VibeVoice.Server  可独立部署的 VibeVoice HTTP 服务与下载器
tests/                         无 HMD 可运行的状态机与投影测试
```
