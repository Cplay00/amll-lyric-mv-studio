import {
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
  clampBackgroundDim,
  clampBackgroundSaturation,
  clampBackgroundBlur,
  clampOptions,
  getStageLayoutClass,
  DEFAULT_RENDER_OPTIONS,
  type RenderOptions,
} from "./types.ts";

/**
 * 纯函数单元测试套件：验证规范参数边界与归一化逻辑
 * 可在 Node (ts-node / tsx / 打包阶段) 或浏览器直接执行
 */
export function runUnitTests(): { passed: number; total: number; errors: string[] } {
  const errors: string[] = [];
  let passed = 0;
  let total = 0;

  function assert(condition: boolean, msg: string) {
    total++;
    if (condition) {
      passed++;
    } else {
      errors.push(`Assertion failed: ${msg}`);
    }
  }

  // 1. secondaryMode: none | translation | roman | both
  assert(clampSecondaryMode("none") === "none", "secondaryMode none");
  assert(clampSecondaryMode("translation") === "translation", "secondaryMode translation");
  assert(clampSecondaryMode("roman") === "roman", "secondaryMode roman");
  assert(clampSecondaryMode("both") === "both", "secondaryMode both");
  assert(clampSecondaryMode("invalid") === "none", "secondaryMode fallback");

  // 2. lyricWidth: 30..100, 默认 54
  assert(clampLyricWidth(54) === 54, "lyricWidth default 54");
  assert(clampLyricWidth(20) === 30, "lyricWidth clamp lower 30");
  assert(clampLyricWidth(95) === 95, "lyricWidth 95 within range");
  assert(clampLyricWidth(120) === 100, "lyricWidth clamp upper 100");
  assert(clampLyricWidth(Number.NaN) === 30, "lyricWidth NaN fallback");

  // 3. lyricOffset: -600..600 垂直像素
  assert(clampLyricOffset(0) === 0, "lyricOffset default 0");
  assert(clampLyricOffset(-800) === -600, "lyricOffset clamp -600");
  assert(clampLyricOffset(999) === 600, "lyricOffset clamp 600");
  assert(clampLyricOffset(Number.NaN) === -600, "lyricOffset NaN fallback");

  // 4. backgroundSaturation: 0..2
  assert(clampBackgroundSaturation(1.0) === 1.0, "backgroundSaturation default 1.0");
  assert(clampBackgroundSaturation(-0.5) === 0, "backgroundSaturation clamp 0");
  assert(clampBackgroundSaturation(3.5) === 2.0, "backgroundSaturation clamp 2.0");
  assert(clampBackgroundSaturation(Number.NaN) === 0, "backgroundSaturation NaN fallback");

  // 5. flowSpeed: 0..5, 0 为静止
  assert(clampFlowSpeed(0) === 0, "flowSpeed 0 static");
  assert(clampFlowSpeed(-1) === 0, "flowSpeed lower clamp 0");
  assert(clampFlowSpeed(6) === 5, "flowSpeed upper clamp 5");
  assert(clampFlowSpeed(1.5) === 1.5, "flowSpeed valid 1.5");

  // 6. clampPresetId & clampLyricAlign
  assert(clampPresetId("1080p") === "1080p", "presetId 1080p");
  assert(clampPresetId("invalid") === "1080p", "presetId invalid fallback");
  assert(clampLyricAlign("center") === "center", "lyricAlign center");
  assert(clampLyricAlign("right") === "right", "lyricAlign right");
  assert(clampLyricAlign("top") === "left", "lyricAlign invalid fallback to left");

  // 7. clampBackgroundDim & clampBackgroundBlur & clampFps & clampRenderScale
  assert(clampBackgroundDim(0.4) === 0.4, "backgroundDim 0.4");
  assert(clampBackgroundDim(2) === 0.9, "backgroundDim upper clamp 0.9");
  assert(clampBackgroundBlur(90) === 80, "backgroundBlur upper clamp 80");
  assert(clampBackgroundBlur(-5) === 0, "backgroundBlur lower clamp 0");
  assert(clampFps(0) === 1, "fps lower clamp 1");
  assert(clampFps(300) === 240, "fps upper clamp 240");
  assert(clampRenderScale(0.05) === 0.1, "renderScale lower clamp 0.1");
  assert(clampRenderScale(2.5) === 2.0, "renderScale upper clamp 2.0");

  // 8. clampWordFadeWidth, clampAlignPosition, clampFontScale, clampFontWeight, clampArtworkScale
  assert(clampWordFadeWidth(1.2) === 1.2, "wordFadeWidth 1.2");
  assert(clampWordFadeWidth(3.0) === 2.0, "wordFadeWidth upper clamp 2.0");
  assert(clampAlignPosition(0.5) === 0.5, "alignPosition 0.5");
  assert(clampAlignPosition(1.5) === 1.0, "alignPosition upper clamp 1.0");
  assert(clampFontScale(0.2) === 0.5, "fontScale lower clamp 0.5");
  assert(clampFontScale(3) === 2.0, "fontScale upper clamp 2.0");
  assert(clampFontWeight(800) === 800, "fontWeight 800");
  assert(clampFontWeight(50) === 100, "fontWeight lower clamp 100");
  assert(clampFontWeight(1200) === 900, "fontWeight upper clamp 900");
  assert(clampArtworkScale(2.5) === 1.5, "artworkScale upper clamp 1.5");
  assert(clampArtworkScale(0.1) === 0.5, "artworkScale lower clamp 0.5");

  // 9. clampOptions 整体对象处理
  const badOptions: RenderOptions = {
    ...DEFAULT_RENDER_OPTIONS,
    lyricWidth: 120,
    lyricOffset: 850,
    backgroundSaturation: 5,
    secondaryMode: "both",
    flowSpeed: -2,
    fps: 300,
  };
  const fixed = clampOptions(badOptions);
  assert(fixed.lyricWidth === 100, "clampOptions lyricWidth");
  assert(fixed.lyricOffset === 600, "clampOptions lyricOffset");
  assert(fixed.backgroundSaturation === 2.0, "clampOptions backgroundSaturation");
  assert(fixed.secondaryMode === "both", "clampOptions secondaryMode both");
  assert(fixed.flowSpeed === 0, "clampOptions flowSpeed 0");
  assert(fixed.fps === 240, "clampOptions fps 240");

  // 10. GUI preset id 到实际 CSS 布局类映射
  assert(getStageLayoutClass("1080p") === "landscape", "1080p maps to landscape");
  assert(getStageLayoutClass("1440p") === "landscape", "1440p maps to landscape");
  assert(getStageLayoutClass("4k") === "landscape", "4k maps to landscape");
  assert(getStageLayoutClass("vertical") === "portrait", "vertical maps to portrait");
  assert(getStageLayoutClass("square") === "square", "square maps to square");

  return { passed, total, errors };
}

// 自动执行一次单元自检，确保模块导入时即完成校验
const result = runUnitTests();
if (result.errors.length > 0) {
  console.error(`[types.test] 失败: ${result.errors.length} 项错误`, result.errors);
} else {
  console.log(`[types.test] 全部通过: ${result.passed}/${result.total} 项测试通过`);
}
