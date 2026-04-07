<template>
  <RouterView />
  <footer class="footer">
    <span>{{ appVersionText }}</span>
  </footer>
</template>

<script lang="ts">
import { getVersion } from "@/api/versionApi"

export default {
  name: "AppRoot",
  data() {
    return {
      appVersion: ""
    }
  },
  computed: {
    appVersionText(): string {
      if (!this.appVersion) return "version: loading..."
      return `version: ${this.appVersion}`
    }
  },
  methods: {
    async loadVersion() {
      try {
        this.appVersion = await getVersion()
      } catch {
        this.appVersion = "v1.0.0"
      }
    }
  },
  mounted() {
    this.loadVersion()
  }
}
</script>

<style>
:root {
  color-scheme: dark;
  --bg-main: #0a0b12;
  --bg-secondary: #121626;
  --surface: rgba(26, 31, 53, 0.82);
  --surface-border: rgba(140, 157, 255, 0.26);
  --text-main: #e8ecff;
  --text-soft: #9ca7d9;
  --accent-cyan: #35e0d7;
  --accent-violet: #8b5cf6;
  --accent-magenta: #f04fd8;
}

* {
  box-sizing: border-box;
}

body {
  margin: 0;
  min-height: 100vh;
  color: var(--text-main);
  font-family: "Segoe UI", "Helvetica Neue", sans-serif;
  background:
    radial-gradient(circle at 15% 25%, rgba(53, 224, 215, 0.2), transparent 38%),
    radial-gradient(circle at 80% 5%, rgba(240, 79, 216, 0.18), transparent 42%),
    radial-gradient(circle at 82% 84%, rgba(139, 92, 246, 0.22), transparent 45%),
    linear-gradient(145deg, var(--bg-main), var(--bg-secondary));
}

#app {
  min-height: 100vh;
}

.footer {
  position: fixed;
  right: 14px;
  bottom: 10px;
  padding: 6px 10px;
  border-radius: 999px;
  border: 1px solid var(--surface-border);
  background: rgba(8, 11, 22, 0.72);
  backdrop-filter: blur(10px);
  font-size: 12px;
  color: var(--text-soft);
  letter-spacing: 0.03em;
}
</style>
