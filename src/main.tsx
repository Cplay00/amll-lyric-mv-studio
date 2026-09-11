import { createRoot } from "react-dom/client";
import "@applemusic-like-lyrics/core/style.css";
import "./styles.css";
import App from "./App";

// 不使用 StrictMode：AMLL 的歌词播放器与 WebGL 背景在开发模式下会被
// 双次挂载/销毁，容易触发 WebGL 上下文重建设与动画抖动。
createRoot(document.getElementById("root")!).render(<App />);
