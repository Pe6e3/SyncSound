<template>
  <main class="container">
    <button type="button" @click="goBackToRooms">Назад</button>
    <h1>Комната</h1>
    <p>ID комнаты: {{ roomId }}</p>
    <p v-if="serverUnixSeconds" class="time">Время сервера: {{ serverUnixSeconds }}</p>
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
  gap: 12px;
  text-align: center;
  font-family: Arial, sans-serif;
}

.error {
  color: #b91c1c;
}

.time {
  font-weight: 700;
}
</style>
