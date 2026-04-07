<template>
  <main class="page-wrap">
    <section class="panel">
      <button type="button" class="back-btn" @click="goBackToRooms">Назад</button>
      <h1>Комната</h1>
      <p class="room-id">ID комнаты: {{ roomId }}</p>
      <p v-if="serverUnixSeconds" class="time">Время сервера: {{ serverUnixSeconds }}</p>
      <p v-if="errorMessage" class="error">{{ errorMessage }}</p>
    </section>
  </main>
</template>

<script lang="ts">
import { getRoom } from "@/api/roomApi"
import { getServerTimeSeconds } from "@/api/timeApi"

export default {
  props: {
    roomId: {
      type: String,
      required: true
    }
  },
  data() {
    return {
      errorMessage: "",
      serverUnixSeconds: 0,
      timerId: 0 as number
    }
  },
  methods: {
    async loadRoom() {
      try {
        await getRoom(this.roomId)
        this.errorMessage = ""
      } catch (error) {
        const message = error instanceof Error ? error.message : "Неизвестная ошибка"
        this.errorMessage = `Комната недоступна: ${message}`
      }
    },
    async loadServerTime() {
      try {
        this.serverUnixSeconds = await getServerTimeSeconds()
      } catch {
        this.serverUnixSeconds = 0
      }
    },
    goBackToRooms() {
      this.$router.push("/sound")
    }
  },
  mounted() {
    this.loadRoom()
    this.loadServerTime()
    this.timerId = window.setInterval(() => {
      this.loadServerTime()
    }, 5000)
  },
  beforeUnmount() {
    if (!this.timerId) return
    window.clearInterval(this.timerId)
  },
  watch: {
    roomId() {
      this.loadRoom()
    }
  }
}
</script>

<style scoped>
.page-wrap {
  min-height: 100vh;
  display: flex;
  align-items: center;
  justify-content: center;
  padding: 24px;
}

.panel {
  width: min(100%, 560px);
  padding: 28px;
  border-radius: 20px;
  border: 1px solid var(--border);
  background: linear-gradient(145deg, rgba(21, 62, 48, 0.9), rgba(10, 32, 24, 0.95));
  box-shadow: var(--shadow);
  text-align: center;
}

.back-btn {
  margin-bottom: 14px;
  border: 1px solid var(--border);
  border-radius: 10px;
  padding: 8px 14px;
  background: rgba(11, 33, 25, 0.85);
  color: var(--text-main);
  cursor: pointer;
  transition: border-color 0.2s ease, background-color 0.2s ease;
}

.back-btn:hover {
  border-color: var(--brand-strong);
  background: rgba(20, 51, 40, 0.9);
}

h1 {
  margin: 0;
  font-size: clamp(30px, 4vw, 40px);
}

.room-id {
  margin: 10px 0 8px;
  color: var(--text-muted);
}

.error {
  color: var(--danger);
}

.time {
  margin: 0;
  font-weight: 700;
}
</style>
