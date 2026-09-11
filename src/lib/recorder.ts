/**
 * 浏览器内舞台录制。
 *
 * 普通 div 没有 captureStream，因此这里使用「标签页捕获 + Region Capture」：
 * 先由用户授权捕获当前标签页，再用 CropTarget 把画面裁剪到舞台元素，
 * 从而得到与预览构图一致、且带原声的成片。
 *
 * 该方式受实时播放与编码器影响，帧率是「尽力而为」；
 * 需要严格帧率与无损音轨时请改用 `npm run render` 命令行渲染。
 */

interface CropTargetLike {
  fromElement(element: Element): Promise<unknown>;
}

declare global {
  interface Window {
    CropTarget?: CropTargetLike;
  }
}

export interface StageRecorderOptions {
  fps: number;
  videoBitsPerSecond: number;
  /** 录制源的音频元素，用于并入音轨 */
  audioElements?: HTMLAudioElement[];
  /** 是否把画面裁剪到指定元素（默认 true，失败时自动回退为整页） */
  cropToStage?: boolean;
}

export interface StageRecorderHandle {
  stop: () => Promise<Blob>;
  mimeType: string;
  /** 是否成功裁剪到舞台区域 */
  cropped: boolean;
  /** 用于展示录制状态的提示信息 */
  notice?: string;
}

function pickMimeType(): string {
  const candidates = [
    "video/webm;codecs=vp9,opus",
    "video/webm;codecs=vp8,opus",
    "video/webm",
  ];
  for (const type of candidates) {
    if (typeof MediaRecorder !== "undefined" && MediaRecorder.isTypeSupported(type)) return type;
  }
  return "";
}

/** 从页面内的音频元素取音轨，保证录制文件自带声音 */
function collectAudioTracks(elements: HTMLAudioElement[]): MediaStreamTrack[] {
  const tracks: MediaStreamTrack[] = [];
  for (const el of elements) {
    const capture = (el as HTMLAudioElement & {
      captureStream?: () => MediaStream;
      mozCaptureStream?: () => MediaStream;
    }).captureStream ?? (el as HTMLAudioElement & { mozCaptureStream?: () => MediaStream }).mozCaptureStream;
    if (typeof capture !== "function") continue;
    try {
      const stream = capture.call(el);
      tracks.push(...stream.getAudioTracks());
    } catch {
      // 该元素已被 WebAudio 接管时 captureStream 会抛错，忽略即可
    }
  }
  return tracks;
}

export async function startStageRecording(
  stage: HTMLElement,
  options: StageRecorderOptions,
): Promise<StageRecorderHandle> {
  if (!navigator.mediaDevices?.getDisplayMedia) {
    throw new Error("当前浏览器不支持屏幕捕获，请改用 Chrome / Edge，或使用 npm run render 命令行渲染。");
  }

  const displayStream = await navigator.mediaDevices.getDisplayMedia({
    video: { frameRate: { ideal: options.fps, max: options.fps } },
    audio: false,
    // 以下字段为 Chromium 扩展参数：优先当前标签页并允许选中自身
    preferCurrentTab: true,
    selfBrowserSurface: "include",
    surfaceSwitching: "exclude",
  } as DisplayMediaStreamOptions);

  const videoTracks = displayStream.getVideoTracks();
  if (videoTracks.length === 0) {
    displayStream.getTracks().forEach((t) => t.stop());
    throw new Error("没有获得可用的画面轨道，录制已取消。");
  }

  let cropped = false;
  let notice: string | undefined;

  if (options.cropToStage !== false && window.CropTarget) {
    try {
      const target = await window.CropTarget.fromElement(stage);
      // cropTo 属于 Region Capture 扩展，尚未进入标准 TS 类型定义
      await videoTracks[0].applyConstraints({
        advanced: [{ cropTo: target }],
      } as unknown as MediaTrackConstraints);
      cropped = true;
    } catch (err) {
      notice = `画面裁剪失败，将录制整个标签页：${err instanceof Error ? err.message : String(err)}`;
    }
  } else if (!window.CropTarget) {
    notice = "当前浏览器不支持 Region Capture，将录制整个标签页，请保持窗口最大化且不要切走。";
  }

  let stream: MediaStream | null = null;
  try {
    stream = new MediaStream([videoTracks[0]]);
    const audioTracks = collectAudioTracks(options.audioElements ?? []);
    for (const track of audioTracks) stream.addTrack(track);

    const mimeType = pickMimeType();
    const recorder = new MediaRecorder(stream, {
      ...(mimeType ? { mimeType } : {}),
      videoBitsPerSecond: options.videoBitsPerSecond,
    });

    const chunks: BlobPart[] = [];
    recorder.ondataavailable = (e) => {
      if (e.data.size > 0) chunks.push(e.data);
    };
    recorder.start(1000);

    const combinedStream = stream;
    let stopped = false;
    const stopAllTracks = () => {
      try {
        combinedStream.getTracks().forEach((t) => t.stop());
      } catch {
        // ignore
      }
      try {
        displayStream.getTracks().forEach((t) => t.stop());
      } catch {
        // ignore
      }
    };

    return {
      mimeType: mimeType || "video/webm",
      cropped,
      notice,
      stop: () =>
        new Promise<Blob>((resolve) => {
          if (stopped) {
            stopAllTracks();
            resolve(new Blob(chunks, { type: mimeType || "video/webm" }));
            return;
          }
          stopped = true;

          recorder.addEventListener(
            "stop",
            () => {
              stopAllTracks();
              resolve(new Blob(chunks, { type: mimeType || "video/webm" }));
            },
            { once: true },
          );

          try {
            if (recorder.state !== "inactive") {
              recorder.stop();
            } else {
              stopAllTracks();
              resolve(new Blob(chunks, { type: mimeType || "video/webm" }));
            }
          } catch {
            stopAllTracks();
            resolve(new Blob(chunks, { type: mimeType || "video/webm" }));
          }
        }),
    };
  } catch (err) {
    if (stream) {
      try {
        stream.getTracks().forEach((t) => t.stop());
      } catch {
        // ignore
      }
    }
    try {
      displayStream.getTracks().forEach((t) => t.stop());
    } catch {
      // ignore
    }
    throw err;
  }
}

export function downloadBlob(blob: Blob, fileName: string): void {
  const url = URL.createObjectURL(blob);
  const a = document.createElement("a");
  a.href = url;
  a.download = fileName;
  document.body.appendChild(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 60_000);
}
