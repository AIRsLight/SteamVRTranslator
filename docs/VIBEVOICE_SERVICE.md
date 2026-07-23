# VibeVoice 独立服务

## 目标

`SteamVRTranslator.VibeVoice.Server` 把 VibeVoice-ASR 从 SteamVR Translator 主进程中拆出。主程序只上传音频并接收带时间戳和匿名说话人标签的 JSON，因此可以：

- 在本机独立进程中运行，避免模型加载或崩溃影响 WPF/SteamVR 进程；
- 把 CPU、Vulkan 或 CUDA 推理迁移到另一台机器；
- 让多个客户端复用同一个常驻模型；
- 独立安装和更新 CrispASR 运行时及 VibeVoice Q4_K 权重。

发布包包含跨平台后台服务和 Windows 原生管理器，不包含 CrispASR 二进制和模型权重。本机使用时，首次安装会写入服务程序所在目录：

```text
vibevoice-service\data\
  models\vibevoice-asr-q4_k.gguf
  runtimes\cpu|vulkan|cuda\...
  downloads\...
  logs\vibevoice-service-YYYYMMDD.log
  runtime-settings.json
```

Windows 与 Linux 未指定 `dataDirectory` 时都使用服务程序旁的 `data` 目录；独立服务器部署仍可显式配置绝对路径。

## 本机使用

1. 构建或解压发布包。
2. 在主程序“字幕”页选择 `VibeVoice API`。
3. 保持地址为 `http://127.0.0.1:5090`，点击“服务管理器”，主程序会打开原生管理窗口并按需启动后台服务。
4. 选择 CPU、Vulkan 或 CUDA；运行时与约 4.5 GB 的 Q4_K 模型可以分别下载，也可以点击“全部安装”。
5. 返回主程序测试连接，再执行音频文件回放。

原生管理器位于：

```text
vibevoice-service\SteamVRTranslator.VibeVoice.Manager.exe
```

若本机服务尚未运行，管理器会在后台启动同目录中的：

```text
vibevoice-service\SteamVRTranslator.VibeVoice.Server.exe
```

主程序退出不会强制终止该独立服务。运行时和权重与服务程序一起位于便携目录；更新时应覆盖程序文件而不是删除整个 `vibevoice-service/data` 目录。

## 远程部署

远程监听必须配置 API Key。可以编辑服务目录的 `appsettings.json`，也可以使用环境变量：

```powershell
$env:VIBEVOICE_LISTEN_URL = "http://0.0.0.0:5090"
$env:VIBEVOICE_API_KEY = "replace-with-a-long-random-secret"
$env:VIBEVOICE_DATA_DIRECTORY = "D:\VibeVoiceServiceData"
.\SteamVRTranslator.VibeVoice.Server.exe
```

Linux x64 示例：

```bash
VIBEVOICE_LISTEN_URL=http://0.0.0.0:5090 \
VIBEVOICE_API_KEY='replace-with-a-long-random-secret' \
VIBEVOICE_DATA_DIRECTORY=/srv/vibevoice \
./SteamVRTranslator.VibeVoice.Server
```

然后在主程序字幕页填写 `http://服务器地址:5090` 和同一 API Key。不要把无 TLS 的端口直接暴露到公网；跨公网使用时应通过 Caddy、Nginx 或 VPN 提供 TLS 和访问控制。

## 管理与推理接口

| 方法 | 路径 | 说明 |
| --- | --- | --- |
| `GET` | `/health` | 无敏感信息的存活检查 |
| `GET` | `/api/v1/status` | 安装、下载、运行时和错误状态 |
| `POST` | `/api/v1/install` | 下载所选 CrispASR 运行时和 Q4_K 模型 |
| `POST` | `/api/v1/install/runtime` | 只下载当前选择的 CrispASR 运行时 |
| `POST` | `/api/v1/install/model` | 只下载共享的 VibeVoice Q4_K 模型 |
| `POST` | `/api/v1/install/cancel` | 取消当前下载并保留可续传的 `.part` 文件 |
| `POST` | `/api/v1/configure` | 保存 backend、device、threads 和镜像源 |
| `POST` | `/api/v1/runtime/start` | 启动常驻推理进程 |
| `POST` | `/api/v1/runtime/stop` | 停止常驻推理进程 |
| `GET` | `/v1/models` | OpenAI 兼容模型列表 |
| `POST` | `/v1/audio/transcriptions` | OpenAI 兼容 multipart 音频转写 |

配置了 API Key 后，除 `/health` 外均需提供：

```http
Authorization: Bearer <API Key>
```

转写请求由服务原样流式转发给仅监听 `127.0.0.1` 随机端口的 CrispASR 子进程。服务在首个请求前自动启动已安装的运行时，并等待模型加载完成；后续请求复用同一常驻进程。

## 运行时选择

- `CPU`：安装体积最小，适合无可用 GPU 或希望行为稳定的服务器。
- `Vulkan`：适用于 NVIDIA、AMD 和 Intel Vulkan 设备，可把负载迁移到空闲集成显卡；实际速度可能更快或更慢。
- `CUDA`：运行时包较大，只适用于具备兼容 NVIDIA 驱动的机器。

切换 backend 会安装并使用对应 CrispASR 包。不同运行时可并存，模型权重只保存一份。下载支持 `.part` 断点续传；模型安装后会检查文件大小和 GGUF 文件头，运行时压缩包必须能解出 `crispasr` 可执行文件。

## 当前边界

- 当前内置模型固定为 `vibevoice-asr-q4_k.gguf`，约 4.5 GB。
- VibeVoice 路径面向长音频和匿名说话人转写，不替代低延迟 VRChat PTT 的 SenseVoice 默认路径。
- 服务当前不提供请求级队列上限；共享部署时应在反向代理限制并发、上传大小和访问来源。
- Windows 发布脚本只生成 Windows x64 服务。Linux 部署需要在 Linux 上对服务项目执行 `dotnet publish`，CrispASR Linux x64 运行时仍可由管理页下载。
