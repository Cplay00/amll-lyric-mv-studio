#!/usr/bin/env node
/**
 * 生成演示素材，便于零配置跑通整条链路：
 *   public/demo/demo.wav    合成音乐（含低频鼓点，能让流体背景跟着跳动）
 *   public/demo/demo.ttml   逐字（Word 级）TTML 歌词，含翻译
 *   public/demo/cover.png   程序化生成的专辑封面（驱动背景弥散取色）
 *
 * 用法：npm run demo:assets
 */
import { deflateSync } from "node:zlib";
import { mkdirSync, writeFileSync } from "node:fs";
import { resolve } from "node:path";

const OUT_DIR = resolve("public", "demo");
const SAMPLE_RATE = 44100;
const BPM = 96;
const BEAT = 60 / BPM; // 一拍秒数
const BARS = 8;
const DURATION = BARS * 4 * BEAT;
mkdirSync(OUT_DIR, { recursive: true });

/* ---------------- 1. 合成音频 ---------------- */

const totalSamples = Math.floor(DURATION * SAMPLE_RATE);
const left = new Float32Array(totalSamples);
const right = new Float32Array(totalSamples);

/** 可调参数的简单合成器音色 */
function addTone(t0, dur, freq, amp, pan = 0, waveform = "sine", attack = 0.01, release = 0.35) {
  const start = Math.floor(t0 * SAMPLE_RATE);
  const count = Math.floor(dur * SAMPLE_RATE);
  for (let i = 0; i < count; i++) {
    const idx = start + i;
    if (idx < 0 || idx >= totalSamples) continue;
    const t = i / SAMPLE_RATE;
    const phase = 2 * Math.PI * freq * t;
    let sample;
    if (waveform === "sine") sample = Math.sin(phase);
    else if (waveform === "triangle") sample = (2 / Math.PI) * Math.asin(Math.sin(phase));
    else if (waveform === "saw") {
      const frac = (freq * t) % 1;
      sample = 2 * frac - 1;
    } else {
      // 柔和的方波（叠加奇次谐波，避免刺耳）
      sample =
        Math.sin(phase) +
        Math.sin(3 * phase) / 3 +
        Math.sin(5 * phase) / 5 +
        Math.sin(7 * phase) / 7;
      sample *= 0.55;
    }
    // ADSR 近似：快速起音 + 指数衰减
    const env =
      Math.min(1, t / attack) * Math.exp(-Math.max(0, t - attack) / release);
    const value = sample * amp * env;
    const l = Math.sqrt((1 - pan) / 2 + 0.5) * 0.7;
    const r = Math.sqrt((1 + pan) / 2 + 0.5) * 0.7;
    left[idx] += value * l;
    right[idx] += value * r;
  }
}

/** 低频鼓点，用于驱动 AMLL 背景随节拍起伏 */
function addKick(t0, amp = 0.9) {
  const start = Math.floor(t0 * SAMPLE_RATE);
  const dur = 0.34;
  const count = Math.floor(dur * SAMPLE_RATE);
  for (let i = 0; i < count; i++) {
    const idx = start + i;
    if (idx < 0 || idx >= totalSamples) continue;
    const t = i / SAMPLE_RATE;
    // 音高从 110Hz 快速下滑到 45Hz，产生典型底鼓冲击感
    const freq = 45 + 65 * Math.exp(-t / 0.045);
    const env = Math.exp(-t / 0.11);
    const value = Math.sin(2 * Math.PI * freq * t) * amp * env;
    left[idx] += value;
    right[idx] += value;
  }
}

/** 轻量底噪与镲片感，提升背景活跃度 */
function addHat(t0, amp = 0.12) {
  const start = Math.floor(t0 * SAMPLE_RATE);
  const count = Math.floor(0.06 * SAMPLE_RATE);
  for (let i = 0; i < count; i++) {
    const idx = start + i;
    if (idx < 0 || idx >= totalSamples) continue;
    const t = i / SAMPLE_RATE;
    const noise = Math.random() * 2 - 1;
    const env = Math.exp(-t / 0.018);
    const value = noise * amp * env;
    left[idx] += value * 0.8;
    right[idx] += value;
  }
}

// 和弦进行：Am - F - C - G（四个和弦循环两遍）
const CHORDS = [
  [220.0, 261.63, 329.63], // Am
  [174.61, 220.0, 261.63], // F
  [261.63, 329.63, 392.0], // C
  [196.0, 246.94, 293.66], // G
];
const MELODY = [
  [0, 1, 659.25], [1, 0.5, 587.33], [1.5, 0.5, 523.25], [2, 1, 587.33], [3, 1, 659.25],
  [4, 1, 523.25], [5, 0.5, 493.88], [5.5, 0.5, 440.0], [6, 1, 493.88], [7, 1, 523.25],
  [8, 1, 659.25], [9, 0.5, 587.33], [9.5, 0.5, 659.25], [10, 1, 783.99], [11, 1, 659.25],
  [12, 1, 587.33], [13, 0.5, 523.25], [13.5, 0.5, 493.88], [14, 1, 440.0], [15, 1, 493.88],
];

