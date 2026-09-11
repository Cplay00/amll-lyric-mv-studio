import { useCallback, useEffect, useMemo, useRef, useState, type CSSProperties } from "react";
import {
  BackgroundRender,
  LyricPlayer,
  MeshGradientRenderer,
  type BackgroundRenderRef,
} from "@applemusic-like-lyrics/react";
import type { LyricLine } from "@applemusic-like-lyrics/core";
import {
  DEFAULT_RENDER_OPTIONS,
  STAGE_PRESETS,
  clampOptions,
  clampPresetId,
  clampFps,
  clampRenderScale,
  clampFlowSpeed,
  clampWordFadeWidth,
  clampAlignPosition,
  clampFontScale,
  clampFontWeight,
  clampSecondaryMode,
  clampArtworkScale,
  clampLyricAlign,
  clampLyricOffset,
  clampLyricWidth,
  clampLyricFade,
  clampBackgroundDim,
  clampBackgroundSaturation,
  clampBackgroundBlur,
  formatClock,
  getStageLayoutClass,
  type RenderOptions,
  type StageReadyState,
  type TrackAssets,
} from "./lib/types";
import {
  deriveSecondaryLyrics,
  getLyricDuration,
  parseLyricText,
  readMeta,
} from "./lib/lyricLoader";
import { downloadBlob, startStageRecording, type StageRecorderHandle } from "./lib/recorder";
import ControlPanel from "./components/ControlPanel";

/** 自动化脚本读取的桥接对象（scripts/render-mv.mjs 依赖此结构并扩展） */
interface AutomationBridge {
  ready: boolean;
  audioStartedAt: number | null;
  audioEndedAt: number | null;
  error: string | null;
  stageRect?: {
    width: number;
    height: number;
    scale: number;
    left: number;
    top: number;
  };
  readiness?: StageReadyState;
  getTrack?: () => TrackAssets | null;
  getOptions?: () => RenderOptions;
  setOptions?: (opts: Partial<RenderOptions>) => void;
  play?: () => Promise<void>;
  pause?: () => void;
  seek?: (timeMs: number) => void;
  getCurrentTime?: () => number;
  getDuration?: () => number;
}

declare global {
  interface Window {
    __amllMv?: AutomationBridge;
  }
}

/**
 * 无封面时按曲名哈希生成一张稳定的渐变占位封面。
 * MeshGradientRenderer 完全依赖封面色彩建立调色板，缺少封面时会渲染纯黑背景。
 */
function createFallbackCover(seed: string): string {
  const canvas = document.createElement("canvas");
  canvas.width = 32;
  canvas.height = 32;
  const ctx = canvas.getContext("2d");
  if (!ctx) return "";
  let hash = 2166136261;
  for (let i = 0; i < seed.length; i++) {
    hash ^= seed.charCodeAt(i);
    hash = Math.imul(hash, 16777619);
  }
  const hue1 = Math.abs(hash) % 360;
  const hue2 = (hue1 + 60 + (Math.abs(hash >>> 8) % 140)) % 360;
  const hue3 = (hue2 + 80) % 360;
  const gradient = ctx.createLinearGradient(0, 0, 32, 32);
  gradient.addColorStop(0, `hsl(${hue1}, 74%, 54%)`);
  gradient.addColorStop(0.55, `hsl(${hue2}, 68%, 46%)`);
  gradient.addColorStop(1, `hsl(${hue3}, 62%, 38%)`);
  ctx.fillStyle = gradient;
  ctx.fillRect(0, 0, 32, 32);
  return canvas.toDataURL("image/png");
}

