import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// Locally the API runs on http://localhost:5250 (see src/AudioBlog.Api/Properties/launchSettings.json).
// The dev server proxies /api, so no CORS setup is needed while developing.
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: { '/api': 'http://localhost:5250' },
  },
});
