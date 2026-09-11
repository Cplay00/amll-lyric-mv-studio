import type { LyricLine } from "@applemusic-like-lyrics/core";
import * as lyricKit from "@applemusic-like-lyrics/lyric";
import type { SecondaryLyricMode } from "./types";

export interface ParsedLyric {
  lines: LyricLine[];
  /** 原始格式标识，用于界面显示 */
  format: string;
  /** TTML 元数据（标题、艺术家等），其他格式为空数组 */
  metadata: [string, string[]][];
}

type Parser = (text: string) => LyricLine[];

/** 各格式解析器，键为可识别的文件扩展名 */
const EXT_PARSERS: Record<string, { parser: Parser; label: string }> = {
  ttml: { parser: (t) => lyricKit.parseTTML(t).lines, label: "TTML（Apple Music 逐字）" },
  xml: { parser: (t) => lyricKit.parseTTML(t).lines, label: "TTML（Apple Music 逐字）" },
  lrc: { parser: lyricKit.parseLrc, label: "LRC（逐行）" },
  alrc: { parser: lyricKit.parseLrcA2, label: "LRC A2（逐字）" },
  yrc: { parser: lyricKit.parseYrc, label: "网易云 YRC（逐字）" },
  qrc: { parser: lyricKit.parseQrc, label: "QQ 音乐 QRC（逐字）" },
  eslrc: { parser: lyricKit.parseEslrc, label: "ESLyric（逐字）" },
  lys: { parser: lyricKit.parseLys, label: "Lyricify Syllable（逐字）" },
  lyl: { parser: lyricKit.parseLyl, label: "Lyricify Lines（逐行）" },
  lqe: { parser: lyricKit.parseLqe, label: "Lyricify Quick Export" },
};

/**
 * 通过内容特征猜测格式，用于扩展名缺失、不准确或直接从网络拉取的歌词文本
 */
export function guessByContent(text: string): keyof typeof EXT_PARSERS | null {
  const head = text.slice(0, 4000).trim();
  if (/<tt\b/i.test(head) || /<tt:tt\b/i.test(head)) return "ttml";
  if (/^\s*<\?xml/i.test(head) || /<lyric\b/i.test(head)) return "ttml";
  if (head.includes("xmlns:ttm=") || head.includes("xmlns=\"http://www.w3.org/ns/ttml\"")) return "ttml";
  if (/^\[Lyricify Quick Export\]/im.test(head)) return "lqe";
  if (/^\[\d{2}:\d{2}\.\d{2,3}\]/m.test(head) && /\(\d+,\d+\)/.test(head)) return "lys";
  if (/\[\d+,\d+\]/.test(head)) return "yrc";
  if (/\[\d+,\d+\]\(/.test(head) || /<(\d+),(\d+)>/.test(head)) return "qrc";
  if (/\[\d{2}:\d{2}\.\d{2,3}\]/.test(head) && /<\d+>/.test(head)) return "alrc";
  if (/\[\d{2}:\d{2}\.\d{2,3}\]/.test(head) && /\'\d+,\d+/.test(head)) return "eslrc";
  if (/^\[\d{2}:\d{2}\.\d{2,3}\]/m.test(head)) return "lrc";
  return null;
}

/**
 * 稳健的歌词排序：
 * AMLL 必须按时间升序。但如果简单的 sort a.startTime - b.startTime，
 * 遇到伴唱行 (isBG = true) 和主行时间相近甚至先于主行几十毫秒时，全局排序会拆开紧随的主歌/伴唱配对关系。
 * 这里使用稳定排序保持同一组内的相对次序，且让主歌词优先于时间几乎一致的背景歌词。
 */
export function sortLines(lines: LyricLine[]): LyricLine[] {
  // 记录原始索引以保持严格稳定
  const indexed = lines.map((line, idx) => ({ line, idx }));
  indexed.sort((a, b) => {
    const diff = a.line.startTime - b.line.startTime;
    if (Math.abs(diff) > 10) {
      return diff;
    }
    // 时间极其接近（<=10ms）时，非背景行排在背景行前面
    if (Boolean(a.line.isBG) !== Boolean(b.line.isBG)) {
      return a.line.isBG ? 1 : -1;
    }
    return a.idx - b.idx;
  });
  return indexed.map((item) => item.line);
}

/**
 * 安全地派生 LyricLine 数组：
 * 满足需求 3：根据 secondaryMode ("none" | "translation" | "roman" | "both")
 * 决定保留或清空 translatedLyric / romanLyric。
 * 严格保留 words 逐字序列、isBG、isDuet、startTime、endTime、tone 等所有关键信息。
 */
export function deriveSecondaryLyrics(
  lines: LyricLine[],
  mode: SecondaryLyricMode,
): LyricLine[] {
  if (mode === "both") {
    // 均为开启，直接原样或浅拷贝
    return lines;
  }

  return lines.map((line) => {
    let translatedLyric = line.translatedLyric;
    let romanLyric = line.romanLyric;

    if (mode === "none") {
      translatedLyric = "";
      romanLyric = "";
    } else if (mode === "translation") {
      romanLyric = "";
    } else if (mode === "roman") {
      translatedLyric = "";
    }

    return {
      ...line,
      translatedLyric,
      romanLyric,
      // 深度保护 words 数组引用，防止副作用
      words: line.words ? [...line.words] : [],
      isBG: Boolean(line.isBG),
      isDuet: Boolean(line.isDuet),
    };
  });
}

export function parseLyricText(text: string, fileName: string): ParsedLyric {
  const ext = fileName.split(".").pop()?.toLowerCase() ?? "";
  const byExt = EXT_PARSERS[ext];
  const guessed = guessByContent(text);
  const key = (byExt ? ext : guessed) ?? guessed;

  if (!key || !EXT_PARSERS[key]) {
    throw new Error(
      `无法识别歌词格式（文件：${fileName}）。支持的格式：${Object.keys(EXT_PARSERS).join("、")}`,
    );
  }

  const entry = EXT_PARSERS[key];
  if (key === "ttml" || key === "xml") {
    const parsed = lyricKit.parseTTML(text);
    return { lines: sortLines(parsed.lines), format: entry.label, metadata: parsed.metadata ?? [] };
  }
  return { lines: sortLines(entry.parser(text)), format: entry.label, metadata: [] };
}

export function getLyricDuration(lines: LyricLine[]): number {
  return lines.reduce((max, line) => Math.max(max, line.endTime), 0);
}

/** 从 TTML 元数据中取歌曲信息，用于舞台与标题展示 */
export function readMeta(metadata: [string, string[]][], key: string): string {
  const found = metadata.find(([k]) => k.toLowerCase() === key.toLowerCase());
  return found?.[1]?.[0] ?? "";
}

/**
 * 健壮的 fetch 文本辅助，检查 response.ok 并抛出更易读的错误
 */
export async function fetchTextWithCheck(url: string, description = "文件"): Promise<string> {
  const res = await fetch(url);
  if (!res.ok) {
    throw new Error(`无法获取${description}（HTTP ${res.status} ${res.statusText}）: ${url}`);
  }
  return await res.text();
}
