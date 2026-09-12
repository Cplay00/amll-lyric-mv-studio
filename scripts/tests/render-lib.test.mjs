import assert from "node:assert/strict";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import {
  AUTO_ENCODER_CANDIDATES,
  CANONICAL_RENDER_OPTIONS,
  ENCODER_PRESETS,
  VIDEO_ENCODERS,
  assertCopyTimingCompatible,
  buildCfrTimeline,
  buildEncoderProbeArgs,
  buildFfmpegArgs,
  chooseEncoder,
  detectInstallLayout,
  discoverBundledRuntime,
  discoverDevelopmentFfmpeg,
  formatConcatTimeline,
  getEncoderQualityArgs,
  getEncoderRateControlCandidates,
  getFfmpegCfrArgs,
  holdFirstFrameUntil,
  normalizeCanonicalOptions,
  normalizeCapturedFrames,
  parseEncoderListing,
  parseEncoderPresetHelp,
  parseFfmpegMajorVersion,
  resolveEncoderPreset,
  resolveRenderTiming,
  resolveRuntimeTools,
} from "../render-lib.mjs";
import { parseArgs, parseOptions, probeEncoder } from "../render-mv.mjs";

assert.deepEqual(parseArgs(["--audio", "song.wav", "--head=1250"]), { audio: "song.wav", head: "1250" });
assert.throws(() => parseArgs(["--head"]), /--head 缺少值/);
assert.throws(() => parseArgs(["--head", "1", "--head", "2"]), /参数重复：--head/);
const parseRoot = mkdtempSync(join(tmpdir(), "amll-options-test-"));
try {
  const audioPath = join(parseRoot, "song.wav");
  writeFileSync(audioPath, "test");
  const options = parseOptions(["--audio", audioPath, "--tail", "0"]);
  assert.equal(options.headMs, 0);
  assert.match(options.outputPath, /song-1920x1080-60fps\.mp4$/);
  assert.throws(() => parseOptions(["--audio", audioPath, "--tail", "0", "--audio-codec", "copy"]), /copy 仅支持 MKV/);
  assert.doesNotThrow(() => parseOptions(["--audio", audioPath, "--tail", "0", "--audio-codec", "copy", "--out", join(parseRoot, "copy.mkv")]));
  assert.equal(parseOptions(["--audio", audioPath, "--head", "600000", "--tail", "0"]).headMs, 600000);
  assert.throws(() => parseOptions(["--audio", audioPath, "--head", "-1"]), /--head 必须是 0\.\.600000/);
  assert.throws(() => parseOptions(["--audio", audioPath, "--head", "600001"]), /--head 必须是 0\.\.600000/);
} finally {
  rmSync(parseRoot, { recursive: true, force: true });
}

const timing = resolveRenderTiming({ audioDurationMs: 5000, durationLimitMs: 3200, headMs: 750, tailMs: 1250 });
assert.deepEqual(timing, { sourceDurationMs: 3200, totalDurationMs: 5200, audioOffsetMs: 750 });
assert.deepEqual(resolveRenderTiming({ audioDurationMs: 5000 }), { sourceDurationMs: 5000, totalDurationMs: 5000, audioOffsetMs: 0 });
assert.throws(() => resolveRenderTiming({ audioDurationMs: 0 }), /有限正数/);

const normalized = normalizeCapturedFrames([
  { index: 0, timestampMs: 1000, receivedAtMs: 2000 },
  { index: 1, timestampMs: undefined, receivedAtMs: 2010 },
  { index: 2, timestampMs: 1005, receivedAtMs: 2020 },
]);
assert.deepEqual(normalized.map((frame) => frame.timestampMs), [1000, 1010, 1020]);
assert.deepEqual(normalized.map((frame) => frame.receivedAtMs), [2000, 2010, 2020]);

