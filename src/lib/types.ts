import type { LyricLine } from "@applemusic-like-lyrics/core";

export type SecondaryLyricMode = "none" | "translation" | "roman" | "both";
export type LyricAlignMode = "left" | "center" | "right";

export interface TrackAssets {
  /** 音频 Object URL 或远程 URL */
  audioUrl: string;
  /** 封面 Object URL 或远程 URL（可空，背景将回退为默认纯色渐变） */
  coverUrl: string | null;
  /** 原始解析的歌词行 */
  rawLyricLines?: LyricLine[];
  /** 已解析的歌词行 */
  lyricLines: LyricLine[];
  /** 歌词格式名称，用于界面显示 */
  lyricFormat: string;
  /** 曲名 */
  title: string;
  /** 艺术家 */
  artist: string;
  /** 专辑名 */
  album: string;
}

export interface RenderOptions {
  // 基础与画布
  presetId: string;
  fps: number;
  flowSpeed: number; // 0 为完全静止，1 为正常流动
  renderScale: number;
  wordFadeWidth: number;
  alignPosition: number;
  enableSpring: boolean;
  enableBlur: boolean;
  enableScale: boolean;
  hidePassedLines: boolean;
  staticMode: boolean;
  showChrome: boolean;

  // 扩展参数
  title: string;
  artist: string;
  album: string;
  fontFamily: string;
  fontScale: number; // 相对基准字号缩放比例，默认 1.0 (0.5 ~ 2.0)
  fontWeight: number; // 默认 700 (100 ~ 900)
  secondaryMode: SecondaryLyricMode; // none | translation | roman | both，默认 "both"
  showSongInfo: boolean; // 默认 true
  showArtist: boolean; // 歌曲信息块中的艺术家行，默认 true
  showAlbum: boolean; // 歌曲信息块中的专辑行，默认 true
  showArtwork: boolean; // 默认 true
  showControls: boolean; // 默认 false
  artworkScale: number; // 默认 1.0 (0.5 ~ 1.5)
  lyricAlign: LyricAlignMode; // 默认 "left"
  lyricOffset: number; // 垂直位置偏移 (px)，范围 -600 ~ 600，默认 0
  lyricWidth: number; // 歌词区域宽度占比百分比 (30 ~ 80)，默认 54
  lyricFade: number; // 歌词上下边缘羽化，占舞台高度比例 0 ~ 0.3，默认 0.08
  backgroundDim: number; // 遮罩暗度 0 ~ 0.9，默认 0.15
  backgroundSaturation: number; // 背景饱和度比例 0 ~ 2.0，默认 1.2
  backgroundBlur: number; // 额外背景高斯模糊 (px)，默认 0
}

export const DEFAULT_RENDER_OPTIONS: RenderOptions = {
  presetId: "1080p",
  fps: 60,
  flowSpeed: 1, // 0 为静止，1 为正常速度
  renderScale: 0.8,
  wordFadeWidth: 0.5,
  alignPosition: 0.35,
  enableSpring: true,
  enableBlur: true,
  enableScale: true,
  hidePassedLines: false,
  staticMode: false,
  showChrome: true,

  // 新增扩展参数默认值
  title: "",
  artist: "",
  album: "",
  fontFamily: "",
  fontScale: 1.0,
  fontWeight: 700,
  secondaryMode: "both",
  showSongInfo: true,
  showArtist: true,
  showAlbum: true,
  showArtwork: true,
  showControls: false, // 默认不展示播放控制按钮
  artworkScale: 1.0,
  lyricAlign: "left",
  lyricOffset: 0,
  lyricWidth: 54, // 歌词区域宽度百分数 30..80，默认 54
  lyricFade: 0.08, // 歌词边缘羽化默认 8% 舞台高度
  backgroundDim: 0.15,
  backgroundSaturation: 1.2, // 背景饱和度比例 0..2，默认 1.2
  backgroundBlur: 0,
};

export interface StageRectInfo {
  width: number;
  height: number;
  scale?: number;
  left: number;
  top: number;
}

export interface StageReadyState {
  assetsLoaded: boolean;
  audioMetaLoaded: boolean;
  fontLoaded: boolean;
  stageLayoutReady: boolean;
  backgroundReady: boolean;
}

export interface StageReadinessState {
  ready: boolean;
  audioReady: boolean;
  lyricsReady: boolean;
  coverReady: boolean;
  fontsReady: boolean;
  layoutReady: boolean;
}

