---
feature: fix-android-mirror-aspect-ratio
status: delivered
updated: 2026-08-11
branch: fix/android-mirror-aspect-ratio
commits: 6355a8c..ce9c827
---

# 修复 Android 镜像窗口压缩后边框错位和点击异常

## Report

**What was built** — 修改了 `WpfSpatialOverlayHost.cs` 的 `ReadWindowMetrics`（603-633 行）和 `RenderPixels`（220-247 行），让 `WpfWindowOverlaySource` 的所有尺寸快照统一使用 `Window.Width/Height` 属性而非 Content 的 `ActualWidth/ActualHeight`。这使 WPF 主层 quad 纵横比始终与窗口设计尺寸一致，从而视频层 quad（OpenVR 按纹理比例决定高度）与 WPF 边框层保持对齐。

竖屏 Android 镜像（1080x2400）在 200% 缩放/小逻辑屏环境中，窗口设计高度 954 被系统压缩到 815。旧代码从 Content Actual 读取压缩值导致 WPF plane 纵横比（0.5018）与视频层比例（设计值 0.45）不一致，视频 quad 超出边框（"边框小于渲染画面"）且点击命中平面与显示平面错位（"点击异常"）。新代码统一用 Window.Width/Height 属性（设计值），消除此偏差。

其他 WPF 窗口（VrControlPanelWindow、字幕窗口等）在构造前已 Show，Window.Width/Height 为 WPF 同步的实际值，无回归影响。

**Verification** — `dotnet build SteamVRTranslator.sln -c Release`: PASS, 0 warnings 0 errors。`dotnet test SteamVRTranslator.sln -c Release --no-build`: PASS, 354 全部通过（Core.Tests:15 + App.Tests:339）。Review 代理审查通过：Spec 合规性 ✓、正确性 ✓、代码库一致性 ✓。

**Journey log** — 1) 静态推理初期错误假设"视频被拉伸"，后通过 OpenVR quad 高度跟随纹理纵比的注释（`SpatialQuadOverlayManager.cs:242`）纠正，确认视频层永远正确。2) 用 HWND 窗口矩形 + 像素级渲染验证锁定 Content Actual 被压缩是根因。3) 200% DPI 缩放环境是复现关键（虚拟屏 1280x800 逻辑，竖屏窗口 954 > 800 必然压缩）。

## [S1] Problem

竖屏设备（1080x2400）在 200% 缩放的桌面上，窗口设计高度 954 超过系统逻辑工作区高度，窗口被系统压缩到 815。

`WpfWindowOverlaySource.ReadWindowMetrics`（`WpfSpatialOverlayHost.cs:603-633`）读取 Content 的 `ActualWidth/ActualHeight`（压缩值 409x815）计算 `AspectRatio`（0.501840），而 `AndroidMirrorWindow` 的 `DirectPixelRegion`（`AndroidMirrorWindow.xaml.cs:98-104`）基于 `Window.Width/Height` 属性（设计值 409x954）计算。

`CreateWpfWindowPlane`（`SteamVrTranslationRuntime.cs:2586-2587`）用 AspectRatio 决定 plane 纵横比 → WPF 主层 quad 高度 = `plane.Width/AspectRatio`（压缩值 1.99W），视频层 quad 高度 = 视频纹理比例（设计值 2.20W）。两个 quad 高度不一致 → 视频层比 WPF 边框层高 → "边框小于渲染画面"；命中检测基于 WPF plane 而画面在视频 quad → "点击异常"。

横屏设备（窗口约 560 高）或大屏不触发压缩时正常。

## [S2] Design

**策略**：让 `WpfWindowOverlaySource` 的三个尺寸快照统一使用 `Window.Width/Height` 属性——它始终反映窗口的逻辑设计尺寸（Show 前为设计值，Show 后为实际值）——替代从 Content 布局中读取 `ActualWidth/ActualHeight`。

**文件**：`src/SteamVRTranslator.App/SteamVR/WpfSpatialOverlayHost.cs`

**改动点 1 — `ReadWindowMetrics`（603-633 行）**：

- 移除 `EnsureLayout(visual, ...)` + `element.ActualWidth/ActualHeight` 取内容尺寸的逻辑
- 改用 `_window.Width` / `_window.Height` 属性作为内容尺寸（`requestedWidth/requestedHeight` 已经取过，直接用于 `width/height`）
- 保持 `EnsureLayout` 调用以建立 HWND（`EnsureHandle()` 已在前面执行）并确保视觉树可用
- `_fallbackClientWidth/_fallbackClientHeight` 直接用 `Math.Round(_window.Width)` / `Math.Round(_window.Height)` 赋值

**改动点 2 — `RenderPixels` 中的 `EnsureLayout`（220-247 行）**：

- `EnsureLayout(visual, _fallbackClientWidth, _fallbackClientHeight)` 不变——它用属性值做布局，与改动点 1 一致
- `sourceWidth/sourceHeight` 取值：删除 `element.ActualWidth > 1 ? element.ActualWidth : _fallbackClientWidth`，直接使用 `_fallbackClientWidth/_fallbackClientHeight`（窗口设计尺寸）。这确保渲染目标的逻辑源尺寸与 plane 纵横比一致。

**影响范围**：仅 AndroidMirror 窗口从 `Actual`→`设计` 切换。其他通过 `IWpfSpatialOverlayHost` 注册的 WPF 窗口（VrControlPanelWindow、字幕窗口等）在构造前已 Show，其 `Window.Width/Height` 属性已被 WPF 同步为实际尺寸，无回归影响。

## [S3] Out of Scope

- 不修改 AndroidMirrorWindow 或 createplane 逻辑
- 不添加系统工作区检测
- 不修改其他 overlay 层（Chrome、Progress、Pointer）

## Tasks

- [X] T1: 修改 ReadWindowMetrics 用 Window.Width/Height 代替 Content Actual — acceptance: 竖屏 1080x2400 下 AspectRatio = 0.428721（设计值） (covers: S2)
- [X] T2: 修改 RenderPixels 源尺寸用 fallback — acceptance: 渲染纹理内容纵横比与 plane 一致 (covers: S2; depends: T1)
- [X] T3: 运行 App.Tests + Core.Tests 全部通过 — acceptance: dotnet test 零失败 (covers: S2; depends: T1, T2)
