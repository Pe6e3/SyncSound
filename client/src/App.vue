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

<style scoped>
.footer {
  position: fixed;
  right: 12px;
  bottom: 8px;
  font-size: 12px;
  color: #6b7280;
}
</style>
