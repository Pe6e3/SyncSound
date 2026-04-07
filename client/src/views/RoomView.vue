<template>
  <main class="page-wrap">
    <section class="panel">
      <button class="back-btn" type="button" @click="goBackToRooms">Назад</button>
      <h1>Комната</h1>
      <p class="room-id">ID комнаты: {{ roomId }}</p>
      <p v-if="serverUnixSeconds" class="time">Время сервера: {{ serverUnixSeconds }}</p>
    </section>

    <section class="panel">
      <h2>Устройства в комнате</h2>
      <p v-if="!orderedDevices.length" class="empty-label">Пока нет данных об устройствах</p>
      <ul v-else class="devices-list">
        <li
          v-for="device in orderedDevices"
          :key="device.deviceId"
          :class="['device-card', { 'device-card--self': isCurrentDevice(device) }]"
        >
          <span class="card-id">{{ device.deviceId }}</span>

          <button
            :class="['role-dot', { 'role-dot--master': device.isMaster }]"
            type="button"
            :title="getRoleDotTitle(device)"
            :disabled="!canTransferMasterTo(device)"
            @click="handleRoleDotClick(device)"
          >
            <span v-if="device.isMaster">★</span>
          </button>

          <p class="device-name-row">
            <span class="device-name-text">{{ device.displayName || "Имя не указано" }}</span>
            <button
              v-if="isCurrentDevice(device)"
              class="name-edit-btn"
              type="button"
              title="Изменить имя"
              @click="toggleNameEdit(device)"
            >
              ✎
            </button>
          </p>
          <p><strong>Присоединился:</strong> {{ formatUnix(device.firstSeenUtc) }} ({{ formatActivity(device.firstSeenUtc) }})</p>
          <p><strong>Timezone:</strong> {{ getTimezone(device.deviceInfo) }}</p>
          <p><strong>Тип устройства:</strong> {{ getDeviceType(device) }}</p>

          <div v-if="editingDeviceId === device.deviceId" class="inline-editor">
            <input
              :id="`name-input-${device.deviceId}`"
              v-model="displayNameInput"
              class="name-input"
              type="text"
              maxlength="50"
              placeholder="Введите имя"
            />
            <button class="save-btn" type="button" @click="saveDisplayName">Сохранить</button>
          </div>
        </li>
      </ul>
    </section>

    <p v-if="errorMessage" class="error">{{ errorMessage }}</p>
  </main>
</template>

