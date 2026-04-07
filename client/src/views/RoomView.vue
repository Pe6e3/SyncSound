<template>
  <main class="page-wrap">
    <section class="panel">
      <button class="back-btn" type="button" @click="goBackToRooms">Назад</button>
      <h1>Комната</h1>
      <p class="room-id">ID комнаты: {{ roomId }}</p>
      <div v-if="isCurrentMaster" class="audio-upload">
        <input
          class="audio-input"
          type="file"
          accept=".mp3,.wav,.ogg,.aac,.m4a,.flac,audio/mpeg,audio/wav,audio/x-wav,audio/ogg,audio/aac,audio/flac"
          @change="onAudioFileSelected"
        />
        <button class="save-btn" type="button" :disabled="!selectedAudioFile || isUploadingAudio" @click="uploadAudioFile">
          {{ isUploadingAudio ? "Загрузка..." : "Загрузить аудио" }}
        </button>
        <button class="save-btn" type="button" :disabled="!canPlayAudio" @click="sendPlaySignal">
          Play
        </button>
        <button class="save-btn" type="button" :disabled="!canMasterTransportControls" @click="sendPauseSignal">
          Пауза
        </button>
        <button class="save-btn" type="button" :disabled="!canMasterTransportControls" @click="sendStopSignal">
          Стоп
        </button>
      </div>
      <button v-if="pendingPlayAfterUnlock || pendingUnlockForAudioReady" class="save-btn" type="button" @click="unlockAudioPlayback">
        Активировать звук
      </button>
      <p v-if="audioStatusMessage" class="audio-status">{{ audioStatusMessage }}</p>
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
          <div class="card-id-block">
            <span class="card-id">{{ device.deviceId }}</span>
            <span class="card-timezone">{{ getTimezone(device.deviceInfo) }}</span>
          </div>

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
          <p class="device-type-line">{{ getDeviceType(device) }}</p>
          <p class="device-activity">Активен {{ formatActivity(device.firstSeenUtc) }}</p>
          <span :class="['audio-ready-dot', { 'audio-ready-dot--ready': device.isAudioReady }]" title="Готовность аудио"></span>

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
import { downloadRoomAudio, registerDevice, reportAudioReady, transferMaster, updateDeviceName, uploadRoomAudio, type DeviceResponse, type RoomDetailsResponse } from "@/api/roomApi"

const LOCAL_DEVICE_ID_KEY = "syncsound-device-id"

const WS_DEBUG = import.meta.env.DEV

function wsDebugLog(...args: unknown[]) {
  if (!WS_DEBUG) return
  console.log("[SyncSound WS]", ...args)
}

function wsReadyStateLabel(state: number): string {
  if (state === WebSocket.CONNECTING) return "CONNECTING"
  if (state === WebSocket.OPEN) return "OPEN"
  if (state === WebSocket.CLOSING) return "CLOSING"
  if (state === WebSocket.CLOSED) return "CLOSED"
  return String(state)
}