const timeline = buildCfrTimeline([
  { index: 0, timestampMs: 1000 },
  { index: 1, timestampMs: 1070 },
  { index: 2, timestampMs: 1250 },
], 10, 500);
assert.equal(timeline.totalFrames, 5);
assert.equal(timeline.timelineStartTimestampMs, 1000);
assert.deepEqual(timeline.selectedIndices, [0, 1, 1, 2, 2]);
const plannedTimeline = buildCfrTimeline([
  { index: 0, timestampMs: 1025 },
  { index: 1, timestampMs: 1070 },
  { index: 2, timestampMs: 1250 },
], 10, 400, { startTimestampMs: 1000 });
assert.equal(plannedTimeline.timelineStartTimestampMs, 1000);
assert.deepEqual(plannedTimeline.selectedIndices, [0, 1, 1, 2]);
assert.throws(
  () => buildCfrTimeline([{ index: 0, timestampMs: 1000 }], 10, 100, { startTimestampMs: NaN }),
  /startTimestampMs 必须是有限数值/,
);
const heldFrames = holdFirstFrameUntil([
  { index: 0, timestampMs: 1000 },
  { index: 1, timestampMs: 1100 },
  { index: 2, timestampMs: 1499 },
  { index: 3, timestampMs: 1500 },
  { index: 4, timestampMs: 1600 },
], 1500);
assert.deepEqual(heldFrames.map((frame) => frame.index), [0, 3, 4]);
const heldTimeline = buildCfrTimeline(heldFrames, 10, 800, { startTimestampMs: 1000 });
assert.deepEqual(heldTimeline.selectedIndices.slice(0, 5), [0, 0, 0, 0, 0],
  "the entire 500 ms head must repeat the readiness baseline frame");
assert.equal(heldTimeline.selectedIndices[5], 3, "the first post-head frame must begin at audio start");
assert.throws(() => holdFirstFrameUntil([], 1500), /至少一个捕获帧/);
assert.match(formatConcatTimeline(timeline.selectedIndices, 10), /file 'f00000001\.jpg'\nduration 0\.100000000/);
assert.equal(formatConcatTimeline(timeline.selectedIndices, 10).trim().split("\n").at(-1), "file 'f00000002.jpg'");

const encoders = parseEncoderListing([
  " V..... libx264 libx264 H.264",
  " V....D h264_nvenc NVIDIA NVENC",
  " A..... aac AAC",
].join("\n"));
assert.deepEqual([...encoders], ["libx264", "h264_nvenc"]);
assert.equal(chooseEncoder("auto", new Map([
  ["h264_nvenc", { available: true }],
  ["libx264", { available: true }],
])), "h264_nvenc");
assert.equal(chooseEncoder("auto", new Map([
  ["h264_nvenc", { available: false, failureReason: "driver missing" }],
  ["libx264", { available: true }],
])), "libx264");
assert.equal(chooseEncoder("libx264", encoders), "libx264");
assert.throws(() => chooseEncoder("auto", new Map([["h264_nvenc", { available: true }]])), /libx264 CPU 回退/);
assert.throws(
  () => chooseEncoder("h264_nvenc", new Map([["h264_nvenc", { available: false, failureReason: "旧版 preset 试编码失败" }]])),
  /旧版 preset 试编码失败/,
);
assert.throws(() => chooseEncoder("hevc_qsv", encoders), /真实试编码/);
assert.deepEqual(AUTO_ENCODER_CANDIDATES, ["h264_nvenc", "h264_qsv", "h264_amf", "libx264"]);
assert.equal(parseFfmpegMajorVersion("ffmpeg version 7.1"), 7);
assert.equal(parseFfmpegMajorVersion("ffmpeg version N-92722-gf22"), 4);
assert.deepEqual(getFfmpegCfrArgs(4), ["-vsync", "cfr"]);
assert.deepEqual(getFfmpegCfrArgs(5), ["-fps_mode", "cfr"]);

