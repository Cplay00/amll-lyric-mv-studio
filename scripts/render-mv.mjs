#!/usr/bin/env node
import { createReadStream, existsSync, mkdirSync, mkdtempSync, readdirSync, rmSync, statSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { basename, dirname, extname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { spawn } from "node:child_process";
import { createRequire } from "node:module";
import { createServer } from "vite";
import { createServer as createNetServer } from "node:net";
import { chromium } from "playwright-core";
import { performance } from "node:perf_hooks";
import {
  AUTO_ENCODER_CANDIDATES,
  CANONICAL_RENDER_OPTIONS,
  VIDEO_ENCODERS,
  assertCopyTimingCompatible,
  buildCfrTimeline,
  buildEncoderProbeArgs,
  buildFfmpegArgs,
  chooseEncoder,
  formatConcatTimeline,
  normalizeCanonicalOptions,
  normalizeCapturedFrames,
  parseEncoderListing,
  parseEncoderPresetHelp,
  parseFfmpegMajorVersion,
  resolveEncoderPreset,
  resolveRuntimeTools,
  validateContainer,
} from "./render-lib.mjs";

const require = createRequire(import.meta.url);
const SCRIPT_PATH = fileURLToPath(import.meta.url);
const PROJECT_ROOT = resolve(dirname(SCRIPT_PATH), "..");
const PRESETS = Object.freeze({
  "1080p": { width: 1920, height: 1080 },
  "1440p": { width: 2560, height: 1440 },
  "4k": { width: 3840, height: 2160 },
  vertical: { width: 1080, height: 1920 },
  square: { width: 1080, height: 1080 },
});
const VALUE_FLAGS = new Set([
  "audio", "lyric", "cover", "out", "preset", "width", "height", "fps", "flow-speed",
  "render-scale", "crf", "quality", "tail", "port", "browser", "ffmpeg", "encoder",
  "encoder-preset", "audio-codec", "audio-bitrate", "browser-gpu", "title", "artist", "album",
  "font-family", "font-scale", "font-weight", "secondary-mode", "show-song-info", "show-artist",
  "show-album", "show-artwork", "show-controls", "artwork-scale", "lyric-align", "lyric-offset",
  "lyric-width", "lyric-fade", "background-dim", "background-saturation", "background-blur",
  "duration",
]);
const SWITCH_FLAGS = new Set(["help", "list-encoders", "keep-frames", "progress-json"]);
const FRONTEND_QUERY = Object.freeze({
  title: "title", artist: "artist", album: "album", "font-family": "fontFamily",
  "font-scale": "fontScale", "font-weight": "fontWeight", "secondary-mode": "secondaryMode",
  "show-song-info": "showSongInfo", "show-artist": "showArtist", "show-album": "showAlbum",
  "show-artwork": "showArtwork", "show-controls": "showControls",
  "artwork-scale": "artworkScale", "lyric-align": "lyricAlign", "lyric-offset": "lyricOffset",
  "lyric-width": "lyricWidth", "lyric-fade": "lyricFade", "background-dim": "backgroundDim",
  "background-saturation": "backgroundSaturation", "background-blur": "backgroundBlur",
});
const HELP = `AMLL MV renderer
Usage: node scripts/render-mv.mjs --audio <path> [options]

Input/output:
  --audio <path>              Audio input (required)
  --lyric <path>              Lyric input (TTML/LRC/YRC/QRC/...)
  --cover <path>              Artwork input
  --out <path>                Output (.mp4 default; .mov/.mkv supported)
  --preset <id>               1080p|1440p|4k|vertical|square (default 1080p)
  --width/--height <px>       Override preset dimensions (16..8192)
  --fps <n>                   Output FPS (1..240, default 60)
  --tail <ms>                 Retain ending video/audio padding (default 1200)
  --duration <ms>             Limit source duration (useful for smoke renders)

Encoding:
  --encoder <name>            auto|${VIDEO_ENCODERS.join("|")}
  --encoder-preset <name>     Encoder speed/quality preset
  --quality <n>               Quality 0..51 (default 16); --crf remains compatible
  --audio-codec <name>        aac|copy|flac (default aac)
  --audio-bitrate <rate>      AAC bitrate, e.g. 320k
  --list-encoders             Print machine-readable JSON and exit

Browser/query:
  --browser-gpu <mode>        auto|on|off (default auto)
  --flow-speed <n>            0..5 (default 2)
  --render-scale <n>          0.25..1 (default 1)
  --title/--artist/--album <text>
  --font-family <text> --font-scale <0.5..2> --font-weight <100..900>
  --secondary-mode <none|translation|roman|both> (default ${CANONICAL_RENDER_OPTIONS.secondaryMode})
  --show-song-info/--show-artist/--show-album/--show-artwork/--show-controls <true|false>
  --artwork-scale <0.5..1.5> --lyric-align <left|center|right>
  --lyric-offset <-600..600> Vertical offset in pixels (default ${CANONICAL_RENDER_OPTIONS.lyricOffset})
  --lyric-width <30..80>       Lyric width percentage (default ${CANONICAL_RENDER_OPTIONS.lyricWidth})
  --lyric-fade <0..0.3>        Lyric edge feather as stage-height ratio (default ${CANONICAL_RENDER_OPTIONS.lyricFade}; 0 disables)
  --background-dim <0..0.9> --background-saturation <0..2> (ratio; default ${CANONICAL_RENDER_OPTIONS.backgroundSaturation})
  --background-blur <0..100>

Compatibility: --crf, --keep-frames, --progress-json, --browser, --ffmpeg and --port are retained.
`;

function parseArgs(argv) {
  const values = {};
  for (let i = 0; i < argv.length; i++) {
    const token = argv[i];
    if (!token.startsWith("--") || token === "--") throw new Error(`无法识别参数：${token}`);
    const equals = token.indexOf("=");
    const key = token.slice(2, equals < 0 ? undefined : equals);
    if (!VALUE_FLAGS.has(key) && !SWITCH_FLAGS.has(key)) throw new Error(`未知参数：--${key}`);
    if (Object.hasOwn(values, key)) throw new Error(`参数重复：--${key}`);
    if (SWITCH_FLAGS.has(key)) {
      if (equals >= 0) throw new Error(`--${key} 不接受值。`);
      values[key] = true;
      continue;
    }
    const value = equals >= 0 ? token.slice(equals + 1) : argv[++i];
    if (value === undefined || value.startsWith("--") || value === "") {
      throw new Error(`--${key} 缺少值（若值本身以 -- 开头，请改用 --${key}=值 形式）。`);
    }
    values[key] = value;
  }
  return values;
}

function numberArg(args, name, fallback, min, max, integer = false) {
  const raw = args[name] ?? fallback;
  const value = Number(raw);
  if (!Number.isFinite(value) || value < min || value > max || (integer && !Number.isInteger(value))) {
    throw new Error(`--${name} 必须是 ${min}..${max} 的${integer ? "整数" : "有限数值"}。`);
  }
  return value;
}

function enumArg(args, name, fallback, choices) {
  const value = String(args[name] ?? fallback);
  if (!choices.includes(value)) throw new Error(`--${name} 必须是：${choices.join(" / ")}。`);
  return value;
}

function boolArg(args, name) {
  if (args[name] === undefined) return undefined;
  if (args[name] !== "true" && args[name] !== "false") throw new Error(`--${name} 必须明确为 true 或 false。`);
  return args[name] === "true";
}

function parseOptions(argv) {
  const args = parseArgs(argv);
  if (args.help) return { action: "help", args };
  const runtimeTools = resolveRuntimeTools({
    scriptPath: SCRIPT_PATH,
    explicitFfmpeg: args.ffmpeg,
    explicitBrowser: args.browser,
  });
  const ffmpeg = findFfmpeg(runtimeTools.ffmpeg);
  const ffmpegPath = ffmpeg.path;
  if (args["list-encoders"]) return { action: "list-encoders", args, ffmpegPath, ffmpegSource: ffmpeg.source };
  if (!args.audio) throw new Error("缺少 --audio。用 --help 查看用法。");
  const audioPath = resolve(args.audio);
  const lyricPath = args.lyric ? resolve(args.lyric) : null;
  const coverPath = args.cover ? resolve(args.cover) : null;
  for (const [flag, path] of [["audio", audioPath], ["lyric", lyricPath], ["cover", coverPath]]) {
    if (path && !existsSync(path)) throw new Error(`找不到文件（--${flag}）：${path}`);
  }
  const presetName = String(args.preset ?? "1080p");
  const preset = PRESETS[presetName];
  if (!preset) throw new Error(`--preset 必须是：${Object.keys(PRESETS).join(" / ")}。`);
  const width = numberArg(args, "width", preset.width, 16, 8192, true);
  const height = numberArg(args, "height", preset.height, 16, 8192, true);
  const qualityDefault = args.crf ?? 16;
  if (args.quality !== undefined && args.crf !== undefined) throw new Error("--quality 与兼容参数 --crf 不能同时使用。");
  const durationLimitMs = args.duration === undefined ? null : numberArg(args, "duration", 0, 1, 86_400_000, true);
  const encoder = enumArg(args, "encoder", "auto", ["auto", ...VIDEO_ENCODERS]);
  const canonical = normalizeCanonicalOptions({
    secondaryMode: args["secondary-mode"],
    lyricWidth: args["lyric-width"],
    lyricFade: args["lyric-fade"],
    lyricOffset: args["lyric-offset"],
    backgroundSaturation: args["background-saturation"],
    browserGpu: args["browser-gpu"],
  });
  const options = {
    action: "render", args, ffmpegPath, ffmpegSource: ffmpeg.source, runtimeTools,
    audioPath, lyricPath, coverPath, presetName, width, height,
    fps: numberArg(args, "fps", 60, 1, 240),
    flowSpeed: numberArg(args, "flow-speed", 2, 0, 5),
    renderScale: numberArg(args, "render-scale", 1, 0.25, 1),
    tailMs: numberArg(args, "tail", 1200, 0, 600_000, true),
    durationLimitMs,
    port: numberArg(args, "port", 5273, 1, 65535, true),
    quality: numberArg({ quality: args.quality ?? qualityDefault }, "quality", 16, 0, 51),
    encoder,
    encoderPreset: args["encoder-preset"] ? String(args["encoder-preset"]) : null,
    audioCodec: enumArg(args, "audio-codec", "aac", ["aac", "copy", "flac"]),
    audioBitrate: String(args["audio-bitrate"] ?? "320k"),
    browserGpu: canonical.browserGpu,
    canonical,
    keepFrames: Boolean(args["keep-frames"]),
    progressJson: Boolean(args["progress-json"]),
  };
  if (!/^\d+(?:\.\d+)?[kKmM]?$/.test(options.audioBitrate)) throw new Error("--audio-bitrate 格式无效，例如 320k。");
  for (const name of ["show-song-info", "show-artist", "show-album", "show-artwork", "show-controls"]) boolArg(args, name);
  const frontendRanges = [
    ["font-scale", 0.5, 2], ["font-weight", 100, 900, true], ["artwork-scale", 0.5, 1.5],
    ["background-dim", 0, 0.9], ["background-blur", 0, 100],
  ];
  for (const [name, min, max, integer] of frontendRanges) {
    if (args[name] !== undefined) numberArg(args, name, 0, min, max, Boolean(integer));
  }
  if (args["lyric-align"] !== undefined) enumArg(args, "lyric-align", "left", ["left", "center", "right"]);
  if (options.audioCodec !== "aac" && args["audio-bitrate"] !== undefined) throw new Error("--audio-bitrate 仅适用于 --audio-codec aac。");
  // 输出容器/编码组合在参数期就校验，避免抓帧数分钟后才失败（render-lib.validateContainer 同源规则）。
  options.outputPath = options.args.out
    ? resolve(options.args.out)
    : resolve(PROJECT_ROOT, "out", `${sanitize(basename(audioPath, extname(audioPath)))}-${options.width}x${options.height}-${options.fps}fps.mp4`);
  validateContainer(options.outputPath, options.encoder, options.audioCodec);
  assertCopyTimingCompatible({
    audioCodec: options.audioCodec,
    durationLimitMs: options.durationLimitMs,
    tailMs: options.tailMs,
    audioOffsetMs: 0,
  });
  return options;
}

function findFfmpeg(discovered) {
  if (discovered) return discovered;
  try {
    const installed = require("@ffmpeg-installer/ffmpeg");
    if (installed?.path) return { path: installed.path, source: "@ffmpeg-installer/ffmpeg" };
  } catch { /* optional */ }
  try {
    const staticPath = require("ffmpeg-static");
    if (staticPath) return { path: staticPath, source: "ffmpeg-static" };
  } catch { /* optional */ }
  return { path: "ffmpeg", source: "系统 PATH" };
}

function runCaptured(command, commandArgs, label, options = {}) {
  const { acceptedCodes = [0], timeoutMs = 15_000 } = options;
  return new Promise((resolvePromise, rejectPromise) => {
    const child = spawn(command, commandArgs, { stdio: ["ignore", "pipe", "pipe"] });
    let stdout = "";
    let stderr = "";
    let settled = false;
    const finish = (callback) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      callback();
    };
    const timer = setTimeout(() => {
      try { child.kill(); } catch { /* already exited */ }
      finish(() => rejectPromise(new Error(`${label} 超时（${timeoutMs} ms）。`)));
    }, timeoutMs);
    child.stdout.on("data", (chunk) => { stdout += chunk; });
    child.stderr.on("data", (chunk) => { stderr += chunk; });
    child.on("error", (error) => finish(() => rejectPromise(new Error(`${label} 无法启动：${error.message}`))));
    child.on("close", (code) => finish(() => acceptedCodes.includes(code)
      ? resolvePromise({ stdout, stderr })
      : rejectPromise(new Error(`${label} 失败（退出码 ${code}）：\n${stderr.slice(-4000)}`))));
  });
}

export async function probeEncoder(ffmpegPath, encoder, supportedPresets, execute = runCaptured) {
  let preset;
  try {
    preset = resolveEncoderPreset(encoder, null, supportedPresets);
    await execute(
      ffmpegPath,
      buildEncoderProbeArgs({ encoder, encoderPreset: preset, supportedPresets }),
      `${encoder} 320x180 真实试编码`,
      { timeoutMs: 12_000 },
    );
    return { id: encoder, compiled: true, available: true, failureReason: null, supportedPresets, defaultPreset: preset };
  } catch (error) {
    return {
      id: encoder,
      compiled: true,
      available: false,
      failureReason: String(error?.message ?? error).replace(/\s+/g, " ").trim().slice(0, 1000),
      supportedPresets,
      defaultPreset: preset,
    };
  }
}

async function probeFfmpeg(ffmpegPath) {
  const [versionResult, encodersResult, helpResult] = await Promise.all([
    runCaptured(ffmpegPath, ["-version"], "ffmpeg 版本探测"),
    runCaptured(ffmpegPath, ["-hide_banner", "-encoders"], "ffmpeg 编码器探测"),
    runCaptured(ffmpegPath, ["-hide_banner", "-h", "full"], "ffmpeg 选项探测", { acceptedCodes: [0, 1] }),
  ]);
  const compiled = parseEncoderListing(`${encodersResult.stdout}\n${encodersResult.stderr}`);
  const capabilities = new Map();
  for (const encoder of VIDEO_ENCODERS) {
    if (!compiled.has(encoder)) {
      capabilities.set(encoder, {
        id: encoder, compiled: false, available: false,
        failureReason: "当前 FFmpeg 未编译此编码器。", supportedPresets: new Set(), defaultPreset: undefined,
      });
      continue;
    }
    let supportedPresets = new Set();
    try {
      const result = await runCaptured(
        ffmpegPath, ["-hide_banner", "-h", `encoder=${encoder}`], `${encoder} preset 探测`,
        { acceptedCodes: [0, 1], timeoutMs: 8_000 },
      );
      supportedPresets = parseEncoderPresetHelp(encoder, `${result.stdout}\n${result.stderr}`);
    } catch (error) {
      capabilities.set(encoder, {
        id: encoder, compiled: true, available: false,
        failureReason: String(error?.message ?? error), supportedPresets, defaultPreset: undefined,
      });
      continue;
    }
    // Hardware sessions are probed sequentially to avoid driver/session contention causing false negatives.
    capabilities.set(encoder, await probeEncoder(ffmpegPath, encoder, supportedPresets));
  }
  const help = `${helpResult.stdout}\n${helpResult.stderr}`;
  return {
    compiled,
    capabilities,
    available: new Set([...capabilities].filter(([, item]) => item.available).map(([name]) => name)),
    presets: new Map([...capabilities].map(([name, item]) => [name, item.supportedPresets])),
    majorVersion: /(^|\s)-fps_mode(?:\s|$)/m.test(help)
      ? Math.max(5, parseFfmpegMajorVersion(`${versionResult.stdout}\n${versionResult.stderr}`) ?? 5)
      : 4,
    version: `${versionResult.stdout}\n${versionResult.stderr}`.split(/\r?\n/, 1)[0],
  };
}

function encoderReport(probe, ffmpegPath, ffmpegSource) {
  const libx264Available = probe.capabilities.get("libx264")?.available === true;
  return {
    ffmpegVersion: probe.version,
    ffmpegPath: resolve(ffmpegPath),
    ffmpegSource,
    encoders: ["auto", ...VIDEO_ENCODERS].map((name) => {
      if (name === "auto") {
        return {
          name,
          compiled: libx264Available,
          available: libx264Available,
          failureReason: libx264Available ? null : "自动编码要求可用的 libx264 CPU 回退。",
          supportedPresets: [],
        };
      }
      const capability = probe.capabilities.get(name);
      return {
        name,
        compiled: capability?.compiled === true,
        available: capability?.available === true,
        failureReason: capability?.failureReason ?? null,
        supportedPresets: [...(capability?.supportedPresets ?? [])],
        presets: [...(capability?.supportedPresets ?? [])],
        defaultPreset: capability?.available ? capability.defaultPreset : undefined,
      };
    }),
  };
}

function browserCandidates() {
  const local = process.env.LOCALAPPDATA;
  return [
    local && join(local, "Microsoft", "Edge", "Application", "msedge.exe"),
    local && join(local, "Google", "Chrome", "Application", "chrome.exe"),
    "C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe",
    "C:/Program Files/Microsoft/Edge/Application/msedge.exe",
    "C:/Program Files/Google/Chrome/Application/chrome.exe",
    "C:/Program Files (x86)/Google/Chrome/Application/chrome.exe",
    "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
    "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge",
    "/usr/bin/google-chrome", "/usr/bin/chromium", "/usr/bin/chromium-browser",
  ].filter(Boolean);
}

function findBrowser(discovered) {
  if (discovered) {
    if (!existsSync(discovered.path)) throw new Error(`指定的浏览器不存在：${discovered.path}`);
    return discovered;
  }
  const found = browserCandidates().find(existsSync);
  if (!found) throw new Error("未找到 Chromium 内核浏览器。请安装 Chrome/Edge，或用 --browser 指定。");
  return { path: found, source: "系统浏览器" };
}

function browserLaunchArgs(width, height, gpuMode) {
  const launchArgs = [
    "--autoplay-policy=no-user-gesture-required", "--mute-audio",
    "--disable-features=CalculateNativeWinOcclusion", `--window-size=${width},${height}`,
  ];
  if (gpuMode === "on") launchArgs.push("--ignore-gpu-blocklist");
  if (gpuMode === "off") launchArgs.push("--use-angle=swiftshader", "--enable-unsafe-swiftshader");
  return launchArgs;
}

function canListen(port) {
  return new Promise((done) => {
    const probe = createNetServer();
    probe.once("error", () => done(false));
    probe.once("listening", () => probe.close(() => done(true)));
    probe.listen(port, "127.0.0.1");
  });
}

async function pickPort(preferred) {
  for (let port = preferred; port < preferred + 40 && port <= 65535; port++) {
    if (await canListen(port)) return port;
  }
  throw new Error(`端口 ${preferred} 起的 40 个端口均不可用。`);
}

const assetMounts = new Map();
const MIME = Object.freeze({
  ".mp3": "audio/mpeg", ".m4a": "audio/mp4", ".aac": "audio/aac", ".flac": "audio/flac",
  ".wav": "audio/wav", ".ogg": "audio/ogg", ".opus": "audio/ogg", ".jpg": "image/jpeg",
  ".jpeg": "image/jpeg", ".png": "image/png", ".webp": "image/webp", ".gif": "image/gif",
});

function mountAsset(filePath, kind) {
  const url = `/__mv/${kind}${extname(filePath)}`;
  assetMounts.set(url, filePath);
  return url;
}

function securityHeadersPlugin() {
  return { name: "amll-mv-security-headers", configureServer(server) {
    server.middlewares.use((_request, response, next) => {
      response.setHeader("X-Content-Type-Options", "nosniff");
      response.setHeader("Referrer-Policy", "no-referrer");
      response.setHeader("Cross-Origin-Resource-Policy", "same-origin");
      response.setHeader("Content-Security-Policy", "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' blob: data:; media-src 'self' blob:; font-src 'self' data:; connect-src 'self' blob: data: ws://127.0.0.1:*; frame-ancestors 'none'");
      next();
    });
  } };
}

function assetServerPlugin() {
  return { name: "amll-mv-assets", configureServer(server) {
    server.middlewares.use((request, response, next) => {
      const path = (request.url ?? "").split("?", 1)[0];
      const filePath = assetMounts.get(path);
      if (!filePath) return next();
      try {
        const stat = statSync(filePath);
        response.setHeader("Content-Type", MIME[extname(filePath).toLowerCase()] ?? "application/octet-stream");
        response.setHeader("Content-Length", String(stat.size));
        response.setHeader("Cache-Control", "no-store");
        // stat 与打开之间存在窗口：文件被删/占用时 open 异步失败，无 error 监听会令进程崩溃
        const stream = createReadStream(filePath);
        stream.on("error", () => { response.destroy(); });
        stream.pipe(response);
      } catch (error) {
        response.statusCode = 500;
        response.end(String(error));
      }
    });
  } };
}

function buildPageQuery(options, assetUrls) {
  const query = new URLSearchParams({
    auto: "1", chrome: "0", autoplay: "0", audio: assetUrls.audio,
    preset: options.presetName, fps: String(options.fps), flowSpeed: String(options.flowSpeed),
    renderScale: String(options.renderScale), width: String(options.width), height: String(options.height),
    secondaryMode: options.canonical.secondaryMode,
    lyricOffset: String(options.canonical.lyricOffset),
    lyricWidth: String(options.canonical.lyricWidth),
    lyricFade: String(options.canonical.lyricFade),
    backgroundSaturation: String(options.canonical.backgroundSaturation),
  });
  if (assetUrls.lyric) query.set("lyric", assetUrls.lyric);
  if (assetUrls.cover) query.set("cover", assetUrls.cover);
  for (const [cliName, queryName] of Object.entries(FRONTEND_QUERY)) {
    if (options.args[cliName] !== undefined) query.set(queryName, String(options.args[cliName]));
  }
  return query;
}

function makeEmitter(enabled) {
  return (stage, extra = {}) => {
    if (enabled) console.log(`@@PROGRESS ${JSON.stringify({ stage, ...extra })}`);
  };
}

function runEncoder(command, commandArgs, cwd, durationMs, emitProgress) {
  let previousPercent = -1;
  return new Promise((resolvePromise, rejectPromise) => {
    const child = spawn(command, commandArgs, { cwd, stdio: ["ignore", "pipe", "pipe"] });
    let stderr = "";
    let progressBuffer = "";
    child.stderr.on("data", (chunk) => {
      stderr += chunk;
      if (stderr.length > 30000) stderr = stderr.slice(-30000);
    });
    child.stdout.on("data", (chunk) => {
      progressBuffer += chunk;
      const lines = progressBuffer.split(/\r?\n/);
      progressBuffer = lines.pop() ?? "";
      for (const line of lines) {
        const equals = line.indexOf("=");
        if (equals < 0) continue;
        const key = line.slice(0, equals);
        const value = line.slice(equals + 1);
        // 优先解析 out_time 的 HH:MM:SS.ffffff 字符串与 out_time_us：
        // out_time_ms 在新旧 ffmpeg 间单位不一致（历史上实为微秒），弃用。
        let encodedMs = NaN;
        if (key === "out_time" && /^\d+:\d+:\d+(\.\d+)?$/.test(value)) {
          const [h, m, s] = value.split(":").map(Number);
          encodedMs = (h * 3600 + m * 60 + s) * 1000;
        } else if (key === "out_time_us") {
          encodedMs = Number(value) / 1000;
        }
        if (!Number.isFinite(encodedMs)) continue;
        const percent = Math.min(99, Math.max(0, Math.round((encodedMs / durationMs) * 100)));
        if (percent === previousPercent) continue;
        previousPercent = percent;
        emitProgress("encode", {
          encodedMs: Math.min(Math.round(encodedMs), Math.round(durationMs)),
          durationMs: Math.round(durationMs),
          percent,
        });
      }
    });
    child.on("error", (error) => rejectPromise(new Error(`ffmpeg 编码无法启动：${error.message}`)));
    child.on("close", (code) => code === 0
      ? resolvePromise()
      : rejectPromise(new Error(`ffmpeg 编码失败（退出码 ${code}）：\n${stderr.slice(-6000)}`)));
  });
}

async function render(options, ffmpegProbe) {
  const emitProgress = makeEmitter(options.progressJson);
  const framesDir = mkdtempSync(join(tmpdir(), `amll-mv-${process.pid}-`));
  activeFramesDir = framesDir;
  keepActiveFrames = options.keepFrames;
  let server;
  let browser;
  let cdp;
  const keepFrames = options.keepFrames;
  const log = (message) => console.log(`[render] ${message}`);
  try {
    const autoEncoders = AUTO_ENCODER_CANDIDATES.filter((candidate) => ffmpegProbe.capabilities.get(candidate)?.available);
    const encoder = chooseEncoder(options.encoder, ffmpegProbe.capabilities);
    if (options.encoder === "auto" && encoder !== "libx264") log(`auto 将依次尝试 ${autoEncoders.join("、")}。`);
    const listenPort = await pickPort(options.port);
    const assetUrls = {
      audio: mountAsset(options.audioPath, "audio"),
      lyric: options.lyricPath ? mountAsset(options.lyricPath, "lyric") : null,
      cover: options.coverPath ? mountAsset(options.coverPath, "cover") : null,
    };
    server = await createServer({
      root: PROJECT_ROOT,
      server: { host: "127.0.0.1", port: listenPort, strictPort: true },
      plugins: [securityHeadersPlugin(), assetServerPlugin()],
      logLevel: "warn",
    });
    await server.listen();
    emitProgress("server", { port: listenPort });

    const browserExecutable = findBrowser(options.runtimeTools.browser);
    log(`ffmpeg：${options.ffmpegPath}（来源：${options.ffmpegSource}）`);
    log(`浏览器：${browserExecutable.path}（来源：${browserExecutable.source}；GPU ${options.browserGpu}）`);
    browser = await chromium.launch({
      executablePath: browserExecutable.path,
      headless: true,
      args: browserLaunchArgs(options.width, options.height, options.browserGpu),
    });
    const context = await browser.newContext({ viewport: { width: options.width, height: options.height }, deviceScaleFactor: 1 });
    const page = await context.newPage();
    page.on("console", (message) => { if (message.type() === "error") log(`页面错误：${message.text()}`); });
    page.on("pageerror", (error) => log(`页面异常：${error.message}`));
    const query = buildPageQuery(options, assetUrls);
    log(`前端 query 已透传 preset=${options.presetName} 与 ${Object.values(FRONTEND_QUERY).length} 个可选契约字段。`);
    await page.goto(`http://127.0.0.1:${listenPort}/?${query}`, { waitUntil: "load", timeout: 120_000 });
    // The app reserves 24 px while fitting previews. Expand/offset its viewport so automated capture is 1:1.
    await page.addStyleTag({ content: ".viewport{flex:none!important;width:calc(100% + 24px)!important;height:calc(100% + 24px)!important;padding:12px!important;transform:translate(-12px,-12px)}" });
    try {
      await page.waitForFunction(({ width, height }) => {
        const bridge = window.__amllMv;
        if (bridge?.error) throw new Error(bridge.error);
        const audio = document.querySelector("audio");
        const stage = document.querySelector("[data-amll-stage]");
        if (!bridge?.ready || !audio || audio.readyState < 2 || !stage) return false;
        const rect = stage.getBoundingClientRect();
        const bridgeRect = bridge.stageRect;
        const dataMatches = Number(stage.getAttribute("data-width")) === width && Number(stage.getAttribute("data-height")) === height;
        const domMatches = Math.abs(rect.width - width) < 0.5 && Math.abs(rect.height - height) < 0.5;
        const bridgeMatches = !bridgeRect || (Math.abs(bridgeRect.width - width) < 0.5 && Math.abs(bridgeRect.height - height) < 0.5);
        return dataMatches && domMatches && bridgeMatches;
      }, { width: options.width, height: options.height }, { timeout: 120_000 });
    } catch (error) {
      const diagnostic = await page.evaluate(() => {
        const bridge = window.__amllMv;
        const audio = document.querySelector("audio");
        const stage = document.querySelector("[data-amll-stage]");
        const rect = stage?.getBoundingClientRect();
        return {
          bridgeReady: bridge?.ready ?? false,
          bridgeError: bridge?.error ?? null,
          readiness: bridge?.readiness ?? null,
          audioReadyState: audio?.readyState ?? null,
          audioError: audio?.error?.message ?? null,
          stageRect: rect ? { width: rect.width, height: rect.height } : null,
          dataWidth: stage?.getAttribute("data-width") ?? null,
          dataHeight: stage?.getAttribute("data-height") ?? null,
        };
      });
      throw new Error(`${error.message}；前端状态：${JSON.stringify(diagnostic)}`);
    }
    const meta = await page.evaluate(() => {
      const audio = document.querySelector("audio");
      const rect = document.querySelector("[data-amll-stage]")?.getBoundingClientRect();
      return { durationMs: (audio?.duration ?? 0) * 1000, stageRect: rect ? { width: rect.width, height: rect.height } : null, lyricLines: document.querySelectorAll(".FmKaba_lyricLineWrapper").length };
    });
    if (!Number.isFinite(meta.durationMs) || meta.durationMs <= 0) throw new Error("无法读取有效音频时长。");
    const sourceDurationMs = options.durationLimitMs === null ? meta.durationMs : Math.min(meta.durationMs, options.durationLimitMs);
    const expectedDurationMs = sourceDurationMs + options.tailMs;
    emitProgress("ready", { durationMs: Math.round(expectedDurationMs), sourceDurationMs: Math.round(sourceDurationMs), lyricLines: meta.lyricLines });
    log(`舞台就绪 ${meta.stageRect.width}×${meta.stageRect.height}，计划时长 ${(expectedDurationMs / 1000).toFixed(3)}s。`);

    cdp = await context.newCDPSession(page);
    const captured = [];
    let frameIndex = 0;
    let acceptingFrames = false;
    let frameWriteError = null;
    let writeChain = Promise.resolve();
    cdp.on("Page.screencastFrame", (frame) => {
      const receivedAtMs = performance.timeOrigin + performance.now();
      const ack = () => { void cdp.send("Page.screencastFrameAck", { sessionId: frame.sessionId }).catch(() => undefined); };
      if (!acceptingFrames) {
        ack();
        return;
      }
      const index = frameIndex++;
      // 写完再 ack：Chromium screencast 借未 ack 的帧自然降速，慢盘下不会无限积压内存
      writeChain = writeChain.then(() => {
        try {
          writeFileSync(join(framesDir, `f${String(index).padStart(8, "0")}.jpg`), Buffer.from(frame.data, "base64"));
          captured.push({ index, timestampMs: Number(frame.metadata?.timestamp) * 1000, receivedAtMs });
        } catch (error) {
          if (!frameWriteError) frameWriteError = error;
        }
      }).finally(ack);
    });
    await cdp.send("Page.startScreencast", { format: "jpeg", quality: 95, maxWidth: options.width, maxHeight: options.height, everyNthFrame: 1 });
    acceptingFrames = true;
    const captureStartedAtMs = performance.now();
    const audioStartedEpochMs = await page.evaluate(async () => {
      const audio = document.querySelector("audio");
      if (!audio) throw new Error("音频元素不存在。");
      const started = new Promise((resolveStarted) => {
        audio.addEventListener("playing", () => resolveStarted(performance.timeOrigin + performance.now()), { once: true });
      });
      audio.currentTime = 0;
      await audio.play();
      const startedAt = await started;
      if (window.__amllMv) window.__amllMv.audioStartedAt = startedAt;
      return startedAt;
    });
    const captureDeadlineMs = sourceDurationMs + options.tailMs;
    const progressTimer = setInterval(() => {
      const elapsedMs = Math.min(performance.now() - captureStartedAtMs, captureDeadlineMs);
      // 取整为整数毫秒：GUI 侧按 JSON 整数解析，浮点会导致已耗时恒显示 0
      emitProgress("capture", { frames: frameIndex, elapsedMs: Math.round(elapsedMs), durationMs: Math.round(captureDeadlineMs), percent: Math.min(99, Math.round((elapsedMs / captureDeadlineMs) * 100)) });
    }, 500);
    try {
      if (options.durationLimitMs !== null && sourceDurationMs < meta.durationMs) {
        await page.waitForTimeout(sourceDurationMs);
        await page.evaluate(() => { const audio = document.querySelector("audio"); if (audio) audio.pause(); });
      } else {
        try {
          await page.waitForFunction(() => window.__amllMv?.audioEndedAt != null, null, { timeout: sourceDurationMs + 60_000 });
        } catch (error) {
          const audioState = await page.evaluate(() => {
            const audio = document.querySelector("audio");
            return audio
              ? { paused: audio.paused, ended: audio.ended, currentTime: audio.currentTime, duration: audio.duration, error: audio.error?.message ?? null }
              : null;
          }).catch(() => null);
          throw new Error(`等待音频结束超时：${error.message}；音频状态：${JSON.stringify(audioState)}`);
        }
      }
      if (options.tailMs > 0) await page.waitForTimeout(options.tailMs);
    } finally {
      clearInterval(progressTimer);
    }
    acceptingFrames = false;
    await cdp.send("Page.stopScreencast");
    await writeChain;
    if (frameWriteError) throw frameWriteError;
    if (captured.length === 0) throw new Error("未捕获到有效画面帧。");

    const normalizedCaptured = normalizeCapturedFrames(captured);
    // CDP ScreencastFrame 的 metadata.timestamp 是 Unix epoch 秒（Chrome 96+），
    // 与页面 performance.timeOrigin+performance.now() 同属墙钟域，可直接相减。
    // Node 收帧时刻含 IPC 延迟，用它会把音画偏移低估一个传输延迟。
    // 若旧内核缺少 CDP 时间戳，normalizeCapturedFrames 会回退到收帧时刻，此时偏差退化为旧行为。
    const firstCaptureMs = normalizedCaptured[0].timestampMs;
    let audioOffsetMs = audioStartedEpochMs - firstCaptureMs;
    if (!Number.isFinite(audioOffsetMs) || Math.abs(audioOffsetMs) > 3000) audioOffsetMs = 0;
    assertCopyTimingCompatible({
      audioCodec: options.audioCodec,
      durationLimitMs: options.durationLimitMs,
      tailMs: options.tailMs,
      audioOffsetMs,
    });
    const outputDurationMs = Math.max(1, expectedDurationMs + audioOffsetMs);
    const timeline = buildCfrTimeline(normalizedCaptured, options.fps, outputDurationMs);
    writeFileSync(join(framesDir, "frames.txt"), formatConcatTimeline(timeline.selectedIndices, options.fps), "utf8");
    log(`捕获 ${captured.length} 帧，CFR ${timeline.totalFrames} 帧，音频相对首帧 ${audioOffsetMs.toFixed(1)}ms。`);

    const outputPath = options.outputPath;
    mkdirSync(dirname(outputPath), { recursive: true });
    const encodeWith = async (selectedEncoder) => {
      const encoderPreset = resolveEncoderPreset(
        selectedEncoder,
        options.encoder === "auto" ? null : options.encoderPreset,
        ffmpegProbe.presets.get(selectedEncoder),
      );
      const ffmpegArgs = buildFfmpegArgs({
        audioPath: options.audioPath, outputPath, fps: options.fps, durationMs: timeline.durationMs,
        audioOffsetMs, encoder: selectedEncoder, encoderPreset,
        quality: options.quality, audioCodec: options.audioCodec, audioBitrate: options.audioBitrate,
        ffmpegMajorVersion: ffmpegProbe.majorVersion,
      });
      const softwareThreadNote = selectedEncoder === "libx264" || selectedEncoder === "libx265"
        ? "，threads 0（自动多核）"
        : "";
      log(`编码器 ${selectedEncoder}，preset ${encoderPreset}，质量 ${options.quality}${softwareThreadNote}，临时帧 ${framesDir}`);
      await runEncoder(options.ffmpegPath, ffmpegArgs, framesDir, timeline.durationMs, emitProgress);
      return selectedEncoder;
    };
    let usedEncoder = encoder;
    if (options.encoder === "auto") {
      let lastError;
      for (const candidate of autoEncoders) {
        try {
          usedEncoder = await encodeWith(candidate);
          lastError = null;
          break;
        } catch (error) {
          lastError = error;
          // 输出路径若是目录（如 --out 指向 xxx.mp4 目录），rmSync 会抛 EISDIR 掩盖真正的编码错误
          try {
            if (statSync(outputPath, { throwIfNoEntry: false })?.isFile()) rmSync(outputPath, { force: true });
          } catch { /* 保留原始编码错误 */ }
          log(`${candidate} 编码失败${candidate === "libx264" ? "" : "，继续回退"}：${error.message.split("\n", 1)[0]}`);
        }
      }
      if (lastError) throw lastError;
    } else {
      try {
        await encodeWith(encoder);
      } catch (error) {
        throw new Error(`显式编码器 ${encoder} 正式编码失败；不会静默改用其他 codec family：${error.message}`);
      }
    }
    const bytes = statSync(outputPath).size;
    emitProgress("done", { percent: 100, durationMs: Math.round(timeline.durationMs), outPath: outputPath, bytes, width: options.width, height: options.height, fps: options.fps, frames: timeline.totalFrames, encoder: usedEncoder });
    log(`完成：${outputPath}（${(bytes / 1024 / 1024).toFixed(1)} MiB）`);
  } catch (error) {
    if (keepFrames) log(`失败诊断帧保留在 ${framesDir}`);
    throw error;
  } finally {
    try { await cdp?.detach(); } catch { /* cleanup */ }
    try { await browser?.close(); } catch { /* cleanup */ }
    try { await server?.close(); } catch { /* cleanup */ }
    if (!keepFrames) rmSync(framesDir, { recursive: true, force: true });
    activeFramesDir = null;
  }
}

function sanitize(name) {
  return name.replace(/[<>:"/\\|?*\u0000-\u001f]/g, "_").slice(0, 120) || "mv";
}

/* ---------- 临时帧目录生命周期 ---------- */
// GUI 取消渲染走 Kill(entireProcessTree)，Node 侧 finally 不会执行；
// 进程退出钩子覆盖可捕获的退出路径，陈旧目录清扫兜底硬杀路径。
let activeFramesDir = null;
let keepActiveFrames = false;

function cleanupActiveFrames() {
  if (activeFramesDir && !keepActiveFrames) {
    try { rmSync(activeFramesDir, { recursive: true, force: true }); } catch { /* 尽力清理 */ }
  }
}

function sweepStaleFrameDirs(maxAgeMs = 24 * 60 * 60 * 1000) {
  try {
    for (const entry of readdirSync(tmpdir())) {
      if (!/^amll-mv-\d+-/.test(entry)) continue;
      const dir = join(tmpdir(), entry);
      try {
        if (Date.now() - statSync(dir).mtimeMs > maxAgeMs) rmSync(dir, { recursive: true, force: true });
      } catch { /* 并发渲染或权限问题：跳过 */ }
    }
  } catch { /* tmpdir 不可读：跳过 */ }
}

async function main() {
  let options;
  try {
    options = parseOptions(process.argv.slice(2));
  } catch (error) {
    console.error(`参数错误：${error.message}\n用 --help 查看完整用法。`);
    process.exitCode = 2;
    return;
  }
  if (options.action === "help") {
    process.stdout.write(HELP);
    return;
  }
  process.on("exit", cleanupActiveFrames);
  for (const signal of ["SIGINT", "SIGTERM"]) {
    process.on(signal, () => process.exit(signal === "SIGINT" ? 130 : 143));
  }
  sweepStaleFrameDirs();
  console.error(`[render] ffmpeg：${options.ffmpegPath}（来源：${options.ffmpegSource}）`);
  try {
    const probe = await probeFfmpeg(options.ffmpegPath);
    if (options.action === "list-encoders") {
      console.log(JSON.stringify(encoderReport(probe, options.ffmpegPath, options.ffmpegSource)));
      return;
    }
    await render(options, probe);
  } catch (error) {
    const message = String(error?.message ?? error);
    console.error(`渲染失败：${message}`);
    if (options?.progressJson) console.log(`@@ERROR ${JSON.stringify({ message })}`);
    process.exitCode = 1;
  }
}

if (process.argv[1] && resolve(process.argv[1]) === SCRIPT_PATH) await main();
