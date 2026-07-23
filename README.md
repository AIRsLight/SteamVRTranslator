# SteamVR Translator

[项目仓库](https://github.com/AIRsLight/SteamVRTranslator) · [版本发布](https://github.com/AIRsLight/SteamVRTranslator/releases)

## 操作说明

### 首次启动

1. 将发布包解压到具有写入权限的目录。
2. 启动 `SteamVRTranslator.exe`。
3. 首次启动服务时，按照安装向导下载 SenseVoice 运行时和模型。仅使用图像翻译时也可以暂不安装。
4. 在“模型提供商”中选择内置“模拟提供商”进行离线测试，或添加 OpenAI 兼容提供商并填写 Base URL、API Key 和模型。
5. 手动启动 SteamVR。本工具不会自动启动 SteamVR，SteamVR 暂时断开后会自动等待并重连。
6. 在管理器中点击“启动服务”，等待状态显示为已连接。
7. 打开 SteamVR 按键绑定，确认翻译键、左右框选扳机、抓握键、窗口点击扳机和右摇杆已经绑定。

### 图像捕获与翻译

1. 在未接触空间窗口时短按左摇杆，进入捕获模式。
2. 同时按住左右扳机，两只手柄的位置分别作为选择框的两个对角点。
3. 移动双手调整范围，松开任意扳机立即捕获。
4. 捕获成功后，截图窗口会出现在原选择区域。
5. 手接近或抓住截图后：
   - 短按左摇杆直接翻译。
   - 长按左摇杆录制自定义命令，松开后提交。
   - 抓住窗口时，可用另一只手的扳机点击翻译、排版翻译、录音或关闭按钮。
6. 按住抓握键可移动和旋转窗口，松开后固定在当前位置。
7. 触碰结果窗口时使用右摇杆上下滚动。
8. 触碰或抓住单个窗口并长按右摇杆可关闭，松开按键可取消。工具栏中的关闭按钮会立即关闭。
9. 未接触窗口时短按右摇杆，可全局隐藏或重新显示空间窗口。

### VR 控制面板

1. 在非捕获状态下长按左摇杆约 `650 ms`，打开或关闭 VR 控制面板。
2. 使用另一只手指向按钮，并按下扳机点击。
3. 控制面板可启动捕获、打开语音输入、字幕、手机镜像及对应设置。

### VRChat 语音输入

1. 在“VRChat 语音”页面下载 SenseVoice，选择麦克风和 CPU 或本机 Vulkan GPU。
2. 确认 OSC 地址，VRChat 默认使用 `127.0.0.1:9000`。
3. 开启 VRChat 语音输入并启动服务。
4. 在 VR 中按住已绑定的 PTT 键录音，松开后识别并发送到 VRChat Chatbox。
5. 桌面模式默认使用左 `Ctrl`，可在管理器中重新捕获并绑定其他按键。

### 实时字幕

1. 在管理器中启用实验性字幕功能。
2. 选择本机 SenseVoice 或 VibeVoice API，并完成所需运行时和模型安装。
3. 配置音频来源、目标语言、翻译提供商和显示方式。
4. 启动服务后，从 VR 控制面板打开字幕窗口。
5. 点击字幕窗口中的监听按钮，开始或停止捕获进程音频。

### Android 手机镜像

1. 在管理器中启用实验性手机镜像。
2. 下载镜像运行时，并通过 USB 或无线 ADB 连接 Android 设备。
3. 选择设备、分辨率、最大帧率、码率和窗口缩放。
4. 启动服务后，从 VR 控制面板打开手机镜像。
5. 使用 VR 光标点击、拖动或滚动手机画面；窗口底部按钮对应返回、主页和最近任务。

### 文件位置

程序配置、运行时、模型、截图和日志都保存在安装目录内：

```text
appsettings.json
captures/
logs/
models/
runtimes/
runtime-data/
vibevoice-service/data/
```

## 构建方式

构建环境：

- Windows x64
- .NET 8 SDK
- PowerShell

在仓库根目录执行：

```powershell
./build.ps1
```

默认生成依赖 .NET 8 Desktop Runtime 的版本：

```text
artifacts/win-x64/SteamVRTranslator.exe
artifacts/win-x64/vibevoice-service/SteamVRTranslator.VibeVoice.Server.exe
artifacts/SteamVRTranslator-win-x64.zip
```

生成自包含版本：

```powershell
./build.ps1 -SelfContained
```

执行全部自动化测试：

```powershell
dotnet test SteamVRTranslator.sln
```

仅验证 Release 构建：

```powershell
dotnet build SteamVRTranslator.sln -c Release
```

构建脚本会通过 `fetch-openvr.ps1` 获取并校验固定版本的 Valve OpenVR 原生运行时。原生 DLL、模型、运行时、构建产物和捕获图片不会提交到 Git。