for (const encoder of VIDEO_ENCODERS) {
  const defaultPreset = resolveEncoderPreset(encoder, null);
  assert(ENCODER_PRESETS[encoder].includes(defaultPreset));
  for (const preset of ENCODER_PRESETS[encoder]) {
    const args = getEncoderQualityArgs(encoder, 20, preset);
    assert(args.includes(preset), `${encoder} 应接受 ${preset}`);
  }
}
assert.equal(resolveEncoderPreset("h264_nvenc", null), "p4");
assert.equal(resolveEncoderPreset("h264_qsv", null), "medium");
assert.equal(resolveEncoderPreset("h264_amf", null), "balanced");
assert.throws(() => resolveEncoderPreset("h264_nvenc", "ultrafast"), /必须是/);
assert.throws(() => resolveEncoderPreset("h264_qsv", "p4"), /必须是/);
assert.throws(() => resolveEncoderPreset("h264_amf", "slow"), /必须是/);
const detectedNvenc = parseEncoderPresetHelp("h264_nvenc", "-preset <int> p1 p4 p7");
assert.deepEqual([...detectedNvenc], ["p1", "p4", "p7"]);
assert.deepEqual(parseEncoderPresetHelp("libx264", "Set the encoding preset (default medium)"), new Set(ENCODER_PRESETS.libx264));
assert.equal(resolveEncoderPreset("h264_nvenc", null, detectedNvenc), "p4");
const legacyNvenc = parseEncoderPresetHelp("h264_nvenc", "default slow medium fast hp hq");
assert.equal(resolveEncoderPreset("h264_nvenc", null, legacyNvenc), "medium");
assert.throws(() => resolveEncoderPreset("h264_nvenc", "p5", detectedNvenc), /当前 ffmpeg/);
assert.deepEqual(getEncoderQualityArgs("libx265", 18, "slow"), ["-preset", "slow", "-crf", "18", "-threads", "0"]);
assert.deepEqual(getEncoderQualityArgs("h264_nvenc", 20, "p4"), ["-preset", "p4", "-rc", "vbr", "-cq", "20", "-b:v", "0"]);
assert.deepEqual(getEncoderQualityArgs("h264_qsv", 21, "fast"), ["-preset", "fast", "-global_quality", "21"]);
assert.deepEqual(getEncoderQualityArgs("h264_amf", 22, "balanced"), ["-quality", "balanced", "-rc", "cqp", "-qp_i", "22", "-qp_p", "22"]);
const probeArgs = buildEncoderProbeArgs({ encoder: "libx264", encoderPreset: "medium" });
assert(probeArgs.includes("testsrc2=size=320x180:rate=10:duration=0.5"));
assert.deepEqual(probeArgs.slice(probeArgs.indexOf("-c:v"), probeArgs.indexOf("-frames:v")), [
  "-c:v", "libx264", "-preset", "medium", "-crf", "28", "-threads", "0", "-pix_fmt", "yuv420p",
]);
assert.deepEqual(probeArgs.slice(-4), ["5", "-f", "null", "-"]);
const probeFailure = await probeEncoder("fake-ffmpeg", "h264_nvenc", legacyNvenc, async (_command, args) => {
  assert(args.includes("medium"), "legacy NVENC probe must use a supported legacy preset");
  throw new Error("driver rejected legacy encoder");
});
assert.equal(probeFailure.compiled, true);
assert.equal(probeFailure.available, false);
assert.match(probeFailure.failureReason, /driver rejected legacy encoder/);
const probeSuccess = await probeEncoder("fake-ffmpeg", "h264_nvenc", detectedNvenc, async (_command, args) => {
  assert(args.includes("p4"), "modern NVENC probe must default to p4");
  return { stdout: "", stderr: "" };
});
assert.equal(probeSuccess.available, true);
assert.equal(probeSuccess.defaultPreset, "p4");

// AMF 多候选码控探测：cqp 失败后依次回退 qvbr / vbr_peak，并记录命中模式。
const amfCandidates = getEncoderRateControlCandidates("h264_amf");
assert.deepEqual(amfCandidates, ["cqp", "qvbr", "vbr_peak"]);
assert.deepEqual(getEncoderRateControlCandidates("libx264"), [null]);
const triedRcModes = [];
const probeAmfFallback = await probeEncoder("fake-ffmpeg", "h264_amf", new Set(ENCODER_PRESETS.h264_amf), async (_command, args) => {
  const rcIndex = args.indexOf("-rc");
  const mode = rcIndex >= 0 ? args[rcIndex + 1] : null;
  triedRcModes.push(mode);
  if (mode === "cqp") throw new Error("driver rejected cqp");
  return { stdout: "", stderr: "" };
});
assert.equal(probeAmfFallback.available, true);
assert.equal(probeAmfFallback.rateControl, "qvbr");
assert.deepEqual(triedRcModes, ["cqp", "qvbr"]);
assert.equal(
  getEncoderQualityArgs("h264_amf", 20, "balanced", null, "qvbr").join(" "),
  "-quality balanced -rc qvbr -qvbr_quality_level 20",
);
assert.equal(
  getEncoderQualityArgs("h264_amf", 20, "balanced", null, "vbr_peak").join(" "),
  "-quality balanced -rc vbr_peak -b:v 8000k",
);
const probeAmfAllFail = await probeEncoder("fake-ffmpeg", "h264_amf", new Set(ENCODER_PRESETS.h264_amf), async () => {
  throw new Error("driver rejected");
});
assert.equal(probeAmfAllFail.available, false);
assert.match(probeAmfAllFail.failureReason, /driver rejected/);

