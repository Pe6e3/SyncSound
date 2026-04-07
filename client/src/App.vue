<template>
  <div class="app-shell">
    <RouterView />
    <footer class="footer">
      <span>{{ appVersionText }}</span>
    </footer>
  </div>
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
  --bg-main: #06140f;
  --bg-secondary: #0b251b;
  --surface: #113126;
  --surface-soft: #174133;
  --text-main: #e6f7ee;
  --text-muted: #9cc9b0;
  --brand: #2f9f6f;
  --brand-strong: #3db37e;
  --danger: #ff8a95;
  --border: rgba(116, 180, 146, 0.3);
  --shadow: 0 20px 40px rgba(2, 10, 7, 0.55);
}

* {
  box-sizing: border-box;
}

body {
  margin: 0;
  min-height: 100vh;
  color: var(--text-main);
  font-family: "Inter", "Segoe UI", "Arial", sans-serif;
  background:
    radial-gradient(circle at top right, #1a5c42 0%, transparent 42%),
    radial-gradient(circle at bottom left, #0e3b2b 0%, transparent 38%),
    linear-gradient(145deg, var(--bg-main), var(--bg-secondary));
}

a {
  color: var(--brand-strong);
}

button {
  font: inherit;
}

#app,
.app-shell {
  min-height: 100vh;
}

.footer {
  position: fixed;
  right: 16px;
  bottom: 12px;
  padding: 6px 10px;
  border-radius: 999px;
  border: 1px solid var(--border);
  background: rgba(8, 24, 18, 0.7);
  backdrop-filter: blur(6px);
  font-size: 12px;
  color: var(--text-muted);
}
</style>