<script lang="ts">
import { collectDeviceInfo } from "@/utils/deviceInfo"
import { getServerTimeSeconds } from "@/api/timeApi"
import { getRoom, registerDevice, transferMaster, updateDeviceName, type DeviceResponse } from "@/api/roomApi"

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
      displayNameInput: "",
      editingDeviceId: ""
    }
  },
  computed: {
    orderedDevices(): DeviceResponse[] {
      return [...this.devices].sort((left, right) => left.firstSeenUtc - right.firstSeenUtc)
    },
    currentDevice(): DeviceResponse | undefined {
      return this.devices.find(device => device.deviceId == this.currentDeviceId)
    },
    isCurrentMaster(): boolean {
      return Boolean(this.currentDevice?.isMaster)
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
        this.editingDeviceId = ""
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
        this.editingDeviceId = ""
        this.errorMessage = ""
      } catch (error) {
        const message = error instanceof Error ? error.message : "Неизвестная ошибка"
        this.errorMessage = `Не удалось сохранить имя устройства: ${message}`
      }
    },
    formatUnix(unixSeconds: number): string {
      return new Date(unixSeconds * 1000).toLocaleString("ru-RU")
    },
    formatActivity(firstSeenUtc: number): string {
      const nowUnix = Math.floor(Date.now() / 1000)
      const diffSeconds = Math.max(0, nowUnix - firstSeenUtc)
      if (diffSeconds < 60) return `${diffSeconds} сек`

      const minutes = Math.floor(diffSeconds / 60)
      if (minutes <= 90) return `${minutes} мин`

      const hours = Math.round(minutes / 60)
      return `${hours} ч`
    },
    getTimezone(deviceInfo: Record<string, string>): string {
      return deviceInfo.timezone || "N/A"
    },
    detectDeviceType(userAgent: string): string {
      const ua = userAgent.toLowerCase()

      if (!ua) return "Неизвестно"
      if (ua.includes("iphone")) return "iPhone"
      if (ua.includes("ipad")) return "iPad"
      if (ua.includes("android") && ua.includes("mobile")) return "Android телефон"
      if (ua.includes("android")) return "Android планшет"
      if (ua.includes("tablet")) return "Планшет"
      if (ua.includes("macintosh") || ua.includes("windows") || ua.includes("linux")) {
        if (this.isLikelyLaptopByInfo()) return "Ноутбук"
        return "ПК"
      }

      return "Неизвестно"
    },
    isLikelyLaptopByInfo(): boolean {
      if (!this.currentDeviceId) return false

      const device = this.devices.find(entry => entry.deviceId == this.currentDeviceId)
      if (!device) return false

      const screenResolution = device.deviceInfo.screenResolution || ""
      const maxTouchPointsRaw = device.deviceInfo.maxTouchPoints || "0"
      const pixelRatioRaw = device.deviceInfo.pixelRatio || "1"
      const [widthString, heightString] = screenResolution.split("x")

      const width = Number(widthString)
      const height = Number(heightString)
      const maxTouchPoints = Number(maxTouchPointsRaw)
      const pixelRatio = Number(pixelRatioRaw)

      const hasValidResolution = Number.isFinite(width) && Number.isFinite(height) && width > 0 && height > 0
      const smallestSide = hasValidResolution ? Math.min(width, height) : 0
      const largestSide = hasValidResolution ? Math.max(width, height) : 0

      const typicalLaptopResolution =
        hasValidResolution &&
        smallestSide >= 700 &&
        smallestSide <= 1800 &&
        largestSide >= 1200 &&
        largestSide <= 3200

      const touchDesktopLike = maxTouchPoints > 0 && maxTouchPoints <= 10
      const highDensityPortable = pixelRatio >= 1.5 && pixelRatio <= 3.5

      return typicalLaptopResolution || (touchDesktopLike && highDensityPortable)
    },
    getDeviceType(device: DeviceResponse): string {
      if (device.deviceId == this.currentDeviceId) return this.detectDeviceType(device.deviceInfo.userAgent || "")

      const ua = (device.deviceInfo.userAgent || "").toLowerCase()
      if (!ua) return "Неизвестно"
      if (ua.includes("iphone")) return "iPhone"
      if (ua.includes("ipad")) return "iPad"
      if (ua.includes("android") && ua.includes("mobile")) return "Android телефон"
      if (ua.includes("android")) return "Android планшет"
      if (ua.includes("tablet")) return "Планшет"
      if (ua.includes("macintosh") || ua.includes("windows") || ua.includes("linux")) return "ПК/Ноутбук"
      return "Неизвестно"
    },
    isCurrentDevice(device: DeviceResponse): boolean {
      return device.deviceId == this.currentDeviceId
    },
    toggleNameEdit(device: DeviceResponse) {
      if (!this.isCurrentDevice(device)) return
      const shouldOpen = this.editingDeviceId != device.deviceId
      this.editingDeviceId = shouldOpen ? device.deviceId : ""
      this.displayNameInput = device.displayName ?? ""

      if (!shouldOpen) return
      this.$nextTick(() => {
        const input = document.getElementById(`name-input-${device.deviceId}`) as HTMLInputElement | null
        if (!input) return
        input.focus()
        input.select()
      })
    },
    canTransferMasterTo(device: DeviceResponse): boolean {
      if (!this.isCurrentMaster) return false
      if (this.isCurrentDevice(device)) return false
      if (device.isMaster) return false
      return true
    },
    getRoleDotTitle(device: DeviceResponse): string {
      if (device.isMaster) return "Мастер комнаты"
      if (this.canTransferMasterTo(device)) return "Передать мастера этому устройству"
      return "Не мастер"
    },
    handleRoleDotClick(device: DeviceResponse) {
      if (!this.canTransferMasterTo(device)) return
      this.transferMasterTo(device)
    },
    async transferMasterTo(device: DeviceResponse) {
      if (!this.currentDeviceId) return

      try {
        const room = await transferMaster(this.roomId, device.deviceId, this.currentDeviceId)
        this.devices = room.devices
        this.errorMessage = ""
      } catch (error) {
        const message = error instanceof Error ? error.message : "Неизвестная ошибка"
        this.errorMessage = `Не удалось передать мастера: ${message}`
      }
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

.devices-list {
  list-style: none;
  margin: 0;
  padding: 0;
  display: grid;
  gap: 10px;
}

.device-card {
  position: relative;
  border: 1px solid var(--border);
  border-radius: 12px;
  padding: 16px 14px 32px;
  background: rgba(15, 41, 31, 0.58);
}

.device-card--self {
  border-color: rgba(78, 228, 173, 0.7);
  box-shadow: 0 0 0 1px rgba(78, 228, 173, 0.25), 0 10px 24px rgba(7, 34, 23, 0.4);
}

.device-card p {
  margin: 0 0 7px;
}

.card-id {
  position: absolute;
  right: 10px;
  top: 8px;
  font-size: 12px;
  color: rgba(232, 236, 255, 0.5);
}

.device-name-row {
  margin: 0 0 8px;
  display: inline-flex;
  align-items: center;
  gap: 8px;
}

.device-name-text {
  font-weight: 700;
  color: var(--brand-strong);
}

.role-dot {
  position: absolute;
  left: 10px;
  top: 8px;
  width: 22px;
  height: 22px;
  border-radius: 999px;
  display: grid;
  place-items: center;
  border: 1px solid rgba(255, 213, 106, 0.5);
  color: #ffd56a;
  background: rgba(29, 20, 4, 0.9);
  cursor: pointer;
  line-height: 1;
  font-size: 13px;
}

.role-dot[disabled] {
  border-color: rgba(156, 167, 217, 0.35);
  color: transparent;
  background: rgba(156, 167, 217, 0.1);
  cursor: default;
}

.role-dot--master[disabled] {
  transform: translateX(-2px);
  border-color: rgba(255, 213, 106, 0.5);
  color: #ffd56a;
  background: rgba(255, 213, 106, 0.16);
}

.role-dot:not([disabled]) {
  color: #ffd56a;
  background: rgba(255, 213, 106, 0.16);
}

.name-edit-btn {
  width: 22px;
  height: 22px;
  border-radius: 6px;
  border: 1px solid var(--border);
  background: rgba(7, 23, 17, 0.85);
  color: var(--text-main);
  cursor: pointer;
  opacity: 0;
  pointer-events: none;
  transition: opacity 0.15s ease;
}

.device-name-row:hover .name-edit-btn {
  opacity: 1;
  pointer-events: auto;
}

.inline-editor {
  margin-top: 10px;
  display: flex;
  gap: 8px;
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

.name-input {
  flex: 1;
  border: 1px solid var(--border);
  border-radius: 10px;
  padding: 8px 10px;
  background: rgba(7, 23, 17, 0.75);
  color: var(--text-main);
}

.save-btn {
  border: 1px solid var(--border);
  border-radius: 10px;
  padding: 8px 12px;
  background: linear-gradient(180deg, var(--brand-strong), var(--brand));
  color: #052317;
  cursor: pointer;
  font-weight: 600;
}
</style>
