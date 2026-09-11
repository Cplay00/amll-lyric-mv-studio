import { useId, useRef, type ChangeEvent } from "react";
import {
  STAGE_PRESETS,
  FONT_PRESETS,
  clampLyricOffset,
  clampLyricWidth,
  clampBackgroundSaturation,
  clampFlowSpeed,
  clampBackgroundDim,
  clampBackgroundBlur,
  clampFontScale,
  clampFontWeight,
  clampArtworkScale,
  clampFps,
  clampRenderScale,
  clampWordFadeWidth,
  clampAlignPosition,
  type RenderOptions,
  type TrackAssets,
} from "../lib/types";

interface ControlPanelProps {
  options: RenderOptions;
  onChange: (patch: Partial<RenderOptions>) => void;
  track: TrackAssets | null;
  loadError: string | null;
  onPickFiles: (files: { audio?: File; lyric?: File; cover?: File }) => void;
  onClearError: () => void;
  lyricEnd: number;
  onSeek: (timeMs: number) => void;
}

export default function ControlPanel({
  options,
  onChange,
  track,
  loadError,
  onPickFiles,
  onClearError,
}: ControlPanelProps) {
  const audioInputId = useId();
  const lyricInputId = useId();
  const coverInputId = useId();

  const audioInputRef = useRef<HTMLInputElement>(null);
  const lyricInputRef = useRef<HTMLInputElement>(null);
  const coverInputRef = useRef<HTMLInputElement>(null);

  const handleAudioChange = (e: ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0];
    if (file) onPickFiles({ audio: file });
  };

  const handleLyricChange = (e: ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0];
    if (file) onPickFiles({ lyric: file });
  };

  const handleCoverChange = (e: ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0];
    if (file) onPickFiles({ cover: file });
  };

  return (
    <aside className="panel" aria-label="MV 渲染工作台控制台">
      <header className="panel-header">
        <div className="panel-title-group">
          <span className="panel-badge">AMLL</span>
          <h2 className="panel-title">MV 渲染工作台</h2>
        </div>
        <p className="panel-sub">Apple Music 风格动态歌词 MV 生成环境</p>
      </header>

      <div className="panel-scroll">
        {loadError ? (
          <div className="panel-alert" role="alert">
            <div className="panel-alert-content">
              <strong>素材加载提示</strong>
              <p>{loadError}</p>
            </div>
            <button className="panel-alert-close" onClick={onClearError} title="关闭提示">
              ×
            </button>
          </div>
        ) : null}

        {/* 1. 素材导入 */}
        <section className="panel-sec">
          <h3 className="panel-sec-title">素材文件导入</h3>
          <div className="file-grid">
            <label className={`file-drop ${track?.audioUrl ? "file-drop--loaded" : ""}`} htmlFor={audioInputId}>
              <span className="file-drop-icon">🎵</span>
              <span className="file-drop-name">{track ? "音频已载入" : "载入音频文件"}</span>
              <span className="file-drop-hint">MP3 / FLAC / WAV / M4A</span>
              <input
                id={audioInputId}
                ref={audioInputRef}
                type="file"
                accept="audio/*,.mp3,.flac,.wav,.m4a,.ogg"
                onChange={handleAudioChange}
              />
            </label>

            <label className={`file-drop ${track?.rawLyricLines?.length ? "file-drop--loaded" : ""}`} htmlFor={lyricInputId}>
              <span className="file-drop-icon">📝</span>
              <span className="file-drop-name">{track?.rawLyricLines?.length ? "歌词已载入" : "载入歌词文件（可选）"}</span>
              <span className="file-drop-hint">LRC / TTML / QRC / YRC / JSON</span>
              <input
                id={lyricInputId}
                ref={lyricInputRef}
                type="file"
                accept=".lrc,.ttml,.xml,.qrc,.yrc,.json,.txt"
                onChange={handleLyricChange}
              />
            </label>

            <label className={`file-drop ${track?.coverUrl ? "file-drop--loaded" : ""}`} htmlFor={coverInputId}>
              <span className="file-drop-icon">🖼️</span>
              <span className="file-drop-name">{track?.coverUrl ? "封面已就绪" : "载入封面图像（可选）"}</span>
              <span className="file-drop-hint">JPG / PNG / WebP（可选）</span>
              <input
                id={coverInputId}
                ref={coverInputRef}
                type="file"
                accept="image/*"
                onChange={handleCoverChange}
              />
            </label>
          </div>
        </section>

        {/* 2. 舞台画布预设 */}
        <section className="panel-sec">
          <h3 className="panel-sec-title">画布预设分辨率</h3>
          <div className="preset-grid">
            {STAGE_PRESETS.map((p) => (
              <button
                key={p.id}
                type="button"
                className={`preset-chip ${options.presetId === p.id ? "preset-chip--active" : ""}`}
                onClick={() => onChange({ presetId: p.id })}
              >
                <span className="preset-chip-title">{p.label}</span>
                <span className="preset-chip-desc">{p.width} × {p.height}</span>
              </button>
            ))}
          </div>
        </section>

        {/* 3. 字体与曲目信息 */}
        <section className="panel-sec">
          <h3 className="panel-sec-title">曲目元信息与字体</h3>
          <div className="panel-fields">
            <div className="field">
              <label className="field-label">曲目标题（必填）</label>
              <input
                type="text"
                className="field-input"
                placeholder={track?.title || "未命名曲目"}
                value={options.title ?? ""}
                onChange={(e) => onChange({ title: e.target.value })}
              />
            </div>

            <div className="field-row">
              <div className="field">
                <label className="field-label">艺术家（可选）</label>
                <input
                  type="text"
                  className="field-input"
                  placeholder={track?.artist || "未知艺术家"}
                  value={options.artist ?? ""}
                  onChange={(e) => onChange({ artist: e.target.value })}
                />
              </div>
              <div className="field">
                <label className="field-label">专辑名（可选）</label>
                <input
                  type="text"
                  className="field-input"
                  placeholder={track?.album || "专辑"}
                  value={options.album ?? ""}
                  onChange={(e) => onChange({ album: e.target.value })}
                />
              </div>
            </div>

            <div className="field">
              <label className="field-label">歌词与文本字体族</label>
              <div className="font-chips">
                {FONT_PRESETS.map((f) => (
                  <button
                    key={f.id}
                    type="button"
                    className={`font-chip ${options.fontFamily === f.family ? "font-chip--active" : ""}`}
                    onClick={() => onChange({ fontFamily: f.family })}
                  >
                    {f.label}
                  </button>
                ))}
              </div>
              <input
                type="text"
                className="field-input"
                style={{ marginTop: 8 }}
                placeholder="自定义 CSS font-family 字符串"
                value={options.fontFamily ?? ""}
                onChange={(e) => onChange({ fontFamily: e.target.value })}
              />
            </div>

            <div className="field-row">
              <div className="field">
                <div className="field-split">
                  <label className="field-label">字号缩放</label>
                  <span className="field-val">{options.fontScale.toFixed(2)}x</span>
                </div>
                <input
                  type="range"
                  min={0.5}
                  max={2.0}
                  step={0.05}
                  value={options.fontScale}
                  onChange={(e) => onChange({ fontScale: clampFontScale(Number(e.target.value)) })}
                />
              </div>

              <div className="field">
                <div className="field-split">
                  <label className="field-label">字重 (Font Weight)</label>
                  <span className="field-val">{options.fontWeight}</span>
                </div>
                <input
                  type="range"
                  min={100}
                  max={900}
                  step={100}
                  value={options.fontWeight}
                  onChange={(e) => onChange({ fontWeight: clampFontWeight(Number(e.target.value)) })}
                />
              </div>
            </div>
          </div>
        </section>

        {/* 4. 歌词行为与排版 */}
        <section className="panel-sec">
          <h3 className="panel-sec-title">歌词行为与排版</h3>
          <div className="panel-fields">
            <div className="field">
              <label className="field-label">第二歌词行（翻译 / 音译）</label>
              <select
                className="field-select"
                value={options.secondaryMode}
                onChange={(e) => onChange({ secondaryMode: e.target.value as RenderOptions["secondaryMode"] })}
              >
                <option value="none">仅主歌词 (none)</option>
                <option value="translation">显示翻译 (translation)</option>
                <option value="roman">显示音译/罗马音 (roman)</option>
                <option value="both">同时显示翻译与音译 (both)</option>
              </select>
            </div>

            <div className="field-row">
              <div className="field">
                <label className="field-label">歌词对齐方式</label>
                <div className="seg-group">
                  {(["left", "center", "right"] as const).map((align) => (
                    <button
                      key={align}
                      type="button"
                      className={`seg-btn ${options.lyricAlign === align ? "seg-btn--active" : ""}`}
                      onClick={() => onChange({ lyricAlign: align })}
                    >
                      {align === "left" ? "居左" : align === "center" ? "居中" : "居右"}
                    </button>
                  ))}
                </div>
              </div>

              <div className="field">
                <div className="field-split">
                  <label className="field-label">歌词区最大宽度 (%)</label>
                  <span className="field-val">{Math.round(options.lyricWidth)}%</span>
                </div>
                <input
                  type="range"
                  min={30}
                  max={80}
                  step={1}
                  value={options.lyricWidth}
                  onChange={(e) => onChange({ lyricWidth: clampLyricWidth(Number(e.target.value)) })}
                />
              </div>
            </div>

            <div className="field">
              <div className="field-split">
                <label className="field-label">歌词垂直位移 (像素 -600px ~ 600px)</label>
                <span className="field-val">{options.lyricOffset}px</span>
              </div>
              <input
                type="range"
                min={-600}
                max={600}
                step={5}
                value={options.lyricOffset}
                onChange={(e) => onChange({ lyricOffset: clampLyricOffset(Number(e.target.value)) })}
              />
              <span className="field-desc">调整歌词在舞台垂直方向上的像素偏移行程，并非时间偏差</span>
            </div>

            <div className="field-row">
              <div className="field">
                <div className="field-split">
                  <label className="field-label">当前行锚定垂直位</label>
                  <span className="field-val">{Math.round(options.alignPosition * 100)}%</span>
                </div>
                <input
                  type="range"
                  min={0.1}
                  max={0.9}
                  step={0.02}
                  value={options.alignPosition}
                  onChange={(e) => onChange({ alignPosition: clampAlignPosition(Number(e.target.value)) })}
                />
              </div>

              <div className="field">
                <div className="field-split">
                  <label className="field-label">逐字渐变宽度</label>
                  <span className="field-val">{options.wordFadeWidth}</span>
                </div>
                <input
                  type="range"
                  min={0}
                  max={60}
                  step={2}
                  value={options.wordFadeWidth}
                  onChange={(e) => onChange({ wordFadeWidth: clampWordFadeWidth(Number(e.target.value)) })}
                />
              </div>
            </div>

            <div className="toggle-grid">
              <label className="toggle-item">
                <input
                  type="checkbox"
                  checked={options.enableSpring}
                  onChange={(e) => onChange({ enableSpring: e.target.checked })}
                />
                <span>弹性物理滚动</span>
              </label>

              <label className="toggle-item">
                <input
                  type="checkbox"
                  checked={options.enableBlur}
                  onChange={(e) => onChange({ enableBlur: e.target.checked })}
                />
                <span>非当前行景深模糊</span>
              </label>

              <label className="toggle-item">
                <input
                  type="checkbox"
                  checked={options.enableScale}
                  onChange={(e) => onChange({ enableScale: e.target.checked })}
                />
                <span>当前行字号放大缩放</span>
              </label>

              <label className="toggle-item">
                <input
                  type="checkbox"
                  checked={options.hidePassedLines}
                  onChange={(e) => onChange({ hidePassedLines: e.target.checked })}
                />
                <span>隐藏已唱过歌词</span>
              </label>
            </div>
          </div>
        </section>

        {/* 5. 动态背景与视觉滤镜 */}
        <section className="panel-sec">
          <h3 className="panel-sec-title">动态背景与视觉滤镜</h3>
          <div className="panel-fields">
            <div className="field-row">
              <div className="field">
                <div className="field-split">
                  <label className="field-label">背景流动速度</label>
                  <span className="field-val">{options.flowSpeed.toFixed(1)}x {options.flowSpeed === 0 ? "(静止)" : ""}</span>
                </div>
                <input
                  type="range"
                  min={0}
                  max={5.0}
                  step={0.1}
                  value={options.flowSpeed}
                  onChange={(e) => onChange({ flowSpeed: clampFlowSpeed(Number(e.target.value)) })}
                />
              </div>

              <div className="field">
                <div className="field-split">
                  <label className="field-label">背景暗化遮罩</label>
                  <span className="field-val">{Math.round(options.backgroundDim * 100)}%</span>
                </div>
                <input
                  type="range"
                  min={0}
                  max={0.9}
                  step={0.02}
                  value={options.backgroundDim}
                  onChange={(e) => onChange({ backgroundDim: clampBackgroundDim(Number(e.target.value)) })}
                />
              </div>
            </div>

            <div className="field-row">
              <div className="field">
                <div className="field-split">
                  <label className="field-label">背景饱和度比例</label>
                  <span className="field-val">{options.backgroundSaturation.toFixed(2)}x ({Math.round(options.backgroundSaturation * 100)}%)</span>
                </div>
                <input
                  type="range"
                  min={0}
                  max={2.0}
                  step={0.05}
                  value={options.backgroundSaturation}
                  onChange={(e) => onChange({ backgroundSaturation: clampBackgroundSaturation(Number(e.target.value)) })}
                />
              </div>

              <div className="field">
                <div className="field-split">
                  <label className="field-label">背景模糊度</label>
                  <span className="field-val">{options.backgroundBlur}px</span>
                </div>
                <input
                  type="range"
                  min={0}
                  max={80}
                  step={2}
                  value={options.backgroundBlur}
                  onChange={(e) => onChange({ backgroundBlur: clampBackgroundBlur(Number(e.target.value)) })}
                />
              </div>
            </div>

            <div className="field-row">
              <div className="field">
                <div className="field-split">
                  <label className="field-label">背景采样倍率</label>
                  <span className="field-val">{options.renderScale.toFixed(2)}x</span>
                </div>
                <input
                  type="range"
                  min={0.25}
                  max={1.0}
                  step={0.05}
                  value={options.renderScale}
                  onChange={(e) => onChange({ renderScale: clampRenderScale(Number(e.target.value)) })}
                />
              </div>

              <div className="field">
                <div className="field-split">
                  <label className="field-label">背景目标帧率</label>
                  <span className="field-val">{options.fps} FPS</span>
                </div>
                <input
                  type="range"
                  min={15}
                  max={60}
                  step={1}
                  value={options.fps}
                  onChange={(e) => onChange({ fps: clampFps(Number(e.target.value)) })}
                />
              </div>
            </div>

            <div className="toggle-grid">
              <label className="toggle-item">
                <input
                  type="checkbox"
                  checked={options.staticMode || options.flowSpeed === 0}
                  onChange={(e) => onChange({ staticMode: e.target.checked })}
                />
                <span>静态背景模式 (0流动)</span>
              </label>

              <label className="toggle-item">
                <input
                  type="checkbox"
                  checked={options.showArtwork}
                  onChange={(e) => onChange({ showArtwork: e.target.checked })}
                />
                <span>显示专辑封面</span>
              </label>

              <label className="toggle-item">
                <input
                  type="checkbox"
                  checked={options.showSongInfo}
                  onChange={(e) => onChange({ showSongInfo: e.target.checked })}
                />
                <span>显示曲目信息</span>
              </label>

              <label className="toggle-item">
                <input
                  type="checkbox"
                  checked={options.showControls}
                  onChange={(e) => onChange({ showControls: e.target.checked })}
                />
                <span>显示底部控制条</span>
              </label>
            </div>

            {options.showArtwork ? (
              <div className="field" style={{ marginTop: 8 }}>
                <div className="field-split">
                  <label className="field-label">专辑封面缩放比</label>
                  <span className="field-val">{options.artworkScale.toFixed(2)}x</span>
                </div>
                <input
                  type="range"
                  min={0.5}
                  max={1.5}
                  step={0.05}
                  value={options.artworkScale}
                  onChange={(e) => onChange({ artworkScale: clampArtworkScale(Number(e.target.value)) })}
                />
              </div>
            ) : null}
          </div>
        </section>
      </div>
    </aside>
  );
}