/** 解析 URL search params 并经过统一 clamp 覆盖 RenderOptions */
export function parseUrlOptions(search: string): Partial<RenderOptions> {
  const p = new URLSearchParams(search);
  const opts: Partial<RenderOptions> = {};

  if (p.has("preset")) {
    const val = p.get("preset");
    if (val !== null) opts.presetId = clampPresetId(val);
  }
  if (p.has("fps")) {
    const val = Number(p.get("fps"));
    if (Number.isFinite(val)) opts.fps = clampFps(val);
  }
  if (p.has("renderScale")) {
    const val = Number(p.get("renderScale"));
    if (Number.isFinite(val)) opts.renderScale = clampRenderScale(val);
  }
  if (p.has("flowSpeed")) {
    const val = Number(p.get("flowSpeed"));
    if (Number.isFinite(val)) opts.flowSpeed = clampFlowSpeed(val);
  }
  if (p.has("wordFadeWidth")) {
    const val = Number(p.get("wordFadeWidth"));
    if (Number.isFinite(val)) opts.wordFadeWidth = clampWordFadeWidth(val);
  }
  if (p.has("alignPosition")) {
    const val = Number(p.get("alignPosition"));
    if (Number.isFinite(val)) opts.alignPosition = clampAlignPosition(val);
  }

  if (p.has("enableSpring")) opts.enableSpring = p.get("enableSpring") !== "false" && p.get("enableSpring") !== "0";
  if (p.has("enableBlur")) opts.enableBlur = p.get("enableBlur") !== "false" && p.get("enableBlur") !== "0";
  if (p.has("enableScale")) opts.enableScale = p.get("enableScale") !== "false" && p.get("enableScale") !== "0";
  if (p.has("hidePassedLines")) opts.hidePassedLines = p.get("hidePassedLines") === "true" || p.get("hidePassedLines") === "1";
  if (p.has("staticMode")) opts.staticMode = p.get("staticMode") === "true" || p.get("staticMode") === "1";

  if (p.has("title")) opts.title = p.get("title")!;
  if (p.has("artist")) opts.artist = p.get("artist")!;
  if (p.has("album")) opts.album = p.get("album")!;
  if (p.has("fontFamily")) opts.fontFamily = p.get("fontFamily")!;
  if (p.has("fontScale")) {
    const val = Number(p.get("fontScale"));
    if (Number.isFinite(val)) opts.fontScale = clampFontScale(val);
  }
  if (p.has("fontWeight")) {
    const val = Number(p.get("fontWeight"));
    if (Number.isFinite(val)) opts.fontWeight = clampFontWeight(val);
  }

  if (p.has("secondaryMode")) {
    const val = p.get("secondaryMode");
    if (val !== null) opts.secondaryMode = clampSecondaryMode(val);
  }
  if (p.has("showSongInfo")) opts.showSongInfo = p.get("showSongInfo") !== "false" && p.get("showSongInfo") !== "0";
  if (p.has("showArtist")) opts.showArtist = p.get("showArtist") !== "false" && p.get("showArtist") !== "0";
  if (p.has("showAlbum")) opts.showAlbum = p.get("showAlbum") !== "false" && p.get("showAlbum") !== "0";
  if (p.has("showArtwork")) opts.showArtwork = p.get("showArtwork") !== "false" && p.get("showArtwork") !== "0";
  if (p.has("showControls")) opts.showControls = p.get("showControls") === "true" || p.get("showControls") === "1";

  if (p.has("artworkScale")) {
    const val = Number(p.get("artworkScale"));
    if (Number.isFinite(val)) opts.artworkScale = clampArtworkScale(val);
  }
  if (p.has("lyricAlign")) {
    const val = p.get("lyricAlign");
    if (val !== null) opts.lyricAlign = clampLyricAlign(val);
  }
  if (p.has("lyricOffset")) {
    const val = Number(p.get("lyricOffset"));
    if (Number.isFinite(val)) opts.lyricOffset = clampLyricOffset(val);
  }
  if (p.has("lyricWidth")) {
    const val = Number(p.get("lyricWidth"));
    if (Number.isFinite(val)) opts.lyricWidth = clampLyricWidth(val);
  }
  if (p.has("lyricFade")) {
    const val = Number(p.get("lyricFade"));
    if (Number.isFinite(val)) opts.lyricFade = clampLyricFade(val);
  }

  if (p.has("backgroundDim")) {
    const val = Number(p.get("backgroundDim"));
    if (Number.isFinite(val)) opts.backgroundDim = clampBackgroundDim(val);
  }
  if (p.has("backgroundSaturation")) {
    const val = Number(p.get("backgroundSaturation"));
    if (Number.isFinite(val)) opts.backgroundSaturation = clampBackgroundSaturation(val);
  }
  if (p.has("backgroundBlur")) {
    const val = Number(p.get("backgroundBlur"));
    if (Number.isFinite(val)) opts.backgroundBlur = clampBackgroundBlur(val);
  }

  return opts;
}

