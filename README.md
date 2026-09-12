# AMLL Lyric MV Studio

![AMLL Lyric MV Studio 图标](gui/Assets/app.png)

AMLL Lyric MV Studio 0.2.0 是由 **Cplay00** 开发、基于
[applemusic-like-lyrics（AMLL）](https://github.com/amll-dev/applemusic-like-lyrics)
的 Apple Music 风格逐字歌词 MV 制作与导出工具。载入音频、歌词和专辑封面后，可实时预览并导出包含流光弥散背景、逐字渐进高亮和行间景深虚化的成片。

> **平台**：Windows 10 1809+（x64）。GUI 为 WinUI 3 原生程序；命令行渲染管线也可在 macOS / Linux 上独立使用（浏览器探测已覆盖三平台路径）。
>
> **许可证**：AGPL-3.0-only（本仓库与 AMLL 渲染组件），详见 [LICENSE](LICENSE) 与
> [installer/NOTICE.txt](installer/NOTICE.txt)、[installer/THIRD-PARTY-NOTICES.md](installer/THIRD-PARTY-NOTICES.md)。
> 安装包内嵌的 FFmpeg 为 GPLv3 构建。本项目与 Apple Inc. 无关，仅为其歌词视觉风格的复刻实现。

## 核心功能

- 原生 WinUI 3 桌面端与浏览器工作台，支持实时预览和参数调整。
- 支持 TTML、LRC、YRC、QRC、LYS 等歌词格式，TTML 可呈现逐字、翻译、音译及背景人声。
- Playwright 逐帧抓取配合 FFmpeg 编码，输出恒定帧率 H.264/AAC MP4。
- 1080p、1440p、4K、竖屏、方形预设以及自定义分辨率、帧率和质量。
- Full 与 Lite 两种 Windows 安装包，共用相同应用源码。

## 架构概览

浏览器工作台由 React、TypeScript、Vite 和 AMLL npm 包组成；WinUI 3 GUI 通过 WebView2 承载预览并调用本地渲染服务；命令行渲染器使用 Node.js、Playwright 与 FFmpeg。安装构建将前端产物、.NET 自包含 GUI 和固定版本运行时组装为隔离的 x64 包。

## 作者与上游

本项目由 **Cplay00** 创建和维护，歌词渲染能力基于 AMLL 上游社区成果。AMLL 的名称、源码和 npm 包归其相应贡献者所有；本项目与 Apple Inc. 无隶属或认可关系。

## npm 依赖与上游

AMLL 能力来自 npm 上的 `@applemusic-like-lyrics/core@0.5.2`、`react@0.5.2` 和 `lyric@1.0.2`，上游仓库为 [amll-dev/applemusic-like-lyrics](https://github.com/amll-dev/applemusic-like-lyrics)。渲染栈还使用 React 18.3.1、PixiJS 7.4.3、Vite 7.3.6、Playwright Core 1.63.0 和安装包固定的 FFmpeg 8.1.2。精确依赖树与完整性值由 `package-lock.json` 锁定；许可证与获取来源见 [第三方声明](installer/THIRD-PARTY-NOTICES.md)。

经仓库跟踪文件检查，本项目没有复制未修改的 AMLL 上游文件，也没有使用或伪装 AMLL Git submodule；所有 AMLL 上游代码均通过声明的 npm 包获取。


## 已安装的组件

| 组件 | 版本 | 作用 |
| --- | --- | --- |
| `@applemusic-like-lyrics/core` | 0.5.2 | 核心渲染引擎：`LyricPlayer`（逐字歌词）、`MeshGradientRenderer`（流体弥散背景） |
| `@applemusic-like-lyrics/react` | 0.5.2 | React 绑定：`<LyricPlayer />`、`<BackgroundRender />` |
| `@applemusic-like-lyrics/lyric` | 1.0.2 | 歌词解析/序列化：TTML、LRC、LRC A2、YRC、QRC、ESLyric、LYL、LYS、LQE |
| `@pixi/*` | 7.4.3 | AMLL 背景的 PixiJS 渲染后端（含 `@pixi/filter-bulge-pinch@5.1.1`，其版本号体系与主包不同） |
| `react` / `react-dom` | 18.3.1 | UI 框架 |
| `vite` + `typescript` | 7.3.6 / 5.9.3 | 构建与类型检查（Vite 的 esbuild 为 0.28.2） |
| `playwright-core` | 1.63.0 | 命令行渲染时驱动系统 Chrome/Edge 抓帧 |
| FFmpeg（Windows 安装包） | 8.1.2 自建最小 GPL 构建 | 安装包固定携带的静态 ffmpeg（libx264）；对应源码闭包随发布集提供，不使用 `@ffmpeg-installer` 的开发机二进制 |

> 说明：AMLL 官方要求必须通过打包器引入（不支持 CDN），本工程已用 Vite 配好。
> `@pixi/filter-bulge-pinch` 的最新版是 5.1.1，它对应的是 Pixi v7，属于正常现象。

## 快速开始

### 方式一：图形界面（推荐日常使用）

```bat
run-gui.cmd
```

一键构建并启动 WinUI 3 桌面程序：左侧导航分「预览」和「导出成片」两页，
选好素材即可实时预览 Apple Music 效果，调参后点「开始渲染」出片。

也可直接带素材启动：

```bat
run-gui.cmd --audio "歌曲.flac" --lyric "歌词.ttml" --cover "封面.jpg" --preset 4k
```

详见 [gui/README.md](gui/README.md)。

### 方式二：浏览器工作台

```bash
npm install          # 安装依赖
npm run demo:assets  # 生成演示素材（合成音乐 + 逐字 TTML + 程序化封面）
npm run dev          # 启动工作台，浏览器打开 http://127.0.0.1:5273
```

打开页面后，在左侧依次载入：

1. **音频文件** — mp3 / flac / m4a / wav / ogg
2. **歌词文件** — 推荐 TTML（Apple Music 官方格式，逐字最精细）；LRC / YRC / QRC / LYS 等亦可
3. **专辑封面** — 背景的弥散取色来源；不传则回退为默认渐变

演示素材由 `npm run demo:assets` 生成到 `public/demo/`（均为程序化合成，无第三方版权）。

### 快捷键

| 按键 | 作用 |
| --- | --- |
| `空格` | 播放 / 暂停 |
| `←` / `→` | 后退 / 前进 5 秒 |
| `Home` | 回到开头 |

## 导出成片

### 方式一：命令行逐帧渲染（推荐，画质最好）

以真实播放速度驱动页面，通过 CDP 抓取每一帧，再用 ffmpeg 把**原始音频文件无损合入**。
输出为严格恒定帧率的 H.264 + AAC MP4。图形界面的「开始渲染」按钮调用的就是这条管线。

```bash
npm run render -- \
  --audio "你的歌曲.flac" \
  --lyric "你的歌词.ttml" \
  --cover "你的封面.jpg" \
  --preset 4k --fps 60
```

常用参数：

| 参数 | 说明 |
| --- | --- |
| `--audio` / `--lyric` / `--cover` | 输入素材路径（`--audio` 必填） |
| `--preset` | `1080p` / `1440p` / `4k` / `vertical`（9:16 竖屏）/ `square` |
| `--width` `--height` | 自定义分辨率，优先级高于 `--preset` |
| `--fps` | 输出帧率，默认 `60` |
| `--out` | 输出路径，默认 `out/<歌曲名>-<宽>x<高>-<帧率>fps.mp4` |
| `--crf` | x264 质量，越小越好，默认 `16`（近乎视觉无损） |
| `--flow-speed` | 背景流动速度，默认 `2` |
| `--render-scale` | 背景内部分辨率倍率 0.25–1，默认 `1`（最清晰） |
| `--head` | 片头保持首帧的毫秒数，默认 `0` |
| `--tail` | 片尾额外留白毫秒数，默认 `1200` |
| `--keep-frames` | 保留中间帧与渲染日志，便于排查 |
| `--browser` | 指定 Chromium 内核浏览器路径 |

渲染流程会打印帧数、时间轴、音画偏移与编码耗时。音画对齐的偏差通常在 ±20ms 内。

### 方式二：界面内实时录制

在预览界面点「● 录制舞台」：

1. 浏览器弹出捕获授权，选择**当前标签页**；
2. 页面用 Region Capture 把画面裁剪到舞台区域，构图与预览完全一致；
3. 播放结束后自动停止并下载 WebM。

此方式依赖实时编码，帧率是「尽力而为」。**需要严格帧率和无损音轨时请用方式一。**

### 方式三：OBS 等外部采集

舞台本身是固定像素尺寸的网页元素（如 1920×1080），可直接用 OBS 的「窗口采集」抓取浏览器窗口。
优点是能叠加额外的后期元素，缺点是同样无法保证逐帧确定性。

## 界面参数说明

| 参数 | 默认 | 说明 |
| --- | --- | --- |
| 分辨率 | 1080P | 舞台按该像素尺寸真实渲染，再用 `transform` 等比缩放显示，因此预览即所得 |
| 背景帧率 | 60 | 流体背景自身的动画帧率 |
| 流动速度 | 2 | 背景流动快慢 |
| 渲染倍率 | 1.00 | 背景内部分辨率倍率，降低可提升性能 |
| 逐字渐进宽度 | 0.50 | 高亮渐变的柔和程度。`0.5` 接近 iPad 版，`1.0` 接近 Android 版 |
| 垂直对齐位置 | 0.50 | 当前歌词行在画面中的垂直位置 |
| 弹簧动画 | 开 | 歌词滚动的物理弹簧效果；关闭则回退为 CSS 过渡 |
| 景深模糊 | 开 | 非当前行的虚化 |
| 非焦点缩放 | 开 | 非当前行轻微缩小 |
| 隐藏已唱行 | 关 | 隐藏播放过的歌词行 |
| 背景静止 | 关 | 过渡完成后停止背景动画，省性能 |

## 工程结构

```
src/                          # 浏览器工作台（前端）
  App.tsx                     # 主界面：舞台、传输控制、录制
  components/ControlPanel.tsx # 左侧参数面板
  lib/lyricLoader.ts          # 多格式歌词解析与排序
  lib/recorder.ts             # 界面内舞台录制（标签页捕获 + Region Capture）
  lib/types.ts                # 类型、分辨率预设、时间格式化
  styles.css                  # 工作台样式
scripts/
  render-mv.mjs               # 命令行逐帧渲染管线（Playwright + ffmpeg）
  make-demo-assets.mjs        # 生成演示音频 / TTML / 封面
gui/                          # WinUI 3 桌面程序（原生外壳）
  MainWindow.xaml             # 导航框架
  Views/PreviewPage.xaml      # WebView2 预览 + 播放控制
  Views/ExportPage.xaml       # 渲染参数、进度、日志
  Services/                   # 本地服务、渲染调用、共享状态
  tools/                      # 截图与 UI 自动化测试工具
public/demo/                  # 演示素材
out/                          # 导出产物
run-gui.cmd                   # 一键启动图形界面
```

## 关于歌词格式

TTML 是 Apple Music 使用的官方格式，能表达**逐字（Word 级）时间戳、翻译、音译、对唱、背景人声**等完整信息，
效果最佳。可用 [amll-ttml-tool](https://github.com/amll-dev/amll-ttml-tool) 制作与编辑。
LRC 只能表达逐行时间戳，播放时整行一起亮起；YRC / QRC / LYS 等国产逐字格式也已被支持。

## Full / Lite 与发布产物

| 版本 | 浏览器运行时 | 适用场景 | 安装包文件名 |
| --- | --- | --- | --- |
| Full | 内嵌固定版本 Chromium | 开箱即用、可复现渲染 | `AMLL-Lyric-MV-Studio-0.2.0-x64-Setup.exe` |
| Lite | 使用系统 Edge，回退 Chrome | 更小下载体积 | `AMLL-Lyric-MV-Studio-0.2.0-x64-Lite-Setup.exe` |

两种安装包都包含 GUI 所需的 WebView2 Evergreen bootstrapper、Node.js、固定 FFmpeg 8.1.2、许可证和包清单。发布时应同时提供安装包、SHA256/签名元数据、对应源码及发行说明；本仓库不会将生成的二进制视为源码的一部分。`@ffmpeg-installer/ffmpeg` 仅是源码开发依赖，不是 Windows 安装包运行时。

## 构建与验证

发布版安装包由 [installer/build-installer.ps1](installer/build-installer.ps1) 分别组装 Full/Lite；两者都通过后，由 [installer/finalize-release.ps1](installer/finalize-release.ps1) 一次生成发布集和应用源码归档。构建步骤为：
干净 `npm ci` → 前端构建 → .NET 8 自包含发布 → 下载并校验固定版本运行时
（Node 24 / 本地构建的静态 FFmpeg 8.1.2 / Chromium 153 / WebView2 bootstrapper）→ 隔离冒烟验证 → Inno Setup 打包。

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\installer\build-installer.ps1              # 完整版（内嵌 Chromium，约 260MB）
.\installer\build-installer.ps1 -Lite        # 精简版（不含 Chromium，渲染调用系统 Edge/Chrome）
```

两种变体共用同一源码：Lite 版省去约 180MB 的内嵌浏览器，渲染时自动回退到
系统已安装的 Microsoft Edge 或 Google Chrome（GUI 预览始终使用系统 WebView2 运行时）。
构建后可分别执行 `installer/verify-package.ps1`；Full 检查内嵌 Chromium，Lite 使用
`-SystemBrowser` 确认系统浏览器发现。验证器还检查清单哈希、应用图标、Node/FFmpeg 版本、
GUI 启动以及一秒钟隔离渲染。详见 [installer/README.md](installer/README.md)。

## 发布

0.2.0 的 Windows x64 产物命名为 `AMLL-Lyric-MV-Studio-0.2.0-x64-Setup.exe` 和
`AMLL-Lyric-MV-Studio-0.2.0-x64-Lite-Setup.exe`。发布前应分别构建和运行完整包验证，
再附带生成的 `.json` SHA256/签名元数据、对应源码、许可证及发行说明；请勿将旧的
`AMLL-MV-Studio-*` 产物误标为 0.2.0。`finalize-release.ps1` 在 Full/Lite 任一缺失、自建 FFmpeg 8.1.2 对应源码闭包（`amll-ffmpeg-build-source`，含 `build.sh`）哈希校验失败时拒绝准备 final release。

## 常见问题

**背景一片黑 / 尺寸不对？**
AMLL 的背景只在自身动画帧里同步画布尺寸。本工程已处理：舞台尺寸或播放状态变化时会主动触发一次重算。
若仍异常，请确认浏览器启用了硬件加速（`chrome://gpu` 中 WebGL2 为 Hardware accelerated）。

**命令行渲染报「未找到 Chromium 内核浏览器」？**
安装 Chrome 或 Edge，或用 `--browser "C:/路径/到/msedge.exe"` 指定。

**端口被占用？**
`npm run render` 会自动改用 5274 起的空闲端口；也可用 `--port` 指定。

**渲染很慢？**
抓帧速度取决于实时播放，成片多长就需多久（另加编码时间）。4K 60FPS 对显卡有一定压力，
可先用 `--preset 1080p` 验证效果，再出 4K。
