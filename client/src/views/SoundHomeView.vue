<template>
  <main class="page-wrap">
    <section class="panel">
      <p class="badge">SyncSound</p>
      <h1>Комнаты звука</h1>
      <p class="subtitle">Главный роут проекта: /sound</p>

      <button type="button" class="primary-btn" @click="openNewRoom">Создать комнату</button>

      <section class="rooms">
        <h2>Список комнат</h2>
        <p v-if="isLoading" class="hint">Загрузка...</p>

        <ul v-else-if="rooms.length" class="room-list">
          <li v-for="roomId in rooms" :key="roomId">
            <button type="button" class="room-link" @click="openRoom(roomId)">
              {{ roomId }}
            </button>
          </li>
        </ul>

        <p v-else class="hint">Комнат пока нет</p>
      </section>

      <p v-if="errorMessage" class="error">{{ errorMessage }}</p>
    </section>
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
.page-wrap {
  min-height: 100vh;
  display: flex;
  align-items: center;
  justify-content: center;
  padding: 24px;
}

.panel {
  width: min(100%, 620px);
  padding: 30px;
  border-radius: 20px;
  border: 1px solid var(--border);
  background: linear-gradient(145deg, rgba(21, 62, 48, 0.9), rgba(10, 32, 24, 0.95));
  box-shadow: var(--shadow);
  text-align: center;
}

.badge {
  margin: 0 auto 10px;
  width: fit-content;
  padding: 4px 10px;
  border-radius: 999px;
  border: 1px solid var(--border);
  color: var(--text-muted);
  font-size: 12px;
  letter-spacing: 0.08em;
  text-transform: uppercase;
}

h1 {
  margin: 0;
  font-size: clamp(30px, 4vw, 40px);
}

.subtitle {
  margin-top: 8px;
  margin-bottom: 18px;
  color: var(--text-muted);
}

.primary-btn {
  border: none;
  border-radius: 12px;
  padding: 12px 18px;
  background: linear-gradient(180deg, var(--brand-strong), var(--brand));
  color: #052317;
  font-weight: 700;
  cursor: pointer;
  transition: transform 0.2s ease, filter 0.2s ease;
}

.primary-btn:hover {
  filter: brightness(1.05);
  transform: translateY(-1px);
}

.error {
  margin-top: 14px;
  color: var(--danger);
}

.rooms {
  margin-top: 22px;
  padding-top: 16px;
  border-top: 1px solid var(--border);
}

h2 {
  margin: 0 0 12px;
}

.hint {
  margin: 0;
  color: var(--text-muted);
}

.room-list {
  list-style: none;
  padding: 0;
  margin: 0;
  display: grid;
  gap: 8px;
}

.room-link {
  width: 100%;
  border: 1px solid var(--border);
  border-radius: 10px;
  padding: 10px 12px;
  background: rgba(13, 36, 28, 0.65);
  color: var(--text-main);
  cursor: pointer;
  transition: border-color 0.2s ease, background-color 0.2s ease;
}

.room-link:hover {
  border-color: var(--brand-strong);
  background: rgba(20, 51, 40, 0.9);
}
</style>
