# SteamVR Translator

独立运行的 SteamVR 图像框选与翻译原型，不引用 `VRChatVoiceInput` 的程序集、配置或模型运行时。

## 当前功能

- 与 VRChat Voice Input 一致的原生 WPF 侧栏控制窗口与本地日志。
- 桌面全局热键和 SteamVR 动作输入。
- 左右手柄位置作为矩形两个对角点，在追踪空间内生成面向用户的二维选择平面。
- 按住双手扳机调整；松开任一扳机立即捕获，并在原位置生成与捕获范围一致的截图层。
- 选择阶段启用 SteamVR Overlay 高优先级动作集，尽可能阻止扳机继续传给场景应用。
- 头显相对透明叠加层、网格选择框和振动反馈；操作提示紧贴选择框外侧上边缘，详细状态同步到桌面管理器。
- 选择框、截图和流式结果共享一个 D3D11 设备，每个空间对象独立持有 Overlay 纹理，不再连续调用 `SetOverlayRaw`。
- 通过 OpenVR `GetMirrorTextureD3D11` 直接读取 SteamVR 指定单眼的合成纹理，投影裁剪并保存质量 92 的 JPEG。
- 捕获可固定为左眼或右眼；框选时闭上另一只眼睛，可让选择框与截图使用同一个视点。
- 内置不可删除的模拟 Provider，以及 OpenAI 兼容的视觉 `chat/completions` Provider。
- 可保存多个 OpenAI 兼容 Provider，并在同一列表中显式选择启用项；每个兼容 Provider 只配置 Base URL 和 API Key，模型列表由标准 `GET /models` 返回。
- 自定义命令使用 WASAPI 麦克风录音和 SenseVoice 常驻 worker 转写；当前 Mock 命令后端会把转写原文重复十遍并流式显示。
- 可直接下载并校验 SenseVoice CPU 常驻运行时、Q5/Q8 模型和 FSMN VAD，支持 Hugging Face 官方站与 HF Mirror。
- 内置独立 SteamVR VRChat 语音输入动作：按住 PTT 录音、松开识别，并通过 UTF-8 OSC `/chatbox/input` 发送至 VRChat。
- 触碰或抓住截图后，短按左摇杆翻译，长按则录制自定义语音命令；结果以相同尺寸生成在视线前方。
- 抓握键让截图和结果完整跟随手部平移及旋转；右摇杆按下全局显示/隐藏空间对象与手柄，上下滚动当前触碰的结果。
- 截图和结果对象不限制数量。进入框选后会暂时隐藏全部已有对象，截图完成后恢复，避免叠加层进入下一次捕获。
- OpenAI 兼容接口默认请求 SSE 流式响应，支持时逐段刷新结果；接口明确拒绝流式参数时自动回退到完整响应。
- 翻译异常显示在对应的空间结果层并写入本地日志，不关闭控制窗口。

## 构建

要求 Windows x64 和 .NET 8 SDK。

```powershell
./build.ps1
```

默认生成依赖已安装 .NET 8 Desktop Runtime 的版本：

