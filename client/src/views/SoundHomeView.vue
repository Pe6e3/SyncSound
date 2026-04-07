<template>
  <main class="container">
    <h1>Sound</h1>
    <p>Главный роут проекта: /sound</p>
    <button type="button" @click="openNewRoom">Создать комнату</button>
    <section class="rooms">
      <h2>Список комнат</h2>
      <p v-if="isLoading">Загрузка...</p>
      <ul v-else-if="rooms.length" class="room-list">
        <li v-for="roomId in rooms" :key="roomId">
          <a @click.prevent="openRoom(roomId)" href="#">{{ roomId }}</a>
        </li>
      </ul>
      <p v-else>Комнат пока нет</p>
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
  gap: 12px;
  text-align: center;
  font-family: Arial, sans-serif;
}

.error {
  color: #b91c1c;
}

.rooms {
  margin-top: 8px;
}

.room-list {
  list-style: none;
  padding: 0;
  margin: 0;
  display: grid;
  gap: 6px;
}
</style>