assert.deepEqual(normalizeCanonicalOptions(), CANONICAL_RENDER_OPTIONS);
for (const secondaryMode of ["none", "translation", "roman", "both"]) {
  assert.equal(normalizeCanonicalOptions({ secondaryMode }).secondaryMode, secondaryMode);
}
for (const lyricWidth of [30, 54, 80, 100]) assert.equal(normalizeCanonicalOptions({ lyricWidth }).lyricWidth, lyricWidth);
for (const lyricOffset of [-600, 0, 600]) assert.equal(normalizeCanonicalOptions({ lyricOffset }).lyricOffset, lyricOffset);
for (const backgroundSaturation of [0, 1.2, 2]) assert.equal(normalizeCanonicalOptions({ backgroundSaturation }).backgroundSaturation, backgroundSaturation);
for (const browserGpu of ["auto", "on", "off"]) assert.equal(normalizeCanonicalOptions({ browserGpu }).browserGpu, browserGpu);
assert.throws(() => normalizeCanonicalOptions({ lyricWidth: 101 }), /30\.\.100/);
assert.throws(() => normalizeCanonicalOptions({ lyricOffset: 601 }), /-600\.\.600/);
assert.throws(() => normalizeCanonicalOptions({ backgroundSaturation: 2.1 }), /0\.\.2/);
assert.throws(() => normalizeCanonicalOptions({ secondaryMode: "trans" }), /none.*both/);
assert.throws(() => normalizeCanonicalOptions({ browserGpu: "yes" }), /auto.*off/);

const tempRoot = mkdtempSync(join(tmpdir(), "amll-runtime-test-"));
try {
  const appDir = join(tempRoot, "ExampleApp");
  const runtimeDir = join(appDir, "runtime");
  const scriptPath = join(runtimeDir, "content", "scripts", "render-mv.mjs");
  const ffmpegPath = join(runtimeDir, "ffmpeg", process.platform === "win32" ? "ffmpeg.exe" : "ffmpeg");
  const browserPath = join(runtimeDir, "browsers", "chromium", "stable", "bin", "chrome.exe");
  mkdirSync(join(runtimeDir, "content", "scripts"), { recursive: true });
  mkdirSync(join(runtimeDir, "ffmpeg"), { recursive: true });
  mkdirSync(join(runtimeDir, "browsers", "chromium", "stable", "bin"), { recursive: true });
  writeFileSync(scriptPath, "");
  writeFileSync(ffmpegPath, "");
  writeFileSync(browserPath, "");
  assert.deepEqual(detectInstallLayout(scriptPath), { appDir, runtimeDir });
  const bundled = discoverBundledRuntime(scriptPath);
  assert.equal(bundled.ffmpegPath, ffmpegPath);
  assert.equal(bundled.browserPath, browserPath);
  const tools = resolveRuntimeTools({ scriptPath, env: {} });
  assert.equal(tools.ffmpeg.path, ffmpegPath);
  assert.equal(tools.ffmpeg.source, "安装布局 runtime/ffmpeg");
  assert.equal(tools.browser.path, browserPath);
  const explicit = resolveRuntimeTools({ scriptPath, explicitFfmpeg: join(tempRoot, "custom-ffmpeg"), explicitBrowser: join(tempRoot, "custom-browser"), env: {} });
  assert.equal(explicit.ffmpeg.source, "--ffmpeg");
  assert.equal(explicit.browser.source, "--browser");
} finally {
  rmSync(tempRoot, { recursive: true, force: true });
}

const developmentRoot = mkdtempSync(join(tmpdir(), "amll-development-runtime-test-"));
try {
  const scriptPath = join(developmentRoot, "scripts", "render-mv.mjs");
  const stagedFfmpeg = join(developmentRoot, "installer", "staging", "runtime", "ffmpeg", process.platform === "win32" ? "ffmpeg.exe" : "ffmpeg");
  mkdirSync(join(developmentRoot, "scripts"), { recursive: true });
  mkdirSync(join(developmentRoot, "installer", "staging", "runtime", "ffmpeg"), { recursive: true });
  writeFileSync(scriptPath, "");
  writeFileSync(stagedFfmpeg, "");
  assert.equal(discoverDevelopmentFfmpeg(scriptPath), stagedFfmpeg);
  const tools = resolveRuntimeTools({ scriptPath, env: { FFMPEG_PATH: join(developmentRoot, "legacy-ffmpeg") } });
  assert.equal(tools.ffmpeg.path, stagedFfmpeg);
  assert.equal(tools.ffmpeg.source, "开发布局 installer/staging/runtime/ffmpeg");
} finally {
  rmSync(developmentRoot, { recursive: true, force: true });
}

