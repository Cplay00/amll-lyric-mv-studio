# AMLL Lyric MV Studio — WinUI 3 图形界面

`gui/` 目录是工作台的 Windows 原生外壳，用 **WinUI 3**（微软官方 UI 框架）构建。
它把命令行流程包装成一键可用的图形程序：选素材、实时预览、调参数、点按钮出片。

## 关于 WinUI 3 的获取方式

WinUI 3 以 NuGet 包形式随 **Windows App SDK** 分发，本项目通过
`<PackageReference Include="Microsoft.WindowsAppSDK" />` 引用（见 `AmllMvStudio.csproj`），
无需克隆 [microsoft/microsoft-ui-xaml](https://github.com/microsoft/microsoft-ui-xaml) 框架源码仓库——
那是给框架贡献者用的，从源码构建对应用开发属于绕远路。

## 一键启动

```bat
run-gui.cmd
```

该脚本会自动完成：构建工作台网页（`dist/`）→ 检查 .NET SDK → 构建 GUI → 启动。
首次构建需要几分钟（要下载 Windows App SDK 等 NuGet 包）。

也可以直接启动并预载素材：

```bat
run-gui.cmd --audio "歌曲.flac" --lyric "歌词.ttml" --cover "封面.jpg" --preset 4k --fps 60
```

### 环境要求

| 组件 | 要求 |
| --- | --- |
| .NET SDK | 8.0 或更高 |
| Windows App SDK | 由 NuGet 自动还原（1.8） |
| WebView2 运行时 | Win10/11 通常已内置，缺失时安装包会自动补装 |
| Node.js | 渲染时调用（≥18）；安装版内嵌 24.x，无需单独安装 |
| Chromium 内核浏览器 | 仅 Lite 版需要系统已装 Edge/Chrome；完整版内嵌 Chromium |

## 界面说明

程序采用左侧导航 + 右侧内容的布局，共有预览、导出成片和关于三页。

### 预览页

顶部一行按钮选择素材（音频 / 歌词 / 封面），中间是**内嵌的实时预览舞台**，
底部是原生播放控制：回到开头、后退 5 秒、播放/暂停、前进 5 秒、进度条拖动。

预览使用 WebView2 加载工作台页面。为了让页面与素材**同源**（否则无法取用本地音频、
封面），内置了一个轻量 HTTP 服务（`Services/LocalAssetServer.cs`），
它把 `dist/` 作为站点根目录，并把选中的素材挂到 `/__mv/` 下，同时支持 Range 请求
（拖动进度条必需）。服务只监听 `127.0.0.1` 的随机空闲端口，并校验 Host 头、
限定只允许本地源导航，阻断 DNS rebinding 与外站跳转。

### 导出页

左侧是参数面板，右侧是进度与实时日志。

| 参数 | 默认 | 说明 |
| --- | --- | --- |
| 分辨率 | 1080P | 1080P / 1440P / 4K / 竖屏 9:16 / 方形 1:1 |
| 帧率 | 60 FPS | 24–60 |
| 画质 CRF | 16 | 10–30，越小越清晰、文件越大 |
| 背景流动速度 | 2.0 | 0–5 |
| 背景渲染倍率 | 1.00 | 0.25–1，降低可提速 |
| 片头停留 | 0 ms | 在首帧停留后再开始音频与歌词时间轴 |
| 片尾留白 | 1200 ms | 结尾多录一段，避免画面戛然而止 |

参数与预览页、命令行渲染三者共享同一套设置（`Services/AppState.cs`）。

点「开始渲染」后，界面会调用 `scripts/render-mv.mjs`，并解析脚本输出的
`@@PROGRESS` 协议行来驱动进度条：

```
@@PROGRESS {"stage":"server","port":5274}
@@PROGRESS {"stage":"ready","durationMs":20000,"lyricLines":8}
@@PROGRESS {"stage":"capture","frames":476,"elapsedMs":15004,"durationMs":20000,"percent":67}
@@PROGRESS {"stage":"encode","percent":99}
@@PROGRESS {"stage":"done","percent":100,"outPath":"…\\out\\demo.mp4","bytes":11092948}
```

渲染完成后，「定位输出」会在资源管理器中选中成片，「播放成片」用系统默认播放器打开。
若输出文件已存在，首次点击「开始渲染」只提示，再次点击才覆盖；输出路径与
音频/歌词/封面同路径会被直接拒绝（ffmpeg 以 `-y` 覆盖写，同路径会损坏源文件）。

## 工程结构

```
gui/
  AmllMvStudio.csproj          项目文件（非打包模式，直接双击 exe 即可运行）
  app.manifest                 DPI 感知与长路径支持
  App.xaml(.cs)                应用入口、全局异常落盘
  MainWindow.xaml(.cs)         导航框架、命令行参数解析
  Services/
    AppState.cs                两页共享的素材与参数状态
    LocalAssetServer.cs        内置本地 HTTP 服务（含 Range 支持）
    RenderContracts.cs         GUI / 预览 / Node 渲染器共享的规范值域
    RenderRunner.cs            调用渲染脚本并解析进度协议
    RuntimePaths.cs            内嵌 Node / FFmpeg / 浏览器的解析优先级（含系统浏览器回退）
    StagePresets.cs            分辨率预设（与前端保持一致）
    ShellReveal.cs             在资源管理器中定位文件 / 打开成片（不经命令行）
  Views/
    PreviewPage.xaml(.cs)      WebView2 预览 + 原生播放控制
    ExportPage.xaml(.cs)       渲染参数、进度、日志、输出操作
  tests/GuiContractTests/      跨层契约测试（纯 CLI，dotnet run 即可）
  tools/
    WinCapture/                窗口截图工具（PrintWindow，可截 WebGL 图层）
    GuiSmokeTest/              UI Automation 冒烟测试
    OutputActionTest/          输出操作的安全回归测试
```

## 构建与调试

```bash
# 构建（Debug）
dotnet build gui/AmllMvStudio.csproj

# 发布 Release
dotnet build gui/AmllMvStudio.csproj -c Release

# 契约测试（无需 GUI）
dotnet run --project gui/tests/GuiContractTests
```

注意：修改 `src/` 下的网页代码后需要 `npm run build` 重新生成 `dist/`，
预览页才会用到新版本（预览页右上角的刷新按钮只会重新加载页面，不会重新构建）。

启动期若发生异常，日志写在 `%LOCALAPPDATA%\AmllMvStudio\startup-error.log`。

## 已知实现细节

以下几点是排查过程中确认的非显然行为，改动相关代码时请注意：

**1. AMLL 背景在暂停时不绘制**

`MeshGradientRenderer.pause()` 会直接取消挂起的那一帧，导致画布既不同步尺寸、也不出图，
表现为预览一片漆黑。`src/App.tsx` 中的 `syncBackgroundSize` 会在尺寸或播放状态变化时
主动触发「暂停 → 恢复」让画布至少绘制一帧，若不应播放则稍后再暂停回去（用令牌防止竞态）。

**2. XAML 解析期事件回调会访问尚未创建的控件**

`Slider.Value` 赋值、`NavigationView` 初始选中都会在解析过程中触发事件，
此时同级的 `TextBlock`、`UserControl` 还没构造完，直接访问会抛
`XamlParseException` 并让进程静默退出（错误码 `0xC000027B`）。
因此 `ExportPage` 用 `_ready` 标志屏蔽解析期回调，`MainWindow` 对内容控件做了空值检查。

**3. 项目目录下的子项目会被误编译**

SDK 风格项目默认递归包含 `**/*.cs`，会把 `tools/` 下的辅助工具一起吃进来导致
`Program` 重复定义。`AmllMvStudio.csproj` 中已用 `Compile Remove="tools\**"` 等排除。

**4. 输出操作不使用命令行拼接**

文件定位与播放均不经 shell：定位用 `SHParseDisplayName` + `SHOpenFolderAndSelectItems`
（路径以 PIDL 传递，无命令行、无需转义）；播放用 WinRT `Launcher.LaunchFileAsync`
并限定 `.mp4/.webm/.mkv/.mov/.m4v/.avi` 白名单，`.exe`/`.bat` 无法被拉起。
历史版本曾用 `explorer.exe /select,"..."` 字符串拼接，经安全评审后重构为上述实现
（`tools/OutputActionTest` 提供回归验证：含 `& ( )` 的文件名可正确定位且不注入）。