for (let bar = 0; bar < BARS; bar++) {
  const barStart = bar * 4 * BEAT;
  const chord = CHORDS[bar % CHORDS.length];

  // 铺底和弦：每小节两次，长音
  for (const [noteIdx, freq] of chord.entries()) {
    addTone(barStart, 2 * BEAT, freq, 0.12, (noteIdx - 1) * 0.5, "triangle", 0.08, 0.9);
    addTone(barStart + 2 * BEAT, 2 * BEAT, freq, 0.11, (noteIdx - 1) * -0.5, "triangle", 0.08, 0.9);
  }

  // 鼓组：四拍底鼓、反拍镲片
  for (let beat = 0; beat < 4; beat++) {
    addKick(barStart + beat * BEAT, beat === 0 ? 0.95 : 0.72);
    addHat(barStart + beat * BEAT + BEAT / 2, 0.1);
  }

  if (bar >= 2) {
    for (const [beat, len, freq] of MELODY) {
      const globalBeat = bar * 4 + beat;
      if (globalBeat * BEAT >= DURATION) break;
      addTone(globalBeat * BEAT, len * BEAT * 0.95, freq, 0.19, Math.sin(globalBeat) * 0.35, "saw", 0.015, 0.28);
    }
  }
}

// 首尾淡入淡出，避免爆音
const fade = Math.floor(0.35 * SAMPLE_RATE);
for (let i = 0; i < fade; i++) {
  const g = i / fade;
  left[i] *= g;
  right[i] *= g;
  left[totalSamples - 1 - i] *= g;
  right[totalSamples - 1 - i] *= g;
}

// 软限幅，防止叠加削波
function softClip(x) {
  return Math.tanh(x * 1.05) * 0.92;
}

const pcm = Buffer.alloc(totalSamples * 4);
for (let i = 0; i < totalSamples; i++) {
  pcm.writeInt16LE(Math.max(-32768, Math.min(32767, Math.round(softClip(left[i]) * 32767))), i * 4);
  pcm.writeInt16LE(Math.max(-32768, Math.min(32767, Math.round(softClip(right[i]) * 32767))), i * 4 + 2);
}

const dataSize = pcm.length;
const wav = Buffer.alloc(44 + dataSize);
wav.write("RIFF", 0);
wav.writeUInt32LE(36 + dataSize, 4);
wav.write("WAVE", 8);
wav.write("fmt ", 12);
wav.writeUInt32LE(16, 16);
wav.writeUInt16LE(1, 20); // PCM
wav.writeUInt16LE(2, 22); // 立体声
wav.writeUInt32LE(SAMPLE_RATE, 24);
wav.writeUInt32LE(SAMPLE_RATE * 2 * 2, 28);
wav.writeUInt16LE(4, 32);
wav.writeUInt16LE(16, 34);
wav.write("data", 36);
wav.writeUInt32LE(dataSize, 40);
pcm.copy(wav, 44);
writeFileSync(resolve(OUT_DIR, "demo.wav"), wav);
console.log(`已生成 public/demo/demo.wav（${DURATION.toFixed(2)}s，${(wav.length / 1024 / 1024).toFixed(1)}MB）`);

/* ---------------- 2. 生成逐字 TTML ---------------- */

const LYRICS = [
  { text: "夜色漫过城市灯火", trans: "Night spills over the city lights" },
  { text: "耳机里循环着那首歌", trans: "The same song loops in my ears" },
  { text: "每一个字都清晰滚烫", trans: "Every word burns clear and bright" },
  { text: "像有人轻轻跟着我唱", trans: "Like someone softly sings along" },
  { text: "流光在屏幕上弥散开来", trans: "Iridescence diffuses on the screen" },
  { text: "把心事染成温柔色彩", trans: "Dyeing my thoughts in gentle hues" },
  { text: "让时间慢下来 别走太快", trans: "Let time slow down, don't rush away" },
  { text: "这一刻我只想留下来", trans: "This moment, I just want to stay" },
];

