import { existsSync, readdirSync } from "node:fs";
import { basename, dirname, extname, join, resolve } from "node:path";

export const CANONICAL_RENDER_OPTIONS = Object.freeze({
  secondaryMode: "both",
  lyricWidth: 54,
  lyricFade: 0.08,
  lyricOffset: 0,
  backgroundSaturation: 1.2,
  browserGpu: "auto",
});

export function normalizeCanonicalOptions(input = {}) {
  const normalized = {
    secondaryMode: String(input.secondaryMode ?? CANONICAL_RENDER_OPTIONS.secondaryMode).toLowerCase(),
    lyricWidth: Number(input.lyricWidth ?? CANONICAL_RENDER_OPTIONS.lyricWidth),
    lyricFade: Number(input.lyricFade ?? CANONICAL_RENDER_OPTIONS.lyricFade),
    lyricOffset: Number(input.lyricOffset ?? CANONICAL_RENDER_OPTIONS.lyricOffset),
    backgroundSaturation: Number(input.backgroundSaturation ?? CANONICAL_RENDER_OPTIONS.backgroundSaturation),
    browserGpu: String(input.browserGpu ?? CANONICAL_RENDER_OPTIONS.browserGpu).toLowerCase(),
  };
  if (!["none", "translation", "roman", "both"].includes(normalized.secondaryMode)) {
    throw new Error("secondaryMode 必须是 none / translation / roman / both。");
  }
  for (const [name, min, max] of [["lyricWidth", 30, 80], ["lyricFade", 0, 0.3], ["lyricOffset", -600, 600], ["backgroundSaturation", 0, 2]]) {
    const value = normalized[name];
    if (!Number.isFinite(value) || value < min || value > max) {
      throw new Error(`${name} 必须是 ${min}..${max} 的有限数值。`);
    }
  }
  if (!["auto", "on", "off"].includes(normalized.browserGpu)) {
    throw new Error("browserGpu 必须是 auto / on / off。");
  }
  return normalized;
}

export const VIDEO_ENCODERS = Object.freeze([
  "libx264",
  "libx265",
  "h264_nvenc",
  "hevc_nvenc",
  "h264_qsv",
  "hevc_qsv",
  "h264_amf",
  "hevc_amf",
]);

const AUTO_ENCODER_ORDER = Object.freeze([
  "h264_nvenc",
  "h264_qsv",
  "h264_amf",
  "libx264",
]);

export const AUTO_ENCODER_CANDIDATES = AUTO_ENCODER_ORDER;

export const ENCODER_PRESETS = Object.freeze({
  libx264: Object.freeze(["ultrafast", "superfast", "veryfast", "faster", "fast", "medium", "slow", "slower", "veryslow", "placebo"]),
  libx265: Object.freeze(["ultrafast", "superfast", "veryfast", "faster", "fast", "medium", "slow", "slower", "veryslow", "placebo"]),
  h264_nvenc: Object.freeze(["p1", "p2", "p3", "p4", "p5", "p6", "p7", "default", "slow", "medium", "fast", "hp", "hq", "bd", "ll", "llhq", "llhp", "lossless", "losslesshp"]),
  hevc_nvenc: Object.freeze(["p1", "p2", "p3", "p4", "p5", "p6", "p7", "default", "slow", "medium", "fast", "hp", "hq", "bd", "ll", "llhq", "llhp", "lossless", "losslesshp"]),
  h264_qsv: Object.freeze(["veryfast", "faster", "fast", "medium", "slow", "slower", "veryslow"]),
  hevc_qsv: Object.freeze(["veryfast", "faster", "fast", "medium", "slow", "slower", "veryslow"]),
  h264_amf: Object.freeze(["speed", "balanced", "quality"]),
  hevc_amf: Object.freeze(["speed", "balanced", "quality"]),
});

const ENCODER_DEFAULT_PRESETS = Object.freeze({
  libx264: "medium", libx265: "medium",
  h264_nvenc: "p4", hevc_nvenc: "p4",
  h264_qsv: "medium", hevc_qsv: "medium",
  h264_amf: "balanced", hevc_amf: "balanced",
});