/** 自动化脚本与宿主交互的桥接对象 */
export interface AmllMvBridge {
  ready: boolean;
  readiness?: StageReadinessState | StageReadyState;
  stageRect?: StageRectInfo;
  error?: string | null;
  audioStartedAt?: number | null;
  audioEndedAt?: number | null;
  getTrack?: () => TrackAssets | null;
  getOptions?: () => RenderOptions;
  setOptions?: (opts: Partial<RenderOptions>) => void;
  play?: () => Promise<void> | void;
  pause?: () => void;
  seek?: (timeMs: number) => void;
  getCurrentTime?: () => number;
  getDuration?: () => number;
}

/** 辅助 clamp 数值到区间 [min, max] */
export function clamp(val: number, min: number, max: number): number {
  if (!Number.isFinite(val)) return min;
  return Math.min(Math.max(val, min), max);
}

/** 规范化并限制 RenderOptions 部分或全部数值到合法区间 */
export function sanitizeRenderOptions(raw: Partial<RenderOptions>): Partial<RenderOptions> {
  const result: Partial<RenderOptions> = { ...raw };

  if (result.fps !== undefined) {
    result.fps = clamp(Math.round(result.fps), 1, 240);
  }
  if (result.flowSpeed !== undefined) {
    result.flowSpeed = clamp(result.flowSpeed, 0, 5);
  }
  if (result.renderScale !== undefined) {
    result.renderScale = clamp(result.renderScale, 0.1, 2.0);
  }
  if (result.wordFadeWidth !== undefined) {
    result.wordFadeWidth = clamp(result.wordFadeWidth, 0, 2.0);
  }
  if (result.alignPosition !== undefined) {
    result.alignPosition = clamp(result.alignPosition, 0, 1.0);
  }
  if (result.fontScale !== undefined) {
    result.fontScale = clamp(result.fontScale, 0.5, 2.0);
  }
  if (result.fontWeight !== undefined) {
    result.fontWeight = clamp(Math.round(result.fontWeight / 100) * 100, 100, 900);
  }
  if (result.artworkScale !== undefined) {
    result.artworkScale = clamp(result.artworkScale, 0.5, 1.5);
  }
  if (result.lyricOffset !== undefined) {
    result.lyricOffset = clamp(result.lyricOffset, -600, 600);
  }
  if (result.lyricWidth !== undefined) {
    result.lyricWidth = clamp(result.lyricWidth, 30, 80);
  }
  if (result.lyricFade !== undefined) {
    result.lyricFade = clamp(result.lyricFade, 0, 0.3);
  }
  if (result.backgroundDim !== undefined) {
    result.backgroundDim = clamp(result.backgroundDim, 0, 0.9);
  }
  if (result.backgroundSaturation !== undefined) {
    result.backgroundSaturation = clamp(result.backgroundSaturation, 0, 2.0);
  }
  if (result.backgroundBlur !== undefined) {
    result.backgroundBlur = clamp(result.backgroundBlur, 0, 80);
  }
  if (result.secondaryMode !== undefined) {
    const mode = String(result.secondaryMode).toLowerCase();
    if (mode === "none" || mode === "translation" || mode === "roman" || mode === "both") {
      result.secondaryMode = mode as SecondaryLyricMode;
    } else {
      result.secondaryMode = "both";
    }
  }
  if (result.lyricAlign !== undefined) {
    const align = String(result.lyricAlign).toLowerCase();
    if (align === "left" || align === "center" || align === "right") {
      result.lyricAlign = align as LyricAlignMode;
    } else {
      result.lyricAlign = "left";
    }
  }

  return result;
}

export function fileNameToTitle(name: string): string {
  return name.replace(/\.[^./\\]+$/, "").trim() || "未命名歌曲";
}

/** XX:SS 播放时间，用于控制栏 */
export function formatClock(ms: number): string {
  if (!Number.isFinite(ms) || ms < 0) ms = 0;
  const total = Math.floor(ms / 1000);
  const m = Math.floor(total / 60);
  const s = total % 60;
  return `${m}:${String(s).padStart(2, "0")}`;
}

/** 舞台分辨率预设，录制与导出共用 */
export interface StagePreset {
  id: string;
  name?: string;
  label: string;
  width: number;
  height: number;
  /** 画布短边基准字号，AMLL 通过 CSS 变量读取 */
  fontVw: number;
}

