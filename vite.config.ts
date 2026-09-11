import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

// AMLL 依赖 WebGL + Web Audio，开发服务器固定 127.0.0.1 与固定端口，
// 便于自动化录制脚本（scripts/render-mv.mjs）稳定连接。
export default defineConfig({
  plugins: [react()],
  server: {
    host: "127.0.0.1",
    port: 5273,
    strictPort: true,
  },
  preview: {
    host: "127.0.0.1",
    port: 5273,
    strictPort: true,
  },
  build: {
    target: "es2021",
    chunkSizeWarningLimit: 4096,
  },
});