export function resolveEncoderPreset(encoder, requested, supportedPresets) {
  const valid = ENCODER_PRESETS[encoder];
  if (!valid) throw new Error(`未知视频编码器：${encoder}`);
  const hasProbe = supportedPresets instanceof Set && supportedPresets.size > 0;
  const detected = hasProbe ? valid.filter((preset) => supportedPresets.has(preset)) : valid;
  const defaultPreset = ENCODER_DEFAULT_PRESETS[encoder];
  const preset = requested ?? (detected.includes(defaultPreset)
    ? defaultPreset
    : encoder.endsWith("_nvenc") && detected.includes("medium")
      ? "medium"
      : detected[0]);
  if (!preset || !detected.includes(preset)) {
    const suffix = hasProbe ? "（按当前 ffmpeg 能力探测）" : "";
    throw new Error(`${encoder} 的 --encoder-preset 必须是：${detected.join(" / ")}${suffix}。`);
  }
  return preset;
}

export function parseEncoderPresetHelp(encoder, output) {
  const known = ENCODER_PRESETS[encoder];
  if (!known) throw new Error(`未知视频编码器：${encoder}`);
  const text = String(output).toLowerCase();
  const words = new Set(text.match(/[a-z0-9_]+/g) ?? []);
  const found = known.filter((preset) => words.has(preset));
  if (encoder === "libx264" || encoder === "libx265") {
    return /set the (?:encoding|x26[45]) preset/i.test(text) ? new Set(known) : new Set(found);
  }
  return new Set(found);
}

export function assertCopyTimingCompatible({ audioCodec, durationLimitMs = null, headMs = 0, tailMs = 0, audioOffsetMs = 0 }) {
  if (audioCodec !== "copy") return;
  const conflicts = new Set();
  if (durationLimitMs !== null) conflicts.add("--duration");
  if (headMs !== 0) conflicts.add("--head");
  if (tailMs !== 0) conflicts.add("--tail");
  // --head 本身就是音频相对视频的已知偏移，不再重复报告为通用 offset。
  if (Math.abs(audioOffsetMs - headMs) > 0.0005) conflicts.add("额外音频/画面 offset");
  if (conflicts.size > 0) {
    throw new Error(`--audio-codec copy 无法可靠处理 ${[...conflicts].join("、")}；请使用 aac/flac，或移除这些时序变换。`);
  }
}

export function detectInstallLayout(scriptPath) {
  const scriptsDir = dirname(resolve(scriptPath));
  if (basename(scriptsDir).toLowerCase() !== "scripts") return null;
  const contentDir = dirname(scriptsDir);
  if (basename(contentDir).toLowerCase() !== "content") return null;
  const runtimeDir = dirname(contentDir);
  if (basename(runtimeDir).toLowerCase() !== "runtime") return null;
  return { appDir: dirname(runtimeDir), runtimeDir };
}

export function findExecutableRecursive(root, names, maxDepth = 5) {
  if (!root || !existsSync(root)) return null;
  const wanted = new Set(names.map((name) => name.toLowerCase()));
  const queue = [{ path: root, depth: 0 }];
  while (queue.length > 0) {
    const current = queue.shift();
    let entries;
    try {
      entries = readdirSync(current.path, { withFileTypes: true })
        .sort((a, b) => a.name.localeCompare(b.name));
    } catch { continue; }
    for (const entry of entries) {
      if (entry.isFile() && wanted.has(entry.name.toLowerCase())) return join(current.path, entry.name);
    }
    if (current.depth >= maxDepth) continue;
    for (const entry of entries) {
      if (entry.isDirectory() && !entry.isSymbolicLink()) queue.push({ path: join(current.path, entry.name), depth: current.depth + 1 });
    }
  }
  return null;
}

export function discoverBundledRuntime(scriptPath, maxBrowserDepth = 5) {
  const layout = detectInstallLayout(scriptPath);
  if (!layout) return { layout: null, ffmpegPath: null, browserPath: null };
  const ffmpegPath = join(layout.runtimeDir, "ffmpeg", process.platform === "win32" ? "ffmpeg.exe" : "ffmpeg");
  return {
    layout,
    ffmpegPath: existsSync(ffmpegPath) ? ffmpegPath : null,
    browserPath: findExecutableRecursive(join(layout.runtimeDir, "browsers"), ["chrome.exe", "msedge.exe"], maxBrowserDepth),
  };
}

