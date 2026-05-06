import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  build: {
    rolldownOptions: {
      output: {
        codeSplitting: true,
        manualChunks(id) {
          if (!id.includes('node_modules')) return undefined
          if (id.includes('@tiptap') || id.includes('prosemirror')) return 'editor'
          if (id.includes('react-markdown') || id.includes('remark-') || id.includes('micromark') || id.includes('mdast') || id.includes('unified')) {
            return 'markdown'
          }
          if (id.includes('lucide-react')) return 'icons'
          if (id.includes('react') || id.includes('react-dom')) return 'react'
          return 'vendor'
        },
      },
    },
  },
})
