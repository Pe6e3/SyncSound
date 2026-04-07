<template>
  <main class="container">
    <section class="panel">
      <h1>SyncSound Rooms</h1>
      <p class="subtitle">Пространство для совместной работы со звуком</p>
      <button class="primary-btn" type="button" @click="openNewRoom">Создать комнату</button>
    </section>
    <section class="rooms">
      <h2>Список комнат</h2>
      <p v-if="isLoading" class="state-label">Загрузка...</p>
      <ul v-else-if="rooms.length" class="room-list">
        <li v-for="roomId in rooms" :key="roomId">
          <button class="room-btn" @click.prevent="openRoom(roomId)" type="button">{{ roomId }}</button>
        </li>
      </ul>
      <p v-else class="state-label">Комнат пока нет</p>
    </section>
    <p v-if="errorMessage" class="error">{{ errorMessage }}</p>
  </main>
</template>

<script lang="ts">
import { createRoom, getRooms } from "@/api/roomApi"

export default {
  data() {
    return {
      errorMessage: "",
      isLoading: false,
      rooms: [] as string[],
      refreshTimerId: 0 as number
    }
  },
  methods: {
    async loadRooms() {
      this.isLoading = true

      try {
        this.rooms = await getRooms()
        this.errorMessage = ""
      } catch (error) {
        const message = error instanceof Error ? error.message : "Неизвестная ошибка"
        this.errorMessage = `Не удалось загрузить комнаты: ${message}`
      } finally {
        this.isLoading = false
      }
    },
    async openNewRoom() {
      try {
        const roomId = await createRoom()
        this.errorMessage = ""
        this.loadRooms()
        this.$router.push(`/sound/${roomId}`)
      } catch (error) {
        const message = error instanceof Error ? error.message : "Неизвестная ошибка"
        this.errorMessage = `Не удалось создать комнату: ${message}`
      }
    },
    openRoom(roomId: string) {
      this.$router.push(`/sound/${roomId}`)
    }
  },
  mounted() {
    this.loadRooms()
    this.refreshTimerId = window.setInterval(() => {
      this.loadRooms()
    }, 5000)
  },
  beforeUnmount() {
    if (!this.refreshTimerId) return
    window.clearInterval(this.refreshTimerId)
  }
}
</script>

<style scoped>
.container {
  min-height: 100vh;
  display: grid;
  place-content: center;
  gap: 18px;
  padding: 24px;
}

.panel,
.rooms {
  width: min(560px, 92vw);
  padding: 22px 24px;
  border-radius: 18px;
  border: 1px solid rgba(140, 157, 255, 0.26);
  background: rgba(26, 31, 53, 0.82);
  box-shadow: 0 18px 50px rgba(8, 10, 20, 0.6);
}

h1,
h2 {
  margin: 0 0 10px;
  letter-spacing: 0.01em;
}

.subtitle {
  margin: 0 0 16px;
  color: #9ca7d9;
}

.primary-btn,
.room-btn {
  border: 0;
  border-radius: 12px;
  padding: 10px 14px;
  color: #eef2ff;
  cursor: pointer;
  transition: transform 0.15s ease, box-shadow 0.15s ease, opacity 0.15s ease;
}

.primary-btn {
  background: linear-gradient(120deg, #8b5cf6, #35e0d7);
  box-shadow: 0 8px 20px rgba(53, 224, 215, 0.24);
  font-weight: 700;
}

.primary-btn:hover,
.room-btn:hover {
  transform: translateY(-1px);
}

.room-btn {
  width: 100%;
  background: linear-gradient(120deg, rgba(139, 92, 246, 0.35), rgba(240, 79, 216, 0.3));
  border: 1px solid rgba(181, 161, 255, 0.35);
  font-weight: 600;
}

.error {
  color: #ff8aa7;
  text-align: center;
}

.rooms {
  margin-top: 2px;
}

.room-list {
  list-style: none;
  padding: 0;
  margin: 0;
  display: grid;
  gap: 8px;
}

.state-label {
  color: #9ca7d9;
}
</style>
