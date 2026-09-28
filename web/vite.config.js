import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

// 开发代理：API 与 SignalR 全部转发到 SmartKB.Api（http profile, 端口见 launchSettings.json）
export default defineConfig({
  plugins: [vue()],
  server: {
    port: 5173,
    proxy: {
      '/api': { target: 'http://localhost:5219', changeOrigin: true },
      '/hubs': {
        target: 'http://localhost:5219',
        changeOrigin: true,
        ws: true // SignalR WebSocket
      }
    }
  }
})
