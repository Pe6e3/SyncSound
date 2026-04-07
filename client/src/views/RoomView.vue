<template>
  <main class="container">
    <section class="panel">
      <button class="back-btn" type="button" @click="goBackToRooms">Назад</button>
      <h1>Комната</h1>
      <p class="room-id">ID комнаты: {{ roomId }}</p>
      <p v-if="serverUnixSeconds" class="time">Время сервера: {{ serverUnixSeconds }}</p>
    </section>
    <p v-if="errorMessage" class="error">{{ errorMessage }}</p>
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
.container {
  min-height: 100vh;
  display: grid;
  place-content: center;
  gap: 16px;
  padding: 24px;
}

.panel {
  width: min(520px, 92vw);
  padding: 22px 24px;
  border-radius: 18px;
  border: 1px solid rgba(140, 157, 255, 0.26);
  background: rgba(26, 31, 53, 0.82);
  box-shadow: 0 18px 50px rgba(8, 10, 20, 0.6);
  text-align: center;
}

h1 {
  margin: 4px 0 10px;
}

.room-id {
  margin: 0 0 8px;
  color: #e8ecff;
}

.back-btn {
  display: inline-block;
  margin-bottom: 8px;
  border: 1px solid rgba(181, 161, 255, 0.35);
  border-radius: 12px;
  background: linear-gradient(120deg, rgba(139, 92, 246, 0.35), rgba(240, 79, 216, 0.3));
  color: #eef2ff;
  font-weight: 600;
  padding: 9px 14px;
  cursor: pointer;
  transition: transform 0.15s ease;
}

.back-btn:hover {
  transform: translateY(-1px);
}

.error {
  color: #ff8aa7;
  text-align: center;
}

.time {
  font-weight: 700;
  color: #35e0d7;
}
</style>