export default function App() {
  const params = useMemo(() => new URLSearchParams(window.location.search), []);
  const autoMode = params.get("auto") === "1";
  const embeddedPreview = params.get("preview") === "1";
  const strictRenderMode = autoMode && !embeddedPreview;
  const chromeless = params.get("chrome") === "0";

  const [options, setOptionsState] = useState<RenderOptions>(() => {
    const urlOpts = parseUrlOptions(window.location.search);
    const initial: RenderOptions = {
      ...DEFAULT_RENDER_OPTIONS,
      ...urlOpts,
      showChrome: !autoMode && !chromeless,
      showControls: urlOpts.showControls ?? DEFAULT_RENDER_OPTIONS.showControls,
    };
    return clampOptions(initial);
  });

  const setOptions = useCallback((updater: Partial<RenderOptions> | ((prev: RenderOptions) => RenderOptions)) => {
    setOptionsState((prev) => {
      const next: RenderOptions = typeof updater === "function" ? updater(prev) : { ...prev, ...updater };
      return clampOptions(next);
    });
  }, []);

  /** 精确分辨率或预设 */
  const preset = useMemo(() => {
    const base = STAGE_PRESETS.find((p) => p.id === options.presetId) ?? STAGE_PRESETS[0];
    const w = Number(params.get("width"));
    const h = Number(params.get("height"));
    if (!w || !h || !Number.isFinite(w) || !Number.isFinite(h)) return base;
    return { ...base, width: Math.max(16, Math.round(w)), height: Math.max(16, Math.round(h)), fontVw: base.fontVw };
  }, [options.presetId, params]);

  const [track, setTrack] = useState<TrackAssets | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);

  const [playing, setPlaying] = useState(false);
  const [currentTime, setCurrentTime] = useState(0);
  const [duration, setDuration] = useState(0);
  const [isSeeking, setIsSeeking] = useState(false);

  const [recording, setRecording] = useState(false);
  const [recordSeconds, setRecordSeconds] = useState(0);
  const [recordNotice, setRecordNotice] = useState<string | null>(null);

  // 就绪状态细项
  const [readiness, setReadiness] = useState<StageReadyState>({
    assetsLoaded: false,
    audioMetaLoaded: false,
    fontLoaded: false,
    stageLayoutReady: false,
  });

  const stageRef = useRef<HTMLDivElement>(null);
  const viewportRef = useRef<HTMLDivElement>(null);
  const audioRef = useRef<HTMLAudioElement>(null);
  const bgRef = useRef<BackgroundRenderRef>(null);
  const recordStopRef = useRef<(() => Promise<void>) | null>(null);
  const [stageScale, setStageScale] = useState(1);

  // Web Audio 低频能量状态与安全管理
  const [audioLowFreqVolume, setAudioLowFreqVolume] = useState<number>(1.0);
  const audioContextRef = useRef<AudioContext | null>(null);
  const analyserRef = useRef<AnalyserNode | null>(null);
  const audioStreamRef = useRef<MediaStream | null>(null);
  const audioSourceConnectedRef = useRef(false);

  // 资源生命周期追踪：跟踪已创建的 Object URLs 以便在换歌或卸载时回收
  const managedObjectUrlsRef = useRef<Set<string>>(new Set());

  const registerObjectUrl = useCallback((url: string | null | undefined) => {
    if (url && url.startsWith("blob:")) {
      managedObjectUrlsRef.current.add(url);
    }
  }, []);

  const revokeAudioUrl = useCallback((url: string | null | undefined) => {
    if (url && url.startsWith("blob:")) {
      URL.revokeObjectURL(url);
      managedObjectUrlsRef.current.delete(url);
    }
  }, []);

  const revokeCoverUrl = useCallback((url: string | null | undefined) => {
    if (url && url.startsWith("blob:")) {
      URL.revokeObjectURL(url);
      managedObjectUrlsRef.current.delete(url);
    }
  }, []);

  // Web Audio 资源清理与重建函数
  const teardownWebAudio = useCallback(() => {
    if (audioStreamRef.current) {
      try {
        audioStreamRef.current.getTracks().forEach((t) => t.stop());
      } catch {
        // ignore
      }
      audioStreamRef.current = null;
    }
    if (analyserRef.current) {
      try {
        analyserRef.current.disconnect();
      } catch {
        // ignore
      }
      analyserRef.current = null;
    }
    if (audioContextRef.current) {
      try {
        void audioContextRef.current.close().catch(() => undefined);
      } catch {
        // ignore
      }
      audioContextRef.current = null;
    }
    audioSourceConnectedRef.current = false;
    setAudioLowFreqVolume(1.0);
  }, []);

  // 组件卸载时释放所有未回收的 Object URL 与 Web Audio
  useEffect(() => {
    return () => {
      teardownWebAudio();
      managedObjectUrlsRef.current.forEach((url) => {
        try {
          URL.revokeObjectURL(url);
        } catch {
          // ignore
        }
      });
      managedObjectUrlsRef.current.clear();
    };
  }, [teardownWebAudio]);

  /* ---------- 背景自适应与重绘保持 ---------- */
  const kickTokenRef = useRef(0);
  const isStaticBg = options.staticMode || options.flowSpeed === 0;

  const syncBackgroundSize = useCallback(
    (shouldPlay: boolean) => {
      const render = bgRef.current?.bgRender;
      if (!render) return;

      const token = ++kickTokenRef.current;
      const kick = () => {
        if (kickTokenRef.current !== token) return;
        render.pause();
        if (!isStaticBg) {
          render.resume();
        }
        if (!shouldPlay || isStaticBg) {
          window.setTimeout(() => {
            if (kickTokenRef.current === token) render.pause();
          }, 260);
        }
      };

      const frame = requestAnimationFrame(() => {
        kick();
        requestAnimationFrame(kick);
      });
      return () => cancelAnimationFrame(frame);
    },
    [isStaticBg],
  );

  // Apple Music 的背景在暂停/未播放时依然缓慢流动，只有静态模式才停住。
  const shouldPlayBackground = !isStaticBg;

  useEffect(() => {
    return syncBackgroundSize(shouldPlayBackground);
  }, [syncBackgroundSize, preset.width, preset.height, stageScale, shouldPlayBackground, track?.coverUrl]);

  /* ---------- Web Audio 低频分析器设置 ---------- */
  const setupWebAudio = useCallback(() => {
    const audio = audioRef.current;
    if (!audio || audioSourceConnectedRef.current) return;

    try {
      const AudioContextClass =
        window.AudioContext ||
        (window as unknown as { webkitAudioContext: typeof AudioContext }).webkitAudioContext;
      if (!AudioContextClass) return;

      const ctx = new AudioContextClass();
      const analyser = ctx.createAnalyser();
      analyser.fftSize = 256;
      analyser.smoothingTimeConstant = 0.8;

      // 关键安全点：只用 captureStream 取出的流接分析器，绝不调用 createMediaElementSource 污染 audio 元素
      const capture =
        (audio as HTMLAudioElement & { captureStream?: () => MediaStream; mozCaptureStream?: () => MediaStream })
          .captureStream ??
        (audio as HTMLAudioElement & { mozCaptureStream?: () => MediaStream }).mozCaptureStream;

      let stream: MediaStream | null = null;
      if (typeof capture === "function") {
        try {
          stream = capture.call(audio);
          const source = ctx.createMediaStreamSource(stream);
          source.connect(analyser);
          // 注意：MediaStreamSource 不需要连到 ctx.destination，否则会产生重复声音
          audioStreamRef.current = stream;
          audioContextRef.current = ctx;
          analyserRef.current = analyser;
          audioSourceConnectedRef.current = true;
          return;
        } catch {
          // captureStream 失败或创建 source 异常，进入下方清理
        }
      }

      // 若 captureStream 缺失或绑定失败，立即关闭刚创建但未保存的 AudioContext，并停止可能创建的流轨道
      if (stream) {
        try {
          (stream as MediaStream).getTracks().forEach((t) => t.stop());
        } catch {
          // ignore
        }
      }
      try {
        void ctx.close();
      } catch {
        // ignore
      }
    } catch {
      // 优雅降级，维持默认 1.0 能量
    }
  }, []);

  // 轮询低频能量（低频 20Hz ~ 200Hz）
  useEffect(() => {
    let animId = 0;
    const dataArray = new Uint8Array(32);

    const updateEnergy = () => {
      const analyser = analyserRef.current;
      const ctx = audioContextRef.current;
      if (analyser && ctx && ctx.state === "running" && playing && !isStaticBg) {
        analyser.getByteFrequencyData(dataArray);
        // 取前 4-6 个低频 bin 的平均能量
        let sum = 0;
        const count = Math.min(6, dataArray.length);
        for (let i = 0; i < count; i++) {
          sum += dataArray[i];
        }
        const avg = sum / (count * 255);
        // 映射到 0.4 ~ 1.8 波动
        const dynamicVol = 0.4 + avg * 1.4;
        setAudioLowFreqVolume(dynamicVol);
      } else if (!playing || isStaticBg) {
        setAudioLowFreqVolume(1.0);
      }
      animId = requestAnimationFrame(updateEnergy);
    };

    animId = requestAnimationFrame(updateEnergy);
    return () => cancelAnimationFrame(animId);
  }, [playing, isStaticBg]);

  /* ---------- 字体加载状态检测 ---------- */
  useEffect(() => {
    const family = options.fontFamily.trim();
    const fontSpec = family ? `16px ${family}` : "16px system-ui";
    if (document.fonts && typeof document.fonts.check === "function") {
      try {
        if (document.fonts.check(fontSpec)) {
          setReadiness((r) => ({ ...r, fontLoaded: true }));
        } else {
          document.fonts.ready
            .then(() => {
              setReadiness((r) => ({ ...r, fontLoaded: true }));
            })
            .catch(() => {
              setReadiness((r) => ({ ...r, fontLoaded: true }));
            });
        }
      } catch {
        setReadiness((r) => ({ ...r, fontLoaded: true }));
      }
    } else {
      setReadiness((r) => ({ ...r, fontLoaded: true }));
    }
  }, [options.fontFamily]);

  /* ---------- 舞台尺寸与缩放适配 ---------- */
  useEffect(() => {
    const viewport = viewportRef.current;
    if (!viewport) return;

    if (strictRenderMode) {
      // 离线渲染必须严格 1:1 像素对齐；嵌入式实时预览仍需按可用区域缩放。
      setStageScale(1);
      setReadiness((r) => ({ ...r, stageLayoutReady: true }));
      return;
    }

    const fit = () => {
      const rect = viewport.getBoundingClientRect();
      const pad = 24;
      const scale = Math.min((rect.width - pad) / preset.width, (rect.height - pad) / preset.height, 1);
      setStageScale(Math.max(0.05, scale));
      setReadiness((r) => ({ ...r, stageLayoutReady: true }));
    };

    fit();
    const observer = new ResizeObserver(fit);
    observer.observe(viewport);
    return () => observer.disconnect();
  }, [strictRenderMode, preset.width, preset.height]);

  /* ---------- 传输控制方法 ---------- */
  const togglePlay = useCallback(async () => {
    const audio = audioRef.current;
    if (!audio) return;
    if (audio.paused) {
      setupWebAudio();
      if (audioContextRef.current && audioContextRef.current.state === "suspended") {
        void audioContextRef.current.resume();
      }
      await audio.play().catch(() => undefined);
    } else {
      audio.pause();
    }
  }, [setupWebAudio]);

  const seekTo = useCallback((ms: number) => {
    const audio = audioRef.current;
    if (!audio) return;
    const clamped = Math.max(0, ms);
    const duration = Number.isFinite(audio.duration) && audio.duration > 0 ? audio.duration * 1000 : Infinity;
    const target = Math.min(clamped, duration);
    setIsSeeking(true);
    audio.currentTime = target / 1000;
    setCurrentTime(Math.round(target));
    // 等待音频完成 seeked 后再退出拖动状态，保证弹簧动画不被中途插值
    window.setTimeout(() => setIsSeeking(false), 160);
  }, []);

  const seekBy = useCallback(
    (deltaMs: number) => {
      const audio = audioRef.current;
      if (!audio) return;
      seekTo(audio.currentTime * 1000 + deltaMs);
    },
    [seekTo],
  );

  // 状态 Ref 用于自动化桥接的实时读取
  const trackRef = useRef<TrackAssets | null>(track);
  trackRef.current = track;
  const optionsRef = useRef<RenderOptions>(options);
  optionsRef.current = options;
  const currentTimeRef = useRef(currentTime);
  currentTimeRef.current = currentTime;
  const durationRef = useRef(duration);
  durationRef.current = duration;

  /* ---------- 自动化桥接 window.__amllMv 稳定单例维护 ---------- */
  useEffect(() => {
    // 保证 window.__amllMv 稳定单例，永远不被整块重新赋值丢失方法
    if (!window.__amllMv) {
      window.__amllMv = {
        ready: false,
        audioStartedAt: null,
        audioEndedAt: null,
        error: null,
        getTrack: () => trackRef.current,
        getOptions: () => optionsRef.current,
        setOptions: (opts: Partial<RenderOptions>) => {
          setOptions(opts);
        },
        play: async () => {
          const audio = audioRef.current;
          if (!audio) return;
          setupWebAudio();
          if (audioContextRef.current && audioContextRef.current.state === "suspended") {
            void audioContextRef.current.resume();
          }
          await audio.play().catch(() => undefined);
        },
        pause: () => {
          audioRef.current?.pause();
        },
        seek: (timeMs: number) => {
          seekTo(timeMs);
        },
        getCurrentTime: () => {
          const audio = audioRef.current;
          if (audio) return Math.round(audio.currentTime * 1000);
          return currentTimeRef.current;
        },
        getDuration: () => {
          const audio = audioRef.current;
          if (audio && Number.isFinite(audio.duration) && audio.duration > 0) {
            return Math.round(audio.duration * 1000);
          }
          return durationRef.current;
        },
      };
    } else {
      // 保持方法引用最新
      window.__amllMv.getTrack = () => trackRef.current;
      window.__amllMv.getOptions = () => optionsRef.current;
      window.__amllMv.setOptions = (opts: Partial<RenderOptions>) => setOptions(opts);
      window.__amllMv.play = async () => {
        const audio = audioRef.current;
        if (!audio) return;
        setupWebAudio();
        if (audioContextRef.current && audioContextRef.current.state === "suspended") {
          void audioContextRef.current.resume();
        }
        await audio.play().catch(() => undefined);
      };
      window.__amllMv.pause = () => audioRef.current?.pause();
      window.__amllMv.seek = (timeMs: number) => seekTo(timeMs);
      window.__amllMv.getCurrentTime = () => {
        const audio = audioRef.current;
        if (audio) return Math.round(audio.currentTime * 1000);
        return currentTimeRef.current;
      };
      window.__amllMv.getDuration = () => {
        const audio = audioRef.current;
        if (audio && Number.isFinite(audio.duration) && audio.duration > 0) {
          return Math.round(audio.duration * 1000);
        }
        return durationRef.current;
      };
    }

    const allReady =
      readiness.assetsLoaded &&
      readiness.audioMetaLoaded &&
      readiness.fontLoaded &&
      readiness.stageLayoutReady &&
      !loadError;

    window.__amllMv.ready = allReady;
    window.__amllMv.error = loadError;
    window.__amllMv.stageRect = {
      width: preset.width,
      height: preset.height,
      scale: stageScale,
      left: 0,
      top: 0,
    };
    window.__amllMv.readiness = readiness;
  }, [readiness, loadError, preset.width, preset.height, stageScale, setOptions, setupWebAudio, seekTo]);

  /* ---------- 素材加载与应用 ---------- */
  const applyAssets = useCallback(
    async (input: { audioFile?: File | null; lyricFile?: File | null; coverFile?: File | null }) => {
      setLoadError(null);
      setReadiness((r) => ({ ...r, assetsLoaded: false, audioMetaLoaded: false }));

      try {
        // 读 trackRef 而非闭包捕获的 track：并发调用（如连续拖入素材）时，
        // 后一次调用必须看到前一次已提交的 URL，否则会复用被回收的 Object URL。
        const prev = trackRef.current;
        const audioFile = input.audioFile ?? null;
        const lyricFile = input.lyricFile ?? null;
        const coverFile = input.coverFile ?? null;

        if (!audioFile && !lyricFile && !coverFile && !prev) return;

        let lyricLines: LyricLine[] = prev?.lyricLines ?? [];
        let lyricFormat = prev?.lyricFormat ?? "";
        let metadata: [string, string[]][] = [];

        if (lyricFile) {
          const text = await lyricFile.text();
          const parsed = parseLyricText(text, lyricFile.name);
          lyricLines = parsed.lines;
          lyricFormat = parsed.format;
          metadata = parsed.metadata;
        }

        // 如果更换了音频，先清理既有 Web Audio 节点，避免重复音频或流错位
        if (audioFile && prev) {
          teardownWebAudio();
        }

        // 回收旧资源 Object URL：只回收被更换素材的旧 URL，未替换素材保持有效
        if (audioFile && prev?.audioUrl) {
          revokeAudioUrl(prev.audioUrl);
        }
        if (coverFile && prev?.coverUrl) {
          revokeCoverUrl(prev.coverUrl);
        }

        const audioUrl = audioFile ? URL.createObjectURL(audioFile) : prev?.audioUrl ?? "";
        registerObjectUrl(audioUrl);

        let coverUrl = prev?.coverUrl ?? null;
        if (coverFile) {
          coverUrl = URL.createObjectURL(coverFile);
          registerObjectUrl(coverUrl);
        }

        const songTitle =
          options.title || readMeta(metadata, "ti") || (audioFile ? audioFile.name.replace(/\.[^.]+$/, "") : prev?.title ?? "未命名歌曲");
        const songArtist = options.artist || readMeta(metadata, "ar") || prev?.artist || "未知艺术家";
        const songAlbum = options.album || readMeta(metadata, "al") || prev?.album || "";

        const nextTrack = {
          audioUrl,
          coverUrl,
          rawLyricLines: lyricLines,
          lyricLines,
          lyricFormat,
          title: songTitle,
          artist: songArtist,
          album: songAlbum,
        };
        // 同步写回 ref，防止同一次渲染周期内的连续调用读到已撤销的旧 URL
        trackRef.current = nextTrack;
        setTrack(nextTrack);

        setCurrentTime(0);
        setReadiness((r) => ({ ...r, assetsLoaded: true }));
      } catch (err) {
        const message = err instanceof Error ? err.message : String(err);
        setLoadError(message);
        setReadiness((r) => ({ ...r, assetsLoaded: false }));
      }
    },
    [options.title, options.artist, options.album, registerObjectUrl, teardownWebAudio],
  );

  /** 自动模式拉取素材 */
  useEffect(() => {
    if (!autoMode) return;
    const audioUrl = params.get("audio");
    const lyricUrl = params.get("lyric");
    const coverUrl = params.get("cover");

    if (!audioUrl) {
      const message = "自动模式缺少 audio 参数";
      setLoadError(message);
      return;
    }

    let isSubscribed = true;
    void (async () => {
      try {
        const audioRes = await fetch(audioUrl);
        if (!audioRes.ok) throw new Error(`拉取音频失败: HTTP ${audioRes.status}`);
        const audioBlob = await audioRes.blob();
        const audioFile = new File([audioBlob], "auto-audio", {
          type: audioRes.headers.get("content-type") ?? "audio/mpeg",
        });

        let lyricFile: File | null = null;
        if (lyricUrl) {
          const lyricRes = await fetch(lyricUrl);
          if (lyricRes.ok) {
            const lyricBlob = await lyricRes.blob();
            lyricFile = new File([lyricBlob], lyricUrl.split("/").pop() || "auto-lyrics.lrc", {
              type: "text/plain",
            });
          }
        }

        let coverFile: File | null = null;
        if (coverUrl) {
          const coverRes = await fetch(coverUrl);
          if (coverRes.ok) {
            const coverBlob = await coverRes.blob();
            coverFile = new File([coverBlob], coverUrl.split("/").pop() || "auto-cover.jpg", {
              type: coverRes.headers.get("content-type") ?? "image/jpeg",
            });
          }
        }

        if (isSubscribed) {
          await applyAssets({ audioFile, lyricFile, coverFile });
        }
      } catch (err) {
        if (!isSubscribed) return;
        const message = err instanceof Error ? err.message : String(err);
        setLoadError(message);
      }
    })();

    return () => {
      isSubscribed = false;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [autoMode]);

  /* ---------- 播放事件与状态同步 ---------- */
  useEffect(() => {
    const audio = audioRef.current;
    if (!audio) return;
    let frameId = 0;
    const tick = () => {
      if (!audio.paused) setCurrentTime(Math.round(audio.currentTime * 1000));
      frameId = requestAnimationFrame(tick);
    };
    frameId = requestAnimationFrame(tick);
    return () => cancelAnimationFrame(frameId);
  }, [track?.audioUrl]);

  useEffect(() => {
    const audio = audioRef.current;
    if (!audio || !track) return;

    const onLoaded = () => {
      setDuration(Number.isFinite(audio.duration) ? audio.duration * 1000 : 0);
      setReadiness((r) => ({ ...r, audioMetaLoaded: true }));
      setupWebAudio();
    };

    const onPlay = () => {
      setPlaying(true);
      setupWebAudio();
      if (audioContextRef.current && audioContextRef.current.state === "suspended") {
        void audioContextRef.current.resume();
      }
      if (window.__amllMv && window.__amllMv.audioStartedAt === null) {
        // 使用 performance.timeOrigin + performance.now() 获取单调的高精度 Epoch 毫秒
        const monotonicNow =
          typeof performance !== "undefined" && typeof performance.timeOrigin === "number"
            ? performance.timeOrigin + performance.now()
            : Date.now();
        window.__amllMv.audioStartedAt = monotonicNow;
      }
    };

    const onPause = () => setPlaying(false);
    const onEnded = () => {
      setPlaying(false);
      if (window.__amllMv) {
        const monotonicNow =
          typeof performance !== "undefined" && typeof performance.timeOrigin === "number"
            ? performance.timeOrigin + performance.now()
            : Date.now();
        window.__amllMv.audioEndedAt = monotonicNow;
      }
    };
    const onSeeked = () => setCurrentTime(Math.round(audio.currentTime * 1000));

    audio.addEventListener("loadedmetadata", onLoaded);
    audio.addEventListener("durationchange", onLoaded);
    audio.addEventListener("play", onPlay);
    audio.addEventListener("playing", onPlay);
    audio.addEventListener("pause", onPause);
    audio.addEventListener("ended", onEnded);
    audio.addEventListener("seeked", onSeeked);

    if (audio.readyState >= 1) onLoaded();

    return () => {
      audio.removeEventListener("loadedmetadata", onLoaded);
      audio.removeEventListener("durationchange", onLoaded);
      audio.removeEventListener("play", onPlay);
      audio.removeEventListener("playing", onPlay);
      audio.removeEventListener("pause", onPause);
      audio.removeEventListener("ended", onEnded);
      audio.removeEventListener("seeked", onSeeked);
    };
  }, [track, setupWebAudio]);

  /** 自动播放 */
  useEffect(() => {
    if (!autoMode || params.get("autoplay") !== "1") return;
    const audio = audioRef.current;
    if (!audio || !track) return;
    setupWebAudio();
    void audio.play().catch(() => undefined);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [track, autoMode, setupWebAudio]);

  /* ---------- 快捷键 ---------- */
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      const target = e.target as HTMLElement | null;
      if (target && (target.tagName === "INPUT" || target.tagName === "TEXTAREA" || target.tagName === "SELECT")) {
        return;
      }
      if (e.code === "Space") {
        e.preventDefault();
        void togglePlay();
      } else if (e.code === "ArrowLeft") {
        e.preventDefault();
        seekBy(-5000);
      } else if (e.code === "ArrowRight") {
        e.preventDefault();
        seekBy(5000);
      } else if (e.code === "Home") {
        e.preventDefault();
        seekTo(0);
      }
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [togglePlay, seekBy, seekTo]);

  /* ---------- 舞台录制 ---------- */
  const handleToggleRecording = useCallback(async () => {
    if (recording) {
      await recordStopRef.current?.();
      return;
    }
    const stage = stageRef.current;
    if (!stage) return;

    try {
      const audio = audioRef.current ?? undefined;
      const handle: StageRecorderHandle = await startStageRecording(stage, {
        fps: Math.min(60, Math.max(15, options.fps)),
        audioElements: audio ? [audio] : [],
        videoBitsPerSecond: 16_000_000,
      });

      recordStopRef.current = async () => {
        const blob = await handle.stop();
        setRecording(false);
        recordStopRef.current = null;
        const song = track?.title || "amll-mv";
        const ext = blob.type.includes("mp4") ? "mp4" : "webm";
        downloadBlob(blob, `${song}-${preset.id}.${ext}`);
        setRecordNotice(`录制完成，已触发下载`);
        window.setTimeout(() => setRecordNotice(null), 4000);
      };
      setRecording(true);
      setRecordSeconds(0);
    } catch (err) {
      const message = err instanceof Error ? err.message : String(err);
      setRecordNotice(`启动录制失败: ${message}`);
      window.setTimeout(() => setRecordNotice(null), 5000);
    }
  }, [recording, options.fps, track?.title, preset.id]);

  // 录制计时器
  useEffect(() => {
    if (!recording) return;
    const t = window.setInterval(() => setRecordSeconds((s) => s + 1), 1000);
    return () => window.clearInterval(t);
  }, [recording]);

  // 派生副歌词
  const activeLyricLines = useMemo(() => {
    const raw = track?.rawLyricLines ?? track?.lyricLines;
    if (!raw) return [];
    return deriveSecondaryLyrics(raw, options.secondaryMode);
  }, [track?.rawLyricLines, track?.lyricLines, options.secondaryMode]);

  const lyricEnd = useMemo(() => getLyricDuration(activeLyricLines), [activeLyricLines]);
  const hasLyric = activeLyricLines.length > 0;

  // 歌词字体尺寸计算：根据预设的 fontVw 和舞台基准宽度换算
  const lyricFontPx = useMemo(() => {
    const basePx = (preset.width * preset.fontVw) / 100;
    return Math.round(basePx * options.fontScale);
  }, [preset.width, preset.fontVw, options.fontScale]);

  const displayTitle = options.title || track?.title || "未命名歌曲";
  const displayArtist = options.artist || track?.artist || "未知艺术家";
  const displayAlbum = options.album || track?.album || "";

  // 缺省封面按曲目信息生成稳定占位图；同一曲目在实时改标题时不会反复换配色。
  const albumArtUrl = useMemo(() => {
    const seed = track ? `${track.title}/${track.artist}` : "amll-ambient";
    try {
      return track?.coverUrl || createFallbackCover(seed);
    } catch {
      return track?.coverUrl || "";
    }
  }, [track?.coverUrl, track?.title, track?.artist, track]);

  // 注入到 stage 根节点的 CSS 自定义属性
  // backgroundSaturation 规范为 0..2，CSS 滤镜需百分比表示（例如 1.0 -> 100%）
  // lyricFade 为舞台高度比例，换算成舞台坐标系内的像素（.stage-lyrics 的 mask 使用）
  const stageInlineStyle: CSSProperties = {
    width: preset.width,
    height: preset.height,
    transform: `scale(${stageScale})`,
    fontSize: `${lyricFontPx}px`,
    fontFamily: options.fontFamily.trim() || undefined,
    fontWeight: options.fontWeight,
    ["--stage-bg-dim" as string]: options.backgroundDim,
    ["--stage-bg-saturation" as string]: `${Math.round(options.backgroundSaturation * 100)}%`,
    ["--stage-bg-blur" as string]: `${options.backgroundBlur}px`,
    ["--artwork-scale" as string]: options.artworkScale,
    ["--lyric-fade" as string]: `${Math.round(preset.height * options.lyricFade)}px`,
  };

  const lyricContainerStyle: CSSProperties = {
    ["--amll-lp-font-size" as string]: `${lyricFontPx}px`,
    textAlign: options.lyricAlign,
    transform: options.lyricOffset !== 0 ? `translateY(${options.lyricOffset}px)` : undefined,
    maxWidth: options.lyricWidth < 100 ? `${options.lyricWidth}%` : undefined,
  };

  return (
    <div className={`app ${options.showChrome ? "" : "app--bare"}`}>
      {options.showChrome ? (
        <ControlPanel
          options={options}
          onChange={setOptions}
          track={track}
          loadError={loadError}
          onPickFiles={(files) =>
            applyAssets({ audioFile: files.audio, lyricFile: files.lyric, coverFile: files.cover })
          }
          onClearError={() => setLoadError(null)}
          lyricEnd={lyricEnd}
          onSeek={seekTo}
        />
      ) : null}

      <main className="workspace">
        <div className="viewport" ref={viewportRef}>
          <div
            className="stage-frame"
            style={{
              width: strictRenderMode ? preset.width : preset.width * stageScale,
              height: strictRenderMode ? preset.height : preset.height * stageScale,
            }}
          >
            <div
              className="stage"
              ref={stageRef}
              data-amll-stage=""
              data-width={preset.width}
              data-height={preset.height}
              style={stageInlineStyle}
            >
              {/* 全屏动态 MeshGradient 背景 */}
              <BackgroundRender
                ref={bgRef}
                className="stage-bg"
                album={albumArtUrl || undefined}
                playing={!isStaticBg}
                fps={options.fps}
                flowSpeed={options.flowSpeed}
                renderScale={options.renderScale}
                staticMode={isStaticBg}
                hasLyric={hasLyric}
                lowFreqVolume={audioLowFreqVolume}
                renderer={MeshGradientRenderer}
              />

              {/* 背景暗化与模糊遮罩层 */}
              <div className="stage-bg-dimmer" />

              {/* 舞台主要内容：左侧信息 + 右侧歌词 */}
              <div className={`stage-content stage-content--${getStageLayoutClass(preset.id)}`}>
                {/* 封面与曲目信息 */}
                {options.showSongInfo || options.showArtwork ? (
                  <div className="stage-left">
                    {options.showArtwork ? (
                      <div className="stage-artwork-wrapper">
                        {albumArtUrl ? (
                          <img src={albumArtUrl} alt="封面" className="stage-artwork-img" />
                        ) : (
                          <div className="stage-artwork-placeholder" />
                        )}
                      </div>
                    ) : null}

                    {options.showSongInfo ? (
                      <div className="stage-info">
                        <h1 className="stage-info-title">{displayTitle}</h1>
                        {options.showArtist && displayArtist ? (
                          <p className="stage-info-artist">{displayArtist}</p>
                        ) : null}
                        {options.showAlbum && displayAlbum ? (
                          <p className="stage-info-album">{displayAlbum}</p>
                        ) : null}
                      </div>
                    ) : null}
                  </div>
                ) : null}

                {/* 歌词视口 */}
                <div className="stage-right" style={lyricContainerStyle}>
                  {hasLyric ? (
                    <LyricPlayer
                      className="stage-lyrics"
                      lyricLines={activeLyricLines}
                      currentTime={currentTime}
                      playing={playing}
                      isSeeking={isSeeking}
                      alignPosition={options.alignPosition}
                      wordFadeWidth={options.wordFadeWidth}
                      enableSpring={options.enableSpring}
                      enableBlur={options.enableBlur}
                      enableScale={options.enableScale}
                      hidePassedLines={options.hidePassedLines}
                    />
                  ) : (
                    <div className="stage-lyrics stage-lyrics--empty">
                      {track ? "此歌曲未包含歌词内容" : "请载入音频与歌词文件"}
                    </div>
                  )}
                </div>
              </div>

              {/* 音频标签（不可见，保留原生播放器时间线驱动） */}
              {track?.audioUrl ? (
                <audio
                  ref={audioRef}
                  src={track.audioUrl}
                  className="stage-audio"
                  preload="auto"
                  crossOrigin="anonymous"
                />
              ) : null}
            </div>
          </div>
        </div>

        {/* 底部播放进度条与录制控制 */}
        {options.showControls ? (
          <footer className="transport">
            <button
              className="transport-btn"
              onClick={() => void togglePlay()}
              disabled={!track}
              title={playing ? "暂停 (Space)" : "播放 (Space)"}
            >
              {playing ? "❚❚" : "▶"}
            </button>

            <span className="transport-time">{formatClock(currentTime)}</span>

            <input
              type="range"
              className="transport-scrubber"
              min={0}
              max={Math.max(duration, lyricEnd, 1000)}
              value={isSeeking ? undefined : currentTime}
              onChange={(e) => seekTo(Number(e.target.value))}
              disabled={!track}
              aria-label="播放进度"
            />

            <span className="transport-time transport-time--total">
              {formatClock(Math.max(duration, lyricEnd))}
            </span>

            <button
              className={`transport-btn ${recording ? "transport-btn--recording" : ""}`}
              onClick={() => void handleToggleRecording()}
              disabled={!track}
            >
              {recording ? `■ 停止录制 ${formatClock(recordSeconds * 1000)}` : "● 录制舞台"}
            </button>
          </footer>
        ) : null}

        {recordNotice ? (
          <div className="toast" onClick={() => setRecordNotice(null)}>
            {recordNotice}
          </div>
        ) : null}
      </main>
    </div>
  );
}
