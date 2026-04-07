<template>
  <main class="page-wrap">
    <section class="panel">
      <button class="back-btn" type="button" @click="goBackToRooms">Назад</button>
      <h1>Комната</h1>
      <p class="room-id">ID комнаты: {{ roomId }}</p>
      <p v-if="serverUnixSeconds" class="time">Время сервера: {{ serverUnixSeconds }}</p>

      <div class="name-editor">
        <label class="name-label" for="device-name-input">Имя для этого устройства</label>
        <input
          id="device-name-input"
          v-model="displayNameInput"
          class="name-input"
          type="text"
          maxlength="50"
          placeholder="Например: Алекс - ноутбук"
        />
        <button class="save-btn" type="button" @click="saveDisplayName">Сохранить имя</button>
      </div>
    </section>

    <section class="panel">
      <h2>Устройства в комнате</h2>
      <p v-if="!devices.length" class="empty-label">Пока нет данных об устройствах</p>
      <ul v-else class="devices-list">
        <li v-for="device in devices" :key="device.deviceId" class="device-card">
          <p><strong>ID:</strong> {{ device.deviceId }}</p>
          <p><strong>Имя:</strong> {{ device.displayName || "Не задано" }}</p>
          <p><strong>Первый вход:</strong> {{ formatUnix(device.firstSeenUtc) }}</p>
          <p><strong>Последняя активность:</strong> {{ formatUnix(device.lastSeenUtc) }}</p>
          <details class="details">
            <summary>Информация об устройстве</summary>
            <ul class="info-list">
              <li v-for="(value, key) in device.deviceInfo" :key="`${device.deviceId}-${key}`">
                <span class="info-key">{{ key }}:</span> {{ value || "N/A" }}
              </li>
            </ul>
          </details>
        </li>
      </ul>
    </section>

    <p v-if="errorMessage" class="error">{{ errorMessage }}</p>
  </main>
</template>

<script lang="ts">
import { collectDeviceInfo } from "@/utils/deviceInfo"
import { getServerTimeSeconds } from "@/api/timeApi"
import { getRoom, registerDevice, updateDeviceName, type DeviceResponse } from "@/api/roomApi"

const LOCAL_DEVICE_ID_KEY = "syncsound-device-id"

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
      timerId: 0 as number,
      devices: [] as DeviceResponse[],
      currentDeviceId: "",
      displayNameInput: ""
    }
  },
  methods: {
    async enterRoom() {
      try {
        const localDeviceId = window.localStorage.getItem(LOCAL_DEVICE_ID_KEY) ?? undefined
        const response = await registerDevice(this.roomId, {
          deviceId: localDeviceId,
          displayName: this.displayNameInput || undefined,
          deviceInfo: collectDeviceInfo()
        })

        this.currentDeviceId = response.deviceId
        window.localStorage.setItem(LOCAL_DEVICE_ID_KEY, response.deviceId)
        this.devices = response.room.devices

        const currentDevice = this.devices.find(device => device.deviceId === response.deviceId)
        this.displayNameInput = currentDevice?.displayName ?? ""
        this.errorMessage = ""
      } catch (error) {
        const message = error instanceof Error ? error.message : "Неизвестная ошибка"
        this.errorMessage = `Комната недоступна: ${message}`
      }
    },
    async refreshRoomState() {
      try {
        const room = await getRoom(this.roomId)
        this.devices = room.devices
      } catch (error) {
        const message = error instanceof Error ? error.message : "Неизвестная ошибка"
        this.errorMessage = `Не удалось обновить комнату: ${message}`
      }
    },
    async loadServerTime() {
      try {
        this.serverUnixSeconds = await getServerTimeSeconds()
      } catch {
        this.serverUnixSeconds = 0
      }
    },
    async saveDisplayName() {
      if (!this.currentDeviceId) return

      try {
        const room = await updateDeviceName(this.roomId, this.currentDeviceId, this.displayNameInput)
        this.devices = room.devices
        this.errorMessage = ""
      } catch (error) {
        const message = error instanceof Error ? error.message : "Неизвестная ошибка"
        this.errorMessage = `Не удалось сохранить имя устройства: ${message}`
      }
    },
    formatUnix(unixSeconds: number): string {
      return new Date(unixSeconds * 1000).toLocaleString("ru-RU")
    },
    goBackToRooms() {
      this.$router.push("/sound")
    }
  },
  mounted() {
    this.enterRoom()
    this.loadServerTime()
    this.timerId = window.setInterval(() => {
      this.loadServerTime()
      this.refreshRoomState()
    }, 5000)
  },
  beforeUnmount() {
    if (!this.timerId) return
    window.clearInterval(this.timerId)
  },
  watch: {
    roomId() {
      this.enterRoom()
    }
  }
}
</script>

<style scoped>
.page-wrap {
  min-height: 100vh;
  display: grid;
  place-content: center;
  gap: 16px;
  padding: 24px;
}

.panel {
  width: min(760px, 94vw);
  padding: 24px 26px;
  border-radius: 18px;
  border: 1px solid var(--border);
  background: linear-gradient(145deg, rgba(21, 62, 48, 0.9), rgba(10, 32, 24, 0.95));
  box-shadow: var(--shadow);
  text-align: center;
}

h1 {
  margin: 4px 0 10px;
}

h2 {
  margin: 0 0 14px;
}

.room-id {
  margin: 0 0 8px;
  color: var(--text-muted);
}

.back-btn {
  display: inline-block;
  margin-bottom: 8px;
  border: 1px solid var(--border);
  border-radius: 12px;
  background: rgba(11, 33, 25, 0.85);
  color: var(--text-main);
  font-weight: 600;
  padding: 9px 14px;
  cursor: pointer;
  transition: transform 0.15s ease, border-color 0.2s ease, background-color 0.2s ease;
}

.back-btn:hover {
  transform: translateY(-1px);
  border-color: var(--brand-strong);
  background: rgba(20, 51, 40, 0.9);
}

.name-editor {
  margin-top: 14px;
  display: grid;
  gap: 8px;
}

.name-label {
  text-align: left;
  color: var(--text-muted);
  font-size: 13px;
}

.name-input {
  border: 1px solid var(--border);
  border-radius: 10px;
  padding: 10px 12px;
  background: rgba(7, 23, 17, 0.75);
  color: var(--text-main);
}

.save-btn {
  justify-self: start;
  border: 1px solid var(--border);
  border-radius: 10px;
  padding: 8px 14px;
  background: linear-gradient(180deg, var(--brand-strong), var(--brand));
  color: #052317;
  cursor: pointer;
  font-weight: 600;
}

.devices-list {
  list-style: none;
  margin: 0;
  padding: 0;
  display: grid;
  gap: 10px;
}

.device-card {
  border: 1px solid var(--border);
  border-radius: 12px;
  padding: 12px 14px;
  background: rgba(15, 41, 31, 0.58);
}

.device-card p {
  margin: 0 0 7px;
}

.details {
  margin-top: 8px;
}

.info-list {
  margin: 8px 0 0;
  padding-left: 18px;
  display: grid;
  gap: 4px;
}

.info-key {
  color: var(--text-muted);
}

.empty-label {
  color: var(--text-muted);
}

.error {
  color: var(--danger);
  text-align: center;
}

.time {
  margin: 0;
  font-weight: 700;
  color: var(--brand-strong);
}
</style>