export function discoverDevelopmentFfmpeg(scriptPath) {
  const scriptsDir = dirname(resolve(scriptPath));
  if (basename(scriptsDir).toLowerCase() !== "scripts") return null;
  const projectRoot = dirname(scriptsDir);
  const ffmpegPath = join(projectRoot, "installer", "staging", "runtime", "ffmpeg", process.platform === "win32" ? "ffmpeg.exe" : "ffmpeg");
  return existsSync(ffmpegPath) ? ffmpegPath : null;
}

export function resolveRuntimeTools({ scriptPath, explicitFfmpeg, explicitBrowser, env = process.env }) {
  const bundled = discoverBundledRuntime(scriptPath);
  const developmentFfmpeg = discoverDevelopmentFfmpeg(scriptPath);
  return {
    ffmpeg: explicitFfmpeg
      ? { path: resolve(String(explicitFfmpeg)), source: "--ffmpeg" }
      : bundled.ffmpegPath
        ? { path: bundled.ffmpegPath, source: "安装布局 runtime/ffmpeg" }
        : developmentFfmpeg
          ? { path: developmentFfmpeg, source: "开发布局 installer/staging/runtime/ffmpeg" }
          : env.FFMPEG_PATH
            ? { path: String(env.FFMPEG_PATH), source: "FFMPEG_PATH" }
            : null,
    browser: explicitBrowser
      ? { path: resolve(String(explicitBrowser)), source: "--browser" }
      : bundled.browserPath
        ? { path: bundled.browserPath, source: "安装布局 runtime/browsers" }
        : null,
    layout: bundled.layout,
  };
}

export function parseEncoderListing(output) {
  const found = new Set();
  for (const line of String(output).split(/\r?\n/)) {
    // FFmpeg 8 may mark direct-rendering-capable encoders as V....D.
    const match = line.match(/^\s*V[A-Z.]{5}\s+(\S+)/i);
    if (match && VIDEO_ENCODERS.includes(match[1])) found.add(match[1]);
  }
  return found;
}

export function chooseEncoder(requested, capabilities) {
  const capabilityMap = capabilities instanceof Map ? capabilities : null;
  const supported = capabilityMap
    ? new Set([...capabilityMap].filter(([, value]) => value?.available).map(([name]) => name))
    : capabilities instanceof Set ? capabilities : new Set(capabilities);
  if (requested !== "auto") {
    if (!supported.has(requested)) {
      const capability = capabilityMap?.get(requested);
      const detail = capability?.failureReason ? `：${capability.failureReason}` : "。";
      throw new Error(`编码器 ${requested} 未通过真实试编码，当前不可用${detail} 请用 --list-encoders 查看探测结果。`);
    }
    return requested;
  }
  if (!supported.has("libx264")) {
    throw new Error("自动编码要求可用的 libx264 CPU 回退，但 libx264 未通过真实试编码。");
  }
  return AUTO_ENCODER_ORDER.find((name) => supported.has(name));
}

export function resolveRenderTiming({ audioDurationMs, durationLimitMs = null, headMs = 0, tailMs = 0 }) {
  if (!Number.isFinite(audioDurationMs) || audioDurationMs <= 0) {
    throw new Error("audioDurationMs 必须是有限正数。");
  }
  const sourceDurationMs = durationLimitMs === null
    ? audioDurationMs
    : Math.min(audioDurationMs, durationLimitMs);
  return {
    sourceDurationMs,
    totalDurationMs: headMs + sourceDurationMs + tailMs,
    audioOffsetMs: headMs,
  };
}