const fmt = (sec) => {
  const m = Math.floor(sec / 60);
  const s = sec - m * 60;
  return `${String(m).padStart(2, "0")}:${s.toFixed(3).padStart(6, "0")}`;
};
const escapeXml = (s) =>
  s.replace(/[<>&'"]/g, (c) => ({ "<": "&lt;", ">": "&gt;", "&": "&amp;", "'": "&apos;", '"': "&quot;" })[c]);

// 歌词从第 2 小节（约 5s）开始，每行占 2 拍
const lyricStart = 4 * BEAT;
const lineDur = 2 * BEAT;
const lines = LYRICS.map((line, i) => {
  const begin = lyricStart + i * lineDur;
  const end = begin + lineDur;
  // 按字符均分时间，模拟逐字歌词
  const chars = [...line.text];
  const per = lineDur / chars.length;
  const spans = chars
    .map((ch, ci) => {
      const b = begin + ci * per;
      const e = b + per;
      return `        <span begin="${fmt(b)}" end="${fmt(e)}">${escapeXml(ch)}</span>`;
    })
    .join("\n");
  return `      <p begin="${fmt(begin)}" end="${fmt(end)}" itunes:key="L${i + 1}" ttm:agent="v1">\n${spans}\n        <span ttm:role="x-translation" xml:lang="en">${escapeXml(line.trans)}</span>\n      </p>`;
});

const ttml = `<?xml version="1.0" encoding="utf-8"?>
<tt xmlns="http://www.w3.org/ns/ttml" xmlns:ttm="http://www.w3.org/ns/ttml#metadata" xmlns:tts="http://www.w3.org/ns/ttml#styling" xmlns:itunes="http://music.apple.com/lyric-ttml-internal" xmlns:amll="http://www.example.com/ns/amll" xml:lang="zh-CN" itunes:timing="Word">
  <head>
    <metadata>
      <ttm:title>流光弥散</ttm:title>
      <ttm:agent type="person" xml:id="v1"><ttm:name>AMLL Demo</ttm:name></ttm:agent>
    </metadata>
    <amll:meta key="musicName" value="流光弥散"/>
    <amll:meta key="artists" value="AMLL Demo"/>
    <amll:meta key="album" value="Apple Music 风格歌词 MV 示例"/>
  </head>
  <body>
    <div>
${lines.join("\n")}
    </div>
  </body>
</tt>
`;
writeFileSync(resolve(OUT_DIR, "demo.ttml"), ttml, "utf8");
console.log(`已生成 public/demo/demo.ttml（${LYRICS.length} 行逐字歌词）`);

/* ---------------- 3. 生成专辑封面 PNG ---------------- */

const W = 640;
const H = 640;
const rgb = Buffer.alloc(W * H * 3);

// Apple Music 风格的柔和弥散色块：几个高斯光斑叠加
const blobs = [
  { x: 0.26, y: 0.24, r: 0.62, c: [92, 40, 150] },
  { x: 0.78, y: 0.30, r: 0.58, c: [214, 48, 92] },
  { x: 0.34, y: 0.76, r: 0.60, c: [26, 96, 178] },
  { x: 0.80, y: 0.80, r: 0.55, c: [242, 140, 40] },
  { x: 0.52, y: 0.52, r: 0.40, c: [150, 60, 200] },
];

for (let y = 0; y < H; y++) {
  for (let x = 0; x < W; x++) {
    const u = x / W;
    const v = y / H;
    let r = 8;
    let g = 6;
    let b = 18;
    for (const blob of blobs) {
      const dx = (u - blob.x) * 1.0;
      const dy = (v - blob.y) * 1.0;
      const d2 = dx * dx + dy * dy;
      const w = Math.exp(-d2 / (2 * blob.r * blob.r * 0.32));
      r += blob.c[0] * w * 0.55;
      g += blob.c[1] * w * 0.55;
      b += blob.c[2] * w * 0.55;
    }
    // 细颗粒，避免大面积色带
    const grain = (Math.sin(x * 12.9898 + y * 78.233) * 43758.5453) % 1;
    const noise = (grain - Math.floor(grain) - 0.5) * 5;
    const i = (y * W + x) * 3;
    rgb[i] = Math.max(0, Math.min(255, Math.round(r + noise)));
    rgb[i + 1] = Math.max(0, Math.min(255, Math.round(g + noise)));
    rgb[i + 2] = Math.max(0, Math.min(255, Math.round(b + noise)));
  }
}

// 最小 PNG 编码器（仅真彩色、无滤波）
function crc32(buf) {
  let c;
  const table = crc32.table ?? (crc32.table = (() => {
    const t = new Int32Array(256);
    for (let n = 0; n < 256; n++) {
      c = n;
      for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
      t[n] = c;
    }
    return t;
  })());
  let crc = -1;
  for (let i = 0; i < buf.length; i++) crc = table[(crc ^ buf[i]) & 0xff] ^ (crc >>> 8);
  return (crc ^ -1) >>> 0;
}

function chunk(type, data) {
  const len = Buffer.alloc(4);
  len.writeUInt32BE(data.length, 0);
  const typeBuf = Buffer.from(type, "ascii");
  const crcBuf = Buffer.alloc(4);
  crcBuf.writeUInt32BE(crc32(Buffer.concat([typeBuf, data])), 0);
  return Buffer.concat([len, typeBuf, data, crcBuf]);
}

const ihdr = Buffer.alloc(13);
ihdr.writeUInt32BE(W, 0);
ihdr.writeUInt32BE(H, 4);
ihdr[8] = 8; // bit depth
ihdr[9] = 2; // truecolor
ihdr[10] = 0;
ihdr[11] = 0;
ihdr[12] = 0;

const raw = Buffer.alloc(H * (1 + W * 3));
for (let y = 0; y < H; y++) {
  raw[y * (1 + W * 3)] = 0; // filter: none
  rgb.copy(raw, y * (1 + W * 3) + 1, y * W * 3, (y + 1) * W * 3);
}

const png = Buffer.concat([
  Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
  chunk("IHDR", ihdr),
  chunk("IDAT", deflateSync(raw, { level: 9 })),
  chunk("IEND", Buffer.alloc(0)),
]);
writeFileSync(resolve(OUT_DIR, "cover.png"), png);
console.log(`已生成 public/demo/cover.png（${W}×${H}）`);