assert.doesNotThrow(() => assertCopyTimingCompatible({ audioCodec: "copy", durationLimitMs: null, headMs: 0, tailMs: 0, audioOffsetMs: 0 }));
assert.throws(() => assertCopyTimingCompatible({ audioCodec: "copy", durationLimitMs: 1000, headMs: 0, tailMs: 0, audioOffsetMs: 0 }), /--duration/);
assert.throws(() => assertCopyTimingCompatible({ audioCodec: "copy", durationLimitMs: null, headMs: 1, tailMs: 0, audioOffsetMs: 1 }), /--head/);
assert.throws(() => assertCopyTimingCompatible({ audioCodec: "copy", durationLimitMs: null, headMs: 0, tailMs: 1, audioOffsetMs: 0 }), /--tail/);
assert.throws(() => assertCopyTimingCompatible({ audioCodec: "copy", durationLimitMs: null, headMs: 0, tailMs: 0, audioOffsetMs: 1 }), /额外音频\/画面 offset/);
try {
  assertCopyTimingCompatible({ audioCodec: "copy", durationLimitMs: 1000, headMs: 500, tailMs: 250, audioOffsetMs: 500 });
  assert.fail("copy timing conflicts should throw");
} catch (error) {
  assert.match(error.message, /--duration.*--head.*--tail/);
  assert.equal((error.message.match(/--head/g) ?? []).length, 1);
  assert(!error.message.includes("offset"), "head offset must not be reported twice");
}

const common = {
  audioPath: "D:/song.wav",
  outputPath: "D:/mv.mp4",
  fps: 30,
  durationMs: 2500,
  encoder: "libx264",
  encoderPreset: "medium",
  quality: 16,
  audioCodec: "aac",
  audioBitrate: "256k",
  ffmpegMajorVersion: 4,
};
const delayed = buildFfmpegArgs({ ...common, headMs: 125, audioOffsetMs: 125 });
assert.deepEqual(delayed.slice(delayed.indexOf("-itsoffset"), delayed.indexOf("-itsoffset") + 2), ["-itsoffset", "0.125000"]);
assert(!delayed.includes("apad"));
assert(!delayed.includes("-shortest"));
assert(delayed.includes("-vsync"));
assert.deepEqual(delayed.slice(delayed.indexOf("-t"), delayed.indexOf("-t") + 2), ["-t", "2.500000"]);
assert.deepEqual(delayed.slice(-3), ["pipe:1", "-nostats", "D:/mv.mp4"]);

const early = buildFfmpegArgs({ ...common, audioOffsetMs: -250, ffmpegMajorVersion: 6 });
assert(!early.includes("-itsoffset"));
assert(early.includes("atrim=start=0.250000,asetpts=PTS-STARTPTS"));
assert(!early.some((item) => item.includes("apad")));
assert(early.includes("-fps_mode"));
assert.throws(
  () => buildFfmpegArgs({ ...common, outputPath: "D:/mv.mp4", audioCodec: "flac" }),
  /MP4.*FLAC/,
);
const copied = buildFfmpegArgs({ ...common, outputPath: "D:/mv.mkv", audioCodec: "copy", audioOffsetMs: 0 });
assert(!copied.includes("-af"));
assert(!copied.includes("apad"));
assert.throws(
  () => buildFfmpegArgs({ ...common, outputPath: "D:/mv.mp4", audioCodec: "copy", audioOffsetMs: 0 }),
  /copy 仅支持 MKV/,
);
assert.throws(
  () => buildFfmpegArgs({ ...common, outputPath: "D:/mv.mov", audioCodec: "copy", audioOffsetMs: 0 }),
  /copy 仅支持 MKV/,
);
for (const incompatible of [
  { durationLimitMs: 1000 },
  { headMs: 100, audioOffsetMs: 100 },
  { tailMs: 100 },
  { audioOffsetMs: -1 },
  { audioOffsetMs: 1 },
]) {
  assert.throws(
    () => buildFfmpegArgs({ ...common, outputPath: "D:/mv.mkv", audioCodec: "copy", ...incompatible }),
    /copy 无法可靠处理/,
  );
}

console.log("render-lib tests passed");