export function normalizeCapturedFrames(captured) {
  if (!Array.isArray(captured) || captured.length === 0) {
    throw new Error("没有可用于构造时间轴的捕获帧。");
  }
  let previous = -Infinity;
  let previousReceived = NaN;
  return captured.map((frame, position) => {
    const raw = Number(frame?.timestampMs ?? frame?.ts);
    const received = Number(frame?.receivedAtMs);
    let timestampMs = raw;
    if (!Number.isFinite(timestampMs) || timestampMs <= previous) {
      const receiveDelta = Number.isFinite(received) && Number.isFinite(previousReceived)
        ? received - previousReceived
        : NaN;
      timestampMs = previous === -Infinity
        ? (Number.isFinite(received) ? received : 0)
        : previous + (Number.isFinite(receiveDelta) && receiveDelta > 0 ? receiveDelta : 0.001);
    }
    previous = timestampMs;
    previousReceived = received;
    return {
      index: Number.isInteger(frame?.index) ? frame.index : position,
      timestampMs,
      receivedAtMs: Number.isFinite(received) ? received : timestampMs,
    };
  });
}

export function holdFirstFrameUntil(captured, holdUntilTimestampMs) {
  if (!Array.isArray(captured) || captured.length === 0) {
    throw new Error("片头首帧保持需要至少一个捕获帧。");
  }
  if (!Number.isFinite(holdUntilTimestampMs)) {
    throw new Error("holdUntilTimestampMs 必须是有限数值。");
  }
  const first = captured[0];
  return [first, ...captured.slice(1).filter((frame) => {
    const timestampMs = Number(frame?.timestampMs ?? frame?.ts);
    return Number.isFinite(timestampMs) && timestampMs >= holdUntilTimestampMs;
  })];
}

export function buildCfrTimeline(captured, fps, durationMs, options = {}) {
  if (!Number.isFinite(fps) || fps <= 0 || !Number.isFinite(durationMs) || durationMs <= 0) {
    throw new Error("fps 与 durationMs 必须是有限正数。");
  }
  const frames = normalizeCapturedFrames(captured);
  const firstTimestampMs = frames[0].timestampMs;
  const timelineStartTimestampMs = options.startTimestampMs === undefined
    ? firstTimestampMs
    : Number(options.startTimestampMs);
  if (!Number.isFinite(timelineStartTimestampMs)) {
    throw new Error("startTimestampMs 必须是有限数值。");
  }
  const totalFrames = Math.max(1, Math.ceil((durationMs * fps) / 1000));
  const selectedIndices = [];
  let sourceCursor = 0;
  for (let targetIndex = 0; targetIndex < totalFrames; targetIndex++) {
    const targetTimestampMs = timelineStartTimestampMs + (targetIndex * 1000) / fps;
    while (
      sourceCursor + 1 < frames.length &&
      frames[sourceCursor + 1].timestampMs <= targetTimestampMs
    ) {
      sourceCursor++;
    }
    selectedIndices.push(frames[sourceCursor].index);
  }
  return {
    frames,
    firstTimestampMs,
    timelineStartTimestampMs,
    totalFrames,
    durationMs: (totalFrames * 1000) / fps,
    selectedIndices,
  };
}

export function formatConcatTimeline(selectedIndices, fps, frameName = defaultFrameName) {
  if (!Array.isArray(selectedIndices) || selectedIndices.length === 0) {
    throw new Error("CFR 时间轴不能为空。");
  }
  const secondsPerFrame = 1 / fps;
  const lines = [];
  for (const index of selectedIndices) {
    lines.push(`file '${frameName(index)}'`, `duration ${secondsPerFrame.toFixed(9)}`);
  }
  lines.push(`file '${frameName(selectedIndices[selectedIndices.length - 1])}'`);
  return `${lines.join("\n")}\n`;
}

function defaultFrameName(index) {
  return `f${String(index).padStart(8, "0")}.jpg`;
}

export function getFfmpegCfrArgs(majorVersion) {
  return Number.isFinite(majorVersion) && majorVersion >= 5
    ? ["-fps_mode", "cfr"]
    : ["-vsync", "cfr"];
}

export function parseFfmpegMajorVersion(output) {
  const text = String(output);
  const release = text.match(/ffmpeg version\s+(\d+)/i);
  if (release) return Number(release[1]);
  return /ffmpeg version\s+N-/i.test(text) ? 4 : null;
}