export const STAGE_PRESETS: StagePreset[] = [
  { id: "1080p", label: "1080P 横屏 16:9", name: "1080P 横屏 16:9", width: 1920, height: 1080, fontVw: 4.2 },
  { id: "1440p", label: "1440P 横屏 16:9", name: "1440P 横屏 16:9", width: 2560, height: 1440, fontVw: 4.2 },
  { id: "4k", label: "4K 横屏 16:9", name: "4K 横屏 16:9", width: 3840, height: 2160, fontVw: 4.2 },
  { id: "vertical", label: "竖屏 9:16（短视频）", name: "竖屏 9:16（短视频）", width: 1080, height: 1920, fontVw: 7.0 },
  { id: "square", label: "方形 1:1", name: "方形 1:1", width: 1080, height: 1080, fontVw: 5.4 },
];

export type StageLayoutClass = "landscape" | "portrait" | "square";

export function getStageLayoutClass(presetId: string): StageLayoutClass {
  if (presetId === "vertical") return "portrait";
  if (presetId === "square") return "square";
  return "landscape";
}

export interface FontPreset {
  id: string;
  name: string;
  family: string;
  fontFamily: string;
  label: string;
}

export const FONT_PRESETS: FontPreset[] = [
  { id: "system", name: "系统默认无衬线", family: "system-ui, -apple-system, sans-serif", fontFamily: "system-ui, -apple-system, sans-serif", label: "系统默认无衬线" },
  { id: "pingfang", name: "苹方 (PingFang SC)", family: '"PingFang SC", "Microsoft YaHei", sans-serif', fontFamily: '"PingFang SC", "Microsoft YaHei", sans-serif', label: "苹方 (PingFang SC)" },
  { id: "noto-sans", name: "思源黑体 (Noto Sans SC)", family: '"Noto Sans SC", sans-serif', fontFamily: '"Noto Sans SC", sans-serif', label: "思源黑体 (Noto Sans SC)" },
  { id: "noto-serif", name: "思源宋体 (Noto Serif SC)", family: '"Noto Serif SC", serif', fontFamily: '"Noto Serif SC", serif', label: "思源宋体 (Noto Serif SC)" },
  { id: "rounded", name: "圆体 (Rounded)", family: '"Hiragino Sans GB", "Microsoft YaHei", sans-serif', fontFamily: '"Hiragino Sans GB", "Microsoft YaHei", sans-serif', label: "圆体 (Rounded)" },
];

// 单独属性 clamp 辅助函数
export function clampPresetId(val: string): string {
  const allowed = STAGE_PRESETS.map((p) => p.id);
  return allowed.includes(val) ? val : "1080p";
}

export function clampFps(val: number): number {
  return clamp(Math.round(val), 1, 240);
}

export function clampFlowSpeed(val: number): number {
  return clamp(val, 0, 5);
}

export function clampRenderScale(val: number): number {
  return clamp(val, 0.1, 2.0);
}

export function clampWordFadeWidth(val: number): number {
  return clamp(val, 0, 2.0);
}

export function clampAlignPosition(val: number): number {
  return clamp(val, 0, 1.0);
}

export function clampFontScale(val: number): number {
  return clamp(val, 0.5, 2.0);
}

export function clampFontWeight(val: number): number {
  return clamp(Math.round(val / 100) * 100, 100, 900);
}

export function clampSecondaryMode(val: unknown): SecondaryLyricMode {
  const str = String(val).toLowerCase();
  if (str === "none" || str === "translation" || str === "roman" || str === "both") {
    return str as SecondaryLyricMode;
  }
  return "none";
}

export function clampArtworkScale(val: number): number {
  return clamp(val, 0.5, 1.5);
}

export function clampLyricAlign(val: unknown): LyricAlignMode {
  const str = String(val).toLowerCase();
  if (str === "left" || str === "center" || str === "right") {
    return str as LyricAlignMode;
  }
  return "left";
}

export function clampLyricOffset(val: number): number {
  return clamp(val, -600, 600);
}

export function clampLyricWidth(val: number): number {
  return clamp(val, 30, 80);
}

export function clampLyricFade(val: number): number {
  return clamp(val, 0, 0.3);
}

export function clampBackgroundDim(val: number): number {
  return clamp(val, 0, 0.9);
}

export function clampBackgroundSaturation(val: number): number {
  return clamp(val, 0, 2.0);
}

export function clampBackgroundBlur(val: number): number {
  return clamp(val, 0, 80);
}

export function clampOptions(opts: RenderOptions): RenderOptions;
export function clampOptions(opts: Partial<RenderOptions>): Partial<RenderOptions>;
export function clampOptions(opts: Partial<RenderOptions>): Partial<RenderOptions> {
  return sanitizeRenderOptions(opts);
}