```text
artifacts/win-x64/SteamVRTranslator.exe
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
4. 点击“启动”，再通过“SteamVR 按键绑定”确认翻译键和左右扳机绑定。
5. 在没有触碰空间对象时按左摇杆进入选择模式。
6. 同时按住左右扳机；两只手柄的位置就是矩形的两个对角点，移动双手调整空间平面。
7. 松开任一扳机立即捕获；完成后，原位置会出现与捕获区域同尺寸的截图层。
8. 手接近截图或抓住截图时，短按左摇杆直接翻译；按住左摇杆说话，松开后提交自定义命令。结果会以相同尺寸生成在当前视线前方。
9. 手接近任意截图或结果层时按住抓握键，可以让它完整跟随手部平移和旋转；松开后固定在当前位置。
10. 按下右摇杆全局隐藏或重新显示空间对象和定位手柄；新的截图会自动重新显示。触碰或持有结果层时，右摇杆上下平滑滚动长文本；方向不符合习惯时，在控制窗口勾选“反转右摇杆滚动方向”。

需要 VRChat 语音输入时，在“VRChat 语音”页下载 SenseVoice、选择麦克风并开启功能。Touch 默认按住左手 X，Index 默认按住右手 A，其他控制器默认使用右手菜单键；所有绑定都可以在 SteamVR 按键绑定页面修改。VRChat 默认 OSC 地址为 `127.0.0.1:9000`。

默认操作键：Touch、Index 和 WMR 使用左手摇杆按下进入选择或翻译当前截图；Vive 手柄使用左触控板。左右抓握键移动空间对象，右手摇杆或触控板负责滚动和全局显隐。绑定可在 SteamVR 中修改。桌面默认热键为 F8。

模拟 Provider 仍会执行真实 SteamVR 单眼纹理捕获和裁剪，将输出 JPEG 直接保存到 `SteamVRTranslator.exe` 所在目录；触碰截图并按左摇杆后，用约三秒逐段显示足以触发滚动的多语言长文本。这可以在不配置云端接口的情况下完整验证空间框选、截图层、接触抓取、流式结果和滚动链路。

长按阈值默认是 350 ms。程序在手接触截图并按下左摇杆时立即暂存音频，因此不会丢失语音开头；达到阈值时手柄会振动，松开后调用 SenseVoice。短按产生的临时录音会被删除，普通翻译在 SenseVoice 未安装时仍可使用。截图在松开任一框选扳机后立即捕获，翻译阶段复用该截图，不会重复截取画面。

SenseVoice 使用与 `VRChatVoiceInput` 相同的常驻 worker 协议，但项目之间没有程序集依赖。默认文件布局为：

```text
runtimes/llama-funasr-sensevoice.exe
models/sensevoice-small-q8.gguf
models/fsmn-vad.gguf
```

这些二进制和模型不会进入源码仓库或默认构建包，也可以在控制窗口中填写绝对路径。麦克风可以选择 Windows 默认通讯设备或指定 VR 串流软件提供的远程麦克风。

程序会在运行期间持续持有所选眼睛的 SteamVR 镜像纹理。首次创建纹理时等待至少两个合成帧，并在检测到纯黑初始化帧时自动重试。裁切使用同一 `Compositor_FrameTiming` 中的 HMD 姿态，避免用新的头部姿态投影较旧的镜像画面。

## 翻译接口

OpenAI 兼容 Provider 先通过 `{Base URL}/models` 获取模型列表，翻译时只把框选后的 JPEG 发送到 `{Base URL}/chat/completions`。API Key 作为 Bearer Token 使用；本地无鉴权兼容服务可以留空。每个 Provider 独立保存 Base URL、API Key 和从接口选择的模型，配置文件中只记录当前启用 Provider，不再另存一套后端模式。默认启用 `stream`；兼容 SSE 的服务会逐段显示译文，不支持时保留非流式回退。Just Translate 请求使用固定系统提示词；Qwen 模型及 DashScope/阿里云兼容端点会额外发送 `enable_thinking: false`，降低直接翻译延迟。

API Key 会保存在当前 Windows 用户的本地配置文件中，不写入日志或源码仓库。配置文件位于 `%LOCALAPPDATA%\SteamVRTranslator\appsettings.json`。

配置保存到：

```text
%LOCALAPPDATA%\SteamVRTranslator\appsettings.json
```

日志保存到程序目录：

```text
./logs/steamvr-translator-YYYYMMDD.log
```

实机诊断时按同一个 `op` 筛选即可串起一次完整操作；提交后的日志还会带 `request`：

```text
[op=0001 request=0001] [input|selection|audio|asr|capture|backend|stream|overlay|submit]
```

关键记录包括按键持续时间、空间平面位置与角度、麦克风格式和录音字节数、SenseVoice worker PID/耗时/stderr 摘要、SteamVR 合成器帧号、单眼覆盖率、截图文件、流式分块数量、队列延迟和结果层渲染次数。日志只保留识别及返回文本的短预览，避免长流式结果反复写入文件。

## 当前边界

- SteamVR 镜像纹理目前支持常见的 RGBA8/BGRA8 UNorm、sRGB 与 Typeless 格式；若启用 HDR 后返回其他像素格式，程序会记录明确错误而不会回退到桌面截图。
- 镜像纹理仍由 SteamVR 合成器提供，最终时序取决于具体头显驱动；日志会记录纹理生命周期、合成帧号及其对应姿态，便于继续校准。
- SteamVR 对场景应用的输入覆盖仍属于实验能力，需要在 SteamVR 开发者设置中启用 Overlay Input Overrides；未启用时选择可用，但游戏也可能收到扳机输入。
- 相框角度以 HMD 当前视线为基准；框内水平轴也跟随 HMD 本地右轴，不再依赖房间世界轴，因此平视和低头时的手部前后移动保持一致。平面法线与视线夹角超过 35° 时边框变红且不会锁定；双手几乎重合或宽/高小于 4 cm 时同样不会锁定。

## 工程结构

```text
src/SteamVRTranslator.Core     状态机、空间几何和可测试投影数学
src/SteamVRTranslator.OpenVR   固定版本的 Valve OpenVR C# 绑定
src/SteamVRTranslator.App      WPF 控制窗口、SteamVR 运行时、捕获和翻译
tests/                         无 HMD 可运行的状态机与投影测试
```