export function getEncoderQualityArgs(encoder, quality, preset, supportedPresets) {
  const resolvedPreset = resolveEncoderPreset(encoder, preset, supportedPresets);
  const q = String(quality);
  if (encoder === "libx264" || encoder === "libx265") {
    return ["-preset", resolvedPreset, "-crf", q, "-threads", "0"];
  }
  if (encoder.endsWith("_nvenc")) {
    return ["-preset", resolvedPreset, "-rc", "vbr", "-cq", q, "-b:v", "0"];
  }
  if (encoder.endsWith("_qsv")) {
    return ["-preset", resolvedPreset, "-global_quality", q];
  }
  if (encoder.endsWith("_amf")) {
    return ["-quality", resolvedPreset, "-rc", "cqp", "-qp_i", q, "-qp_p", q];
  }
  throw new Error(`未知视频编码器：${encoder}`);
}

export function buildEncoderVideoArgs({ encoder, quality, encoderPreset, supportedPresets }) {
  return [
    "-c:v", encoder,
    ...getEncoderQualityArgs(encoder, quality, encoderPreset, supportedPresets),
    "-pix_fmt", "yuv420p",
  ];
}

export function buildEncoderProbeArgs({ encoder, quality = 28, encoderPreset, supportedPresets }) {
  return [
    "-hide_banner", "-loglevel", "error", "-nostdin",
    "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=10:duration=0.5",
    "-an", ...buildEncoderVideoArgs({ encoder, quality, encoderPreset, supportedPresets }),
    "-frames:v", "5", "-f", "null", "-",
  ];
}

export function validateContainer(outputPath, encoder, audioCodec) {
  const extension = extname(outputPath).toLowerCase();
  if (![".mp4", ".mkv", ".mov"].includes(extension)) {
    throw new Error("输出容器仅支持 .mp4、.mov 或 .mkv；缺省为 .mp4。");
  }
  if (extension === ".mp4" && audioCodec === "flac") {
    throw new Error("MP4 不支持本管线的 FLAC 音频输出；请改用 .mkv/.mov 或 --audio-codec aac。");
  }
  if (audioCodec === "copy" && extension !== ".mkv") {
    throw new Error("--audio-codec copy 仅支持 MKV 输出；请改用 .mkv，或使用 aac/flac。");
  }
  if (encoder.startsWith("hevc_") || encoder === "libx265") return "hevc";
  return "h264";
}

export function buildFfmpegArgs(options) {
  const {
    audioPath,
    outputPath,
    fps,
    durationMs,
    audioOffsetMs = 0,
    encoder,
    encoderPreset,
    quality,
    audioCodec,
    audioBitrate,
    ffmpegMajorVersion,
  } = options;
  assertCopyTimingCompatible({
    audioCodec,
    durationLimitMs: options.durationLimitMs ?? null,
    headMs: options.headMs ?? 0,
    tailMs: options.tailMs ?? 0,
    audioOffsetMs,
  });
  validateContainer(outputPath, encoder, audioCodec);
  const args = [
    "-y",
    "-f", "concat",
    "-safe", "0",
    "-i", "frames.txt",
  ];
  if (audioOffsetMs > 0) args.push("-itsoffset", (audioOffsetMs / 1000).toFixed(6));
  args.push("-i", audioPath);
  if (audioOffsetMs < 0) {
    args.push("-af", `atrim=start=${(-audioOffsetMs / 1000).toFixed(6)},asetpts=PTS-STARTPTS`);
  }
  args.push(
    "-map", "0:v:0",
    "-map", "1:a:0",
    ...buildEncoderVideoArgs({ encoder, quality, encoderPreset }),
    "-r", String(fps),
    ...getFfmpegCfrArgs(ffmpegMajorVersion),
    "-c:a", audioCodec,
  );
  if (audioCodec === "aac" && audioBitrate) args.push("-b:a", audioBitrate);
  args.push("-t", (durationMs / 1000).toFixed(6));
  if ([".mp4", ".mov"].includes(extname(outputPath).toLowerCase())) {
    args.push("-movflags", "+faststart");
  }
  args.push("-progress", "pipe:1", "-nostats", outputPath);
  return args;
}