function summarizeRoomForLog(room: RoomDetailsResponse) {
  return {
    roomId: room.roomId,
    deviceCount: room.devices.length,
    audioRevision: room.audio.revision,
    hasAudio: room.audio.hasAudio
  }
}

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
      devices: [] as DeviceResponse[],
      currentDeviceId: "",
      displayNameInput: "",
      editingDeviceId: "",
      selectedAudioFile: null as File | null,
      isUploadingAudio: false,
      downloadedAudioRevision: 0,
      audioStatusMessage: "",
      audioObjectUrl: "",
      roomSocket: null as WebSocket | null,
      wsReconnectTimerId: 0 as number,
      wsReconnectAttempts: 0,
      isLeavingRoom: false,
      isAudioInteractionUnlocked: false,
      pendingPlayAfterUnlock: false,
      pendingUnlockForAudioReady: false,
      roomAudioPlayer: null as HTMLAudioElement | null,
      roomAudioPlayerBlobUrl: ""
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
    },
    hasAudioInRoom(): boolean {
      return this.downloadedAudioRevision > 0
    },
    areAllDevicesReady(): boolean {
      if (!this.devices.length) return false
      return this.devices.every(device => device.isAudioReady)
    },
    canPlayAudio(): boolean {
      if (!this.isCurrentMaster) return false
      if (!this.hasAudioInRoom) return false
      return this.areAllDevicesReady
    },
    canMasterTransportControls(): boolean {
      if (!this.isCurrentMaster) return false
      if (!this.hasAudioInRoom) return false
      return this.downloadedAudioRevision > 0
    }
  },
  methods: {
    logWsSnapshot(context: string) {
      if (!WS_DEBUG) return
      const main = this.roomSocket
      wsDebugLog(`снимок (${context})`, {
        roomId: this.roomId,
        deviceId: this.currentDeviceId,
        isLeavingRoom: this.isLeavingRoom,
        основнойСокет: main
          ? { readyState: wsReadyStateLabel(main.readyState), url: main.url }
          : null,
        reconnectTimerActive: this.wsReconnectTimerId !== 0,
        попытокПереподключения: this.wsReconnectAttempts
      })
    },
    async enterRoom() {
      try {
        this.isLeavingRoom = false
        this.pendingUnlockForAudioReady = false
        const localDeviceId = window.localStorage.getItem(LOCAL_DEVICE_ID_KEY) ?? undefined
        const response = await registerDevice(this.roomId, {
          deviceId: localDeviceId,
          displayName: this.displayNameInput || undefined,
          deviceInfo: collectDeviceInfo()
        })

        this.currentDeviceId = response.deviceId
        window.localStorage.setItem(LOCAL_DEVICE_ID_KEY, response.deviceId)
        this.devices = response.room.devices
        await this.syncAudioState(response.room)
        await this.playClick()
        this.connectRoomSocket()
        this.logWsSnapshot("после enterRoom")

        const currentDevice = this.devices.find(device => device.deviceId === response.deviceId)
        this.displayNameInput = currentDevice?.displayName ?? ""
        this.editingDeviceId = ""
        this.errorMessage = ""
      } catch (error) {
        const message = error instanceof Error ? error.message : "Неизвестная ошибка"
        this.errorMessage = `Комната недоступна: ${message}`
      }
    },
    async rejoinRoom() {
      if (!this.currentDeviceId) return

      wsDebugLog("rejoinRoom: повторная регистрация", { roomId: this.roomId, deviceId: this.currentDeviceId })
      try {
        const response = await registerDevice(this.roomId, {
          deviceId: this.currentDeviceId,
          displayName: this.displayNameInput || undefined,
          deviceInfo: collectDeviceInfo()
        })
        this.devices = response.room.devices
        await this.syncAudioState(response.room)
        this.connectRoomSocket()
        this.logWsSnapshot("после rejoinRoom")
      } catch {
        wsDebugLog("rejoinRoom: ошибка регистрации, планируется повтор")
        this.scheduleReconnect()
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
        await this.syncAudioState(room)
        this.errorMessage = ""
      } catch (error) {
        const message = error instanceof Error ? error.message : "Неизвестная ошибка"
        this.errorMessage = `Не удалось передать мастера: ${message}`
      }
    },
    onAudioFileSelected(event: Event) {
      const input = event.target as HTMLInputElement
      this.selectedAudioFile = input.files?.[0] ?? null
    },
    async uploadAudioFile() {
      if (!this.currentDeviceId || !this.selectedAudioFile) return

      this.isUploadingAudio = true
      try {
        const room = await uploadRoomAudio(this.roomId, this.currentDeviceId, this.selectedAudioFile)
        this.devices = room.devices
        await this.syncAudioState(room)
        this.audioStatusMessage = `Файл ${this.selectedAudioFile.name} загружен`
        this.selectedAudioFile = null
      } catch (error) {
        const message = error instanceof Error ? error.message : "Неизвестная ошибка"
        this.errorMessage = `Не удалось загрузить аудио: ${message}`
      } finally {
        this.isUploadingAudio = false
      }
    },
    async syncAudioState(room: RoomDetailsResponse) {
      if (!room.audio.hasAudio) return
      if (room.audio.revision <= this.downloadedAudioRevision) return

      try {
        const blob = await downloadRoomAudio(this.roomId)
        await this.cacheAudioBlob(room.audio.revision, blob, room.audio.fileName ?? undefined)
        this.downloadedAudioRevision = room.audio.revision
        await this.waitForRoomAudioCanPlay()
        if (this.currentDeviceId) await this.probePlaybackThenReportReady(room.audio.revision)
        await this.playClick()
        if (room.audio.fileName) this.audioStatusMessage = `Аудио локально: ${room.audio.fileName}`
      } catch (error) {
        const message = error instanceof Error ? error.message : "Неизвестная ошибка"
        this.errorMessage = `Не удалось скачать аудио: ${message}`
      }
    },
    async waitForRoomAudioCanPlay(timeoutMs = 25000): Promise<void> {
      const audio = this.getOrCreateRoomAudioPlayer()
      if (!this.audioObjectUrl) throw new Error("Нет локального аудио")
      if (audio.error) throw new Error(audio.error.message || "Ошибка загрузки аудио")
      if (audio.readyState >= HTMLMediaElement.HAVE_FUTURE_DATA) return

      await new Promise<void>((resolve, reject) => {
        const timer = window.setTimeout(() => {
          cleanup()
          reject(new Error("Таймаут загрузки аудио"))
        }, timeoutMs)
        const onCanPlay = () => {
          cleanup()
          resolve()
        }
        const onErr = () => {
          cleanup()
          const code = audio.error?.code
          const msg =
            code === MediaError.MEDIA_ERR_SRC_NOT_SUPPORTED
              ? "Формат не поддерживается этим браузером"
              : audio.error?.message || "Не удалось загрузить аудио"
          reject(new Error(msg))
        }
        const cleanup = () => {
          window.clearTimeout(timer)
          audio.removeEventListener("canplay", onCanPlay)
          audio.removeEventListener("error", onErr)
        }
        audio.addEventListener("canplay", onCanPlay, { once: true })
        audio.addEventListener("error", onErr, { once: true })
      })
    },
    async probePlaybackThenReportReady(revision: number) {
      if (!this.currentDeviceId || !this.audioObjectUrl) return

      try {
        const audio = this.getOrCreateRoomAudioPlayer()
        audio.volume = 1
        await audio.play()
        window.setTimeout(() => {
          audio.pause()
          audio.currentTime = 0
        }, 100)
        const updatedRoom = await reportAudioReady(this.roomId, this.currentDeviceId, revision)
        this.devices = updatedRoom.devices
        this.pendingUnlockForAudioReady = false
      } catch (error) {
        if (error instanceof DOMException && error.name === "NotAllowedError") {
          this.pendingUnlockForAudioReady = true
          this.audioStatusMessage = "Нажмите «Активировать звук», чтобы подтвердить готовность к воспроизведению."
          return
        }

        const message = error instanceof Error ? error.message : "Неизвестная ошибка"
        this.errorMessage = `Не удалось проверить воспроизведение: ${message}`
      }
    },
    sendConfirmAudioReadyViaWs() {
      const revision = this.downloadedAudioRevision
      if (!this.currentDeviceId || revision <= 0) return

      if (this.roomSocket && this.roomSocket.readyState === WebSocket.OPEN) {
        const payload = { type: "confirm-audio-ready", revision }
        wsDebugLog("исходящее сообщение", payload)
        this.roomSocket.send(JSON.stringify(payload))
        return
      }

      void reportAudioReady(this.roomId, this.currentDeviceId, revision)
        .then(updatedRoom => {
          this.devices = updatedRoom.devices
        })
        .catch(error => {
          const message = error instanceof Error ? error.message : "Неизвестная ошибка"
          this.errorMessage = `Не удалось подтвердить готовность аудио: ${message}`
        })
    },
    getAudioMimeType(fileName?: string): string {
      if (!fileName) return ""
      const extension = fileName.toLowerCase().split(".").pop() ?? ""
      if (extension === "mp3") return "audio/mpeg"
      if (extension === "wav") return "audio/wav"
      if (extension === "ogg") return "audio/ogg"
      if (extension === "aac") return "audio/aac"
      if (extension === "m4a") return "audio/mp4"
      if (extension === "flac") return "audio/flac"
      return ""
    },
    async cacheAudioBlob(revision: number, blob: Blob | ArrayBuffer | Uint8Array | string | unknown, fileName?: string) {
      const fallbackMimeType = this.getAudioMimeType(fileName)
      let normalizedBlob = blob instanceof Blob ? blob : new Blob([blob as BlobPart], fallbackMimeType ? { type: fallbackMimeType } : undefined)
      if (normalizedBlob instanceof Blob && !normalizedBlob.type && fallbackMimeType)
        normalizedBlob = new Blob([normalizedBlob], { type: fallbackMimeType })
      if (normalizedBlob.size <= 0) throw new Error("Пустой аудиофайл")
      const hasCacheStorage = typeof window !== "undefined" && "caches" in window
      if (hasCacheStorage) {
        const cache = await window.caches.open("syncsound-audio-cache")
        const request = new Request(`/local-audio/${this.roomId}/${revision}`)
        const response = new Response(normalizedBlob)
        await cache.put(request, response)
      }

      // Keep a local object URL for immediate playback on all clients.
      if (this.audioObjectUrl) URL.revokeObjectURL(this.audioObjectUrl)
      this.audioObjectUrl = URL.createObjectURL(normalizedBlob)
      if (this.roomAudioPlayer) {
        this.roomAudioPlayer.pause()
        this.roomAudioPlayer.src = this.audioObjectUrl
        this.roomAudioPlayerBlobUrl = this.audioObjectUrl
        void this.roomAudioPlayer.load()
      }
    },
    getOrCreateRoomAudioPlayer(): HTMLAudioElement {
      if (!this.roomAudioPlayer) {
        this.roomAudioPlayer = new Audio()
        this.roomAudioPlayer.preload = "auto"
        this.roomAudioPlayer.setAttribute("playsinline", "")
        this.roomAudioPlayer.setAttribute("webkit-playsinline", "true")
      }
      if (this.audioObjectUrl && this.roomAudioPlayerBlobUrl !== this.audioObjectUrl) {
        this.roomAudioPlayer.pause()
        this.roomAudioPlayer.src = this.audioObjectUrl
        this.roomAudioPlayerBlobUrl = this.audioObjectUrl
        void this.roomAudioPlayer.load()
      }
      return this.roomAudioPlayer
    },
    disposeRoomAudioPlayer() {
      if (!this.roomAudioPlayer) return
      this.roomAudioPlayer.pause()
      this.roomAudioPlayer.removeAttribute("src")
      void this.roomAudioPlayer.load()
      this.roomAudioPlayer = null
      this.roomAudioPlayerBlobUrl = ""
    },
    async playRoomAudio() {
      if (!this.audioObjectUrl) {
        this.errorMessage = "Локальный аудиофайл не готов к воспроизведению"
        return
      }

      const audio = this.getOrCreateRoomAudioPlayer()
      try {
        if (!audio.paused && !audio.ended) return
        if (audio.ended) audio.currentTime = 0
        await audio.play()
        this.pendingPlayAfterUnlock = false
      } catch (error) {
        if (error instanceof DOMException && error.name === "NotAllowedError") {
          this.pendingPlayAfterUnlock = true
          this.isAudioInteractionUnlocked = false
          this.audioStatusMessage = "iPhone блокирует автозапуск. Нажмите 'Активировать звук'."
          return
        }

        const message = error instanceof Error ? error.message : "Неизвестная ошибка"
        this.errorMessage = `Не удалось воспроизвести аудио: ${message}`
      }
    },
    pauseRoomAudio() {
      if (!this.roomAudioPlayer) return
      this.roomAudioPlayer.pause()
    },
    stopRoomAudio() {
      if (!this.audioObjectUrl) return
      const audio = this.getOrCreateRoomAudioPlayer()
      audio.pause()
      audio.currentTime = 0
    },
    unlockAudioPlayback() {
      const hadUnlockForReady = this.pendingUnlockForAudioReady
      const shouldPlayRoom = this.pendingPlayAfterUnlock

      const finishAfterPrime = () => {
        if (hadUnlockForReady) {
          this.pendingUnlockForAudioReady = false
          this.sendConfirmAudioReadyViaWs()
        }
        if (shouldPlayRoom) void this.playRoomAudio()

        if (!this.pendingPlayAfterUnlock)
          this.audioStatusMessage = shouldPlayRoom
            ? "Воспроизведение запущено."
            : "Готовность к звуку подтверждена, плеер готов к синхронному Play."
        else this.audioStatusMessage = "Не удалось начать воспроизведение. Нажмите «Активировать звук» ещё раз или коснитесь экрана."

        const warmupAudio = new Audio("/api/audio/click")
        warmupAudio.volume = 0
        void warmupAudio
          .play()
          .then(() => {
            warmupAudio.pause()
            warmupAudio.currentTime = 0
          })
          .catch(() => {})
      }

      try {
        if (!this.audioObjectUrl) {
          finishAfterPrime()
          return
        }

        const audio = this.getOrCreateRoomAudioPlayer()
        const playResult = audio.play()
        if (playResult === undefined) {
          finishAfterPrime()
          return
        }

        void playResult
          .then(() => {
            audio.pause()
            audio.currentTime = 0
            this.isAudioInteractionUnlocked = true
            this.pendingPlayAfterUnlock = false
            finishAfterPrime()
          })
          .catch(() => {
            this.audioStatusMessage =
              "Не удалось привязать плеер к касанию. Дождитесь загрузки трека или нажмите ещё раз."
            if (hadUnlockForReady) {
              this.pendingUnlockForAudioReady = false
              this.sendConfirmAudioReadyViaWs()
            }
          })
      } catch {
        this.audioStatusMessage = "Не удалось активировать звук. Повторите касание."
      }
    },
    installAudioUnlockListeners() {
      const unlockHandler = () => {
        if (!this.pendingPlayAfterUnlock && !this.pendingUnlockForAudioReady) return
        this.unlockAudioPlayback()
      }

      window.addEventListener("touchstart", unlockHandler, { passive: true })
      window.addEventListener("click", unlockHandler, { passive: true })
      ;(this as unknown as { _audioUnlockHandler?: () => void })._audioUnlockHandler = unlockHandler
    },
    removeAudioUnlockListeners() {
      const handler = (this as unknown as { _audioUnlockHandler?: () => void })._audioUnlockHandler
      if (!handler) return
      window.removeEventListener("touchstart", handler)
      window.removeEventListener("click", handler)
      ;(this as unknown as { _audioUnlockHandler?: () => void })._audioUnlockHandler = undefined
    },
    sendPlaySignal() {
      if (!this.canPlayAudio) return
      if (!this.roomSocket || this.roomSocket.readyState !== WebSocket.OPEN) {
        this.errorMessage = "Соединение WebSocket недоступно"
        wsDebugLog("sendPlaySignal: сокет недоступен", this.roomSocket ? wsReadyStateLabel(this.roomSocket.readyState) : "null")
        return
      }

      const payload = {
        type: "play-audio",
        revision: this.downloadedAudioRevision
      }
      wsDebugLog("исходящее сообщение", payload)
      this.roomSocket.send(JSON.stringify(payload))
    },
    sendMasterTransportCommand(type: "pause-audio" | "stop-audio") {
      if (!this.canMasterTransportControls) return
      if (!this.roomSocket || this.roomSocket.readyState !== WebSocket.OPEN) {
        this.errorMessage = "Соединение WebSocket недоступно"
        return
      }

      const payload = { type, revision: this.downloadedAudioRevision }
      wsDebugLog("исходящее сообщение", payload)
      this.roomSocket.send(JSON.stringify(payload))
    },
    sendPauseSignal() {
      this.sendMasterTransportCommand("pause-audio")
    },
    sendStopSignal() {
      this.sendMasterTransportCommand("stop-audio")
    },
    async playClick() {
      try {
        const response = await fetch("/api/audio/click")
        if (!response.ok) return
        const blob = await response.blob()
        const objectUrl = URL.createObjectURL(blob)
        const audio = new Audio(objectUrl)
        audio.volume = 0.35
        await audio.play()
        window.setTimeout(() => URL.revokeObjectURL(objectUrl), 5000)
      } catch {
        // Browser can block autoplay before first gesture - silent fail.
      }
    },
    buildRoomSocketUrl(): string {
      const protocol = window.location.protocol === "https:" ? "wss" : "ws"
      const encodedRoomId = encodeURIComponent(this.roomId)
      const encodedDeviceId = encodeURIComponent(this.currentDeviceId)
      return `${protocol}://${window.location.host}/ws/rooms/${encodedRoomId}?deviceId=${encodedDeviceId}`
    },
    connectRoomSocket() {
      if (!this.currentDeviceId) return
      if (this.isLeavingRoom) {
        wsDebugLog("connectRoomSocket: пропуск (выход из комнаты)")
        return
      }
      if (this.roomSocket && (this.roomSocket.readyState === WebSocket.OPEN || this.roomSocket.readyState === WebSocket.CONNECTING)) {
        wsDebugLog("connectRoomSocket: уже есть активное соединение", wsReadyStateLabel(this.roomSocket.readyState))
        return
      }

      const url = this.buildRoomSocketUrl()
      wsDebugLog("создание WebSocket", { url })
      const socket = new WebSocket(url)
      this.roomSocket = socket
      this.logWsSnapshot("сразу после new WebSocket")

      socket.onopen = () => {
        wsDebugLog("событие open", { url: socket.url, readyState: wsReadyStateLabel(socket.readyState) })
        this.wsReconnectAttempts = 0
        this.clearReconnectTimer()
      }

      socket.onmessage = async event => {
        const raw = typeof event.data === "string" ? event.data : "[binary]"
        try {
          const payload = JSON.parse(event.data as string) as { type?: string; room?: RoomDetailsResponse; revision?: number }
          if (payload.type === "room-state" && payload.room) {
            wsDebugLog("входящее room-state", summarizeRoomForLog(payload.room), { rawLength: raw.length })
            this.devices = payload.room.devices
            await this.syncAudioState(payload.room)
            return
          }

          if (payload.type === "play-audio") {
            wsDebugLog("входящее play-audio", { revision: payload.revision })
            await this.playRoomAudio()
            return
          }

          if (payload.type === "pause-audio") {
            wsDebugLog("входящее pause-audio", { revision: payload.revision })
            this.pauseRoomAudio()
            return
          }

          if (payload.type === "stop-audio") {
            wsDebugLog("входящее stop-audio", { revision: payload.revision })
            this.stopRoomAudio()
            return
          }

          wsDebugLog("входящее сообщение (неизвестный type)", { rawPreview: raw.length > 200 ? `${raw.slice(0, 200)}…` : raw })
        } catch {
          wsDebugLog("входящее сообщение (не JSON)", { rawPreview: typeof raw === "string" && raw.length > 200 ? `${raw.slice(0, 200)}…` : raw })
        }
      }

      socket.onerror = () => {
        wsDebugLog("событие error", { url: socket.url, readyState: wsReadyStateLabel(socket.readyState) })
      }

      socket.onclose = (event: CloseEvent) => {
        wsDebugLog("событие close", {
          code: event.code,
          reason: event.reason || "(нет)",
          wasClean: event.wasClean,
          url: socket.url
        })
        if (this.roomSocket === socket) this.roomSocket = null
        this.logWsSnapshot("после onclose")
        if (this.isLeavingRoom) {
          wsDebugLog("onclose: без переподключения (выход из комнаты)")
          return
        }
        this.scheduleReconnect()
      }
    },
    scheduleReconnect() {
      if (!this.currentDeviceId) return
      if (this.wsReconnectTimerId) return

      this.wsReconnectAttempts += 1
      const delayMs = Math.min(15000, 1000 * 2 ** (this.wsReconnectAttempts - 1))
      wsDebugLog("план переподключения", { попытка: this.wsReconnectAttempts, delayMs })
      this.wsReconnectTimerId = window.setTimeout(() => {
        this.wsReconnectTimerId = 0
        this.rejoinRoom()
      }, delayMs)
    },
    clearReconnectTimer() {
      if (!this.wsReconnectTimerId) return
      window.clearTimeout(this.wsReconnectTimerId)
      this.wsReconnectTimerId = 0
    },
    disconnectRoomSocket() {
      wsDebugLog("disconnectRoomSocket: закрытие соединения")
      this.clearReconnectTimer()
      this.wsReconnectAttempts = 0
      if (!this.roomSocket) return
      const socket = this.roomSocket
      wsDebugLog("close() вызван", { readyState: wsReadyStateLabel(socket.readyState), url: socket.url })
      this.roomSocket.close()
      this.roomSocket = null
      this.logWsSnapshot("после disconnectRoomSocket")
    },
    goBackToRooms() {
      wsDebugLog("Назад: выход из комнаты, закрытие WS")
      this.isLeavingRoom = true
      this.disconnectRoomSocket()
      this.$router.push("/sound")
    }
  },
  mounted() {
    this.installAudioUnlockListeners()
    this.enterRoom()
  },
  beforeUnmount() {
    this.removeAudioUnlockListeners()
    wsDebugLog("beforeUnmount: размонтирование комнаты")
    this.disconnectRoomSocket()
    this.disposeRoomAudioPlayer()
    if (this.audioObjectUrl) URL.revokeObjectURL(this.audioObjectUrl)
  },
  watch: {
    roomId() {
      this.disconnectRoomSocket()
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

.audio-ready-dot {
  position: absolute;
  right: 10px;
  bottom: 8px;
  width: 12px;
  height: 12px;
  border-radius: 999px;
  border: 1px solid rgba(180, 191, 235, 0.7);
  background: rgba(180, 191, 235, 0.12);
}

.audio-ready-dot--ready {
  border-color: rgba(80, 242, 155, 0.95);
  background: rgba(80, 242, 155, 0.95);
  box-shadow: 0 0 8px rgba(80, 242, 155, 0.5);
}

.device-card--self {
  border-color: rgba(78, 228, 173, 0.7);
  box-shadow: 0 0 0 1px rgba(78, 228, 173, 0.25), 0 10px 24px rgba(7, 34, 23, 0.4);
}

.device-card p {
  margin: 0 0 7px;
}

.card-id-block {
  position: absolute;
  right: 10px;
  top: 8px;
  display: flex;
  flex-direction: column;
  align-items: flex-end;
  gap: 2px;
  text-align: right;
}

.card-id {
  font-size: 12px;
  color: rgba(232, 236, 255, 0.5);
}

.card-timezone {
  font-size: 11px;
  color: rgba(232, 236, 255, 0.38);
}

.device-type-line {
  margin: 0 0 7px;
  font-size: 13px;
  color: rgba(232, 236, 255, 0.72);
}

.device-activity {
  margin: 0 0 7px;
  font-size: 13px;
  color: var(--text-muted);
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
  border-color: rgba(255, 213, 106, 0.5);
  color: #ffd56a;
  background: rgba(255, 213, 106, 0.16);
}
.role-dot--master[disabled] span{
  transform: translateX(-2px);
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

.audio-upload {
  margin-top: 12px;
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  justify-content: center;
}

.audio-input {
  max-width: 320px;
}

.audio-status {
  margin-top: 8px;
  color: var(--text-muted);
  font-size: 13px;
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
