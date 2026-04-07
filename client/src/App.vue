<template>
  <main class="container">
    <h1>Текущее время сервера</h1>
    <p v-if="errorMessage" class="error">{{ errorMessage }}</p>
    <p v-else class="time">{{ serverTimeText }}</p>
  </main>
</template>

<script lang="ts">
import { getServerTimeSeconds } from "@/api/timeApi"

export default {
  data() {
    return {
      serverUnixSeconds: 0,
      errorMessage: "",
      timerId: 0 as number
    }
  },
  computed: {
    serverTimeText(): string {
      if (!this.serverUnixSeconds) return "Загрузка..."

      const date = new Date(this.serverUnixSeconds * 1000)
      return `${this.serverUnixSeconds} (${date.toLocaleString("ru-RU")})`
    }
  },
  methods: {
    async loadServerTime() {
      try {
        const unixSeconds = await getServerTimeSeconds()
        this.serverUnixSeconds = unixSeconds
        this.errorMessage = ""
      } catch (error) {
        const message = error instanceof Error ? error.message : "Неизвестная ошибка"
        this.errorMessage = `Не удалось получить время сервера: ${message}`
      }
    }
  },
  mounted() {
    this.loadServerTime()
    this.timerId = window.setInterval(() => {
      this.loadServerTime()
    }, 5000)
  },
  beforeUnmount() {
    if (!this.timerId) return
    window.clearInterval(this.timerId)
  }
}
</script>

<style scoped>
.container {
  min-height: 100vh;
  display: grid;
  place-content: center;
  gap: 12px;
  text-align: center;
  font-family: Arial, sans-serif;
}

.time {
  font-size: 24px;
  font-weight: 700;
}

.error {
  color: #b91c1c;
}
</style>
