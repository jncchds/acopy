import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import fs from 'fs'
import path from 'path'

// The Docker build copies VERSION to /VERSION; locally it sits at the repo root
const versionFile = [path.resolve(import.meta.dirname, '../../VERSION'), '/VERSION'].find(p => fs.existsSync(p))
const appVersion = versionFile ? fs.readFileSync(versionFile, 'utf-8').trim() : 'dev'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  define: {
    __APP_VERSION__: JSON.stringify(appVersion),
  },
  build: {
    outDir: '../ACopy.Api/wwwroot',
    emptyOutDir: true,
  },
  server: {
    proxy: {
      '/api': { target: 'http://localhost:5005', changeOrigin: true },
    },
  },
})
