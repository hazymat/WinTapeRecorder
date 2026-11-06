import { defineConfig } from 'vite'
import { svelte } from '@sveltejs/vite-plugin-svelte'
// Build straight into the shell's wwwroot, served by WebView2 from app resources
export default defineConfig({ plugins: [svelte()], base: './', build: { outDir: '../app/wwwroot', emptyOutDir: true } })
