<template>
  <main class="page-wrap">
    <section class="panel">
      <button class="back-btn" type="button" @click="goBackToRooms">Назад</button>
      <h1>Комната</h1>
      <p class="room-id">ID комнаты: {{ roomId }}</p>
      <p v-if="!isCurrentMaster && currentDeviceId" class="master-hint">
        Загрузка аудио, калибровка и Play — только у мастера комнаты (звёздочка ★).
      </p>
      <div v-if="isCurrentMaster" class="audio-upload">
        <input
          class="audio-input"
          type="file"
          accept=".mp3,.wav,.ogg,.aac,.m4a,.flac,audio/mpeg,audio/wav,audio/x-wav,audio/ogg,audio/aac,audio/flac"
          @change="onAudioFileSelected"
        />
        <button class="save-btn" type="button" :disabled="!selectedAudioFile || isUploadingAudio" @click="uploadAudioFile">
          {{ uploadButtonLabel }}
        </button>
        <button
          class="save-btn"
          type="button"
          :disabled="!canStartPlaybackCalibration"
          :title="calibrationBlockedReason || 'Запустить калибровку задержки'"
          @click="runPlaybackCalibration"
        >
          {{ isSyncCalibrating ? "Калибровка…" : "Калибровка" }}
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
        <p v-if="calibrationBlockedReason && !isSyncCalibrating" class="calibration-hint">{{ calibrationBlockedReason }}</p>
      </div>
      <div v-if="activeAudioTransfer" class="audio-transfer-panel">
        <div class="audio-transfer-head">
          <span>{{ activeAudioTransfer.kind === "upload" ? "Загрузка на сервер" : "Скачивание аудио" }}</span>
          <span v-if="activeAudioTransfer.label" class="audio-transfer-pct">{{ activeAudioTransfer.label }}</span>
        </div>
        <div
          :class="['audio-transfer-track', { 'audio-transfer-track--busy': activeAudioTransfer.indeterminate }]"
          role="progressbar"
          :aria-valuenow="activeAudioTransfer.indeterminate ? undefined : activeAudioTransfer.value ?? 0"
          aria-valuemin="0"
          aria-valuemax="100"
        >
          <div
            class="audio-transfer-fill"
            :style="{ width: activeAudioTransfer.indeterminate ? '100%' : `${activeAudioTransfer.value ?? 0}%` }"
          />
        </div>
      </div>
      <div v-if="isCurrentMaster && isSyncCalibrating" class="calibration-wave-panel">
        <p class="calibration-wave-label">Сигнал с микрофона</p>
        <div class="calibration-wave-wrap">
          <canvas ref="calibrationWaveCanvas" class="calibration-wave-canvas" />
        </div>
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

          <p class="device-title-row">
            <span class="device-type-line">{{ getDeviceType(device) }}</span>
            <span v-if="device.displayName" class="device-name-text">{{ device.displayName }}</span>
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
          <p class="device-lag-line">Задержка: {{ formatPlaybackLag(device) }}</p>
          <p class="device-activity-corner">{{ formatActivity(device.firstSeenUtc) }}</p>
          <div class="device-indicators" aria-hidden="true">
            <span
              :class="['audio-ready-dot', { 'audio-ready-dot--ready': device.isAudioReady }]"
              title="Аудио загружено и готово"
            />
            <span
              :class="['sync-ready-dot', { 'sync-ready-dot--ready': device.isPlaybackSyncReady }]"
              :title="device.isMaster ? 'Мастер: опорная задержка 0' : 'Калибровка синхронизации'"
            />
          </div>

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
import {
  averageLast,
  fetchServerTimeSkewMs,
  formatMicrophoneOpenError,
  isStableWithinMs,
  measureLagMsAfterSendWithSession,
  openCalibrationMicSession,
  playSyncTonePattern,
  resumeOrCreateAudioContext,
  isMicrophoneContextOk,
  startMicWaveformAnimation,
  type CalibrationMicSession
} from "@/utils/syncCalibration"
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
    hasAudio: room.audio.hasAudio,
    isCalibrationLocked: room.isCalibrationLocked
  }
}

function sleep(ms: number): Promise<void> {
  return new Promise(resolve => window.setTimeout(resolve, ms))
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
      isFetchingRoomAudio: false,
      audioUploadPercent: null as number | null,
      audioDownloadPercent: null as number | null,
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
      roomAudioPlayerBlobUrl: "",
      calibrationAudioContext: null as AudioContext | null,
      isSyncCalibrating: false,
      calibrationWaveformStop: null as (() => void) | null,
      serverTimeSkewMs: 0 as number,
      roomCalibrationLocked: false
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
    /** Ведомые с готовым аудио (без требования isOnline — иначе кнопка часто недоступна до синхр. состояния WS). */
    slaveDevicesForCalibration(): DeviceResponse[] {
      return this.orderedDevices.filter(device => !device.isMaster && device.isAudioReady)
    },
    areAllDevicesPlaybackSyncReady(): boolean {
      if (!this.devices.length) return false
      return this.devices.every(device => device.isPlaybackSyncReady)
    },
    microphoneContextOk(): boolean {
      return isMicrophoneContextOk()
    },
    uploadButtonLabel(): string {
      if (!this.isUploadingAudio) return "Загрузить аудио"
      if (this.audioUploadPercent !== null) return `Загрузка ${this.audioUploadPercent}%`
      return "Загрузка…"
    },
    activeAudioTransfer(): {
      kind: "upload" | "download"
      value: number
      label: string
      indeterminate: boolean
    } | null {
      if (this.isUploadingAudio)
        return {
          kind: "upload",
          value: this.audioUploadPercent ?? 0,
          label: this.audioUploadPercent !== null ? `${this.audioUploadPercent}%` : "",
          indeterminate: this.audioUploadPercent === null
        }
      if (this.isFetchingRoomAudio)
        return {
          kind: "download",
          value: this.audioDownloadPercent ?? 0,
          label: this.audioDownloadPercent !== null ? `${this.audioDownloadPercent}%` : "",
          indeterminate: this.audioDownloadPercent === null
        }
      return null
    },
    canStartPlaybackCalibration(): boolean {
      if (!this.isCurrentMaster) return false
      if (!this.microphoneContextOk) return false
      if (!this.areAllDevicesReady) return false
      if (this.isSyncCalibrating) return false
      if (!this.slaveDevicesForCalibration.length) return false
      return true
    },
    calibrationBlockedReason(): string {
      if (!this.isCurrentMaster) return ""
      if (this.isSyncCalibrating) return ""
      if (!this.microphoneContextOk)
        return "Калибровка недоступна: микрофон в браузере работает только по HTTPS или на localhost. По http:// с IP-адреса (например 192.168.…) запрос доступа не появится — поднимите HTTPS или откройте клиент с https:// или http://localhost."
      if (!this.devices.length) return "Нет устройств в данных комнаты."
      if (!this.areAllDevicesReady)
        return "Дождитесь зелёного индикатора аудио у всех устройств (включая вас и ведомых)."
      if (!this.slaveDevicesForCalibration.length)
        return "Нужен хотя бы один ведомый с готовым аудио: откройте комнату на втором устройстве и дождитесь загрузки трека."
      return ""
    },
    canPlayAudio(): boolean {
      if (!this.isCurrentMaster) return false
      if (!this.hasAudioInRoom) return false
      if (!this.areAllDevicesReady) return false
      return this.areAllDevicesPlaybackSyncReady
    },
    canMasterTransportControls(): boolean {
      if (!this.isCurrentMaster) return false
      if (!this.hasAudioInRoom) return false
      return this.downloadedAudioRevision > 0
    }
  },
  methods: {
    logRoomCalibrationState(context: string) {
      const master = this.currentDevice
      const slaves = this.slaveDevicesForCalibration
      console.log(`[SyncSound] ${context}`, {
        roomId: this.roomId,
        deviceId: this.currentDeviceId,
        isMaster: Boolean(master?.isMaster),
        audioReadyThis: Boolean(master?.isAudioReady),
        rev: this.downloadedAudioRevision,
        devices: this.devices.map(d => ({
          id: d.deviceId.slice(0, 8),
          master: d.isMaster,
          audio: d.isAudioReady,
          online: d.isOnline,
          sync: d.isPlaybackSyncReady
        })),
        slavesForCalibration: slaves.length,
        canCalibrate: this.canStartPlaybackCalibration,
        hint: this.calibrationBlockedReason || "калибровка доступна"
      })
    },
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
        this.roomCalibrationLocked = Boolean(response.room.isCalibrationLocked)
        await this.syncAudioState(response.room)
        await this.playClick()
        this.connectRoomSocket()
        this.logWsSnapshot("после enterRoom")
        this.$nextTick(() => this.logRoomCalibrationState("после enterRoom"))

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
        this.roomCalibrationLocked = Boolean(response.room.isCalibrationLocked)
        await this.syncAudioState(response.room)
        this.connectRoomSocket()
        this.logWsSnapshot("после rejoinRoom")
        this.$nextTick(() => this.logRoomCalibrationState("после rejoinRoom"))
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
        this.roomCalibrationLocked = Boolean(room.isCalibrationLocked)
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
    formatPlaybackLag(device: DeviceResponse): string {
      if (device.isMaster) return "0 ms (опорное)"
      if (!device.isPlaybackSyncReady) return "—"
      const v = device.playbackSyncLagMs
      if (typeof v !== "number" || Number.isNaN(v)) return "—"
      return `${(Math.round(v * 100) / 100).toFixed(2)} ms`
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
        this.roomCalibrationLocked = Boolean(room.isCalibrationLocked)
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
      this.audioUploadPercent = null
      try {
        const file = this.selectedAudioFile
        const room = await uploadRoomAudio(this.roomId, this.currentDeviceId, file, (loaded, total) => {
          if (total !== null && total > 0) this.audioUploadPercent = Math.min(100, Math.round((loaded / total) * 100))
          else this.audioUploadPercent = null
        })
        this.audioUploadPercent = 100
        this.devices = room.devices
        this.roomCalibrationLocked = Boolean(room.isCalibrationLocked)
        this.isUploadingAudio = false
        this.audioUploadPercent = null
        await this.syncAudioState(room)
        this.audioStatusMessage = `Файл ${file.name} загружен`
        this.selectedAudioFile = null
      } catch (error) {
        const message = error instanceof Error ? error.message : "Неизвестная ошибка"
        this.errorMessage = `Не удалось загрузить аудио: ${message}`
      } finally {
        this.isUploadingAudio = false
        this.audioUploadPercent = null
      }
    },
    async syncAudioState(room: RoomDetailsResponse) {
      if (!room.audio.hasAudio) return
      if (room.audio.revision <= this.downloadedAudioRevision) return

      this.isFetchingRoomAudio = true
      this.audioDownloadPercent = null
      try {
        const blob = await downloadRoomAudio(this.roomId, percent => {
          this.audioDownloadPercent = percent
        })
        await this.cacheAudioBlob(room.audio.revision, blob, room.audio.fileName ?? undefined)
        this.downloadedAudioRevision = room.audio.revision
        await this.waitForRoomAudioCanPlay()
        if (this.currentDeviceId) await this.probePlaybackThenReportReady(room.audio.revision)
        await this.playClick()
        if (room.audio.fileName) this.audioStatusMessage = `Аудио локально: ${room.audio.fileName}`
        this.errorMessage = ""
      } catch (error) {
        const message = error instanceof Error ? error.message : "Неизвестная ошибка"
        this.errorMessage = `Не удалось скачать аудио: ${message}`
      } finally {
        this.isFetchingRoomAudio = false
        this.audioDownloadPercent = null
      }
    },
    async waitForRoomAudioCanPlay(timeoutMs = 25000): Promise<void> {
      const audio = this.getOrCreateRoomAudioPlayer()
      if (!this.audioObjectUrl) throw new Error("Нет локального аудио")
      if (audio.readyState >= HTMLMediaElement.HAVE_FUTURE_DATA) return

      await new Promise<void>((resolve, reject) => {
        const timer = window.setTimeout(() => {
          cleanup()
          if (audio.readyState >= HTMLMediaElement.HAVE_FUTURE_DATA) {
            resolve()
            return
          }
          const code = audio.error?.code
          const msg =
            code === MediaError.MEDIA_ERR_SRC_NOT_SUPPORTED
              ? "Формат не поддерживается этим браузером"
              : audio.error?.message || "Таймаут загрузки аудио"
          reject(new Error(msg))
        }, timeoutMs)
        const onCanPlay = () => {
          cleanup()
          resolve()
        }
        const onErr = () => {
          requestAnimationFrame(() => {
            requestAnimationFrame(() => {
              if (audio.readyState >= HTMLMediaElement.HAVE_CURRENT_DATA) {
                cleanup()
                resolve()
              }
            })
          })
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
        this.roomCalibrationLocked = Boolean(updatedRoom.isCalibrationLocked)
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
          this.roomCalibrationLocked = Boolean(updatedRoom.isCalibrationLocked)
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
    async refreshServerSkew() {
      try {
        this.serverTimeSkewMs = await fetchServerTimeSkewMs()
      } catch {
        /* scheduled play falls back to skew 0 */
      }
    },
    async schedulePlayRoomAudio(serverStartMs: number, maxSyncLagMs: number) {
      await this.refreshServerSkew()
      const skew = this.serverTimeSkewMs
      const device = this.currentDevice
      const myLag =
        device?.isMaster ? 0 : typeof device?.playbackSyncLagMs === "number" ? device.playbackSyncLagMs : 0
      const leadMs = Math.max(0, maxSyncLagMs - myLag)
      let waitMs = serverStartMs - skew - Date.now() - leadMs
      if (waitMs < 0) waitMs = 0

      wsDebugLog("запланирован Play", { serverStartMs, maxSyncLagMs, myLag, leadMs, waitMs, skew })
      window.setTimeout(() => void this.playRoomAudio(), waitMs)
    },
    disposeCalibrationAudioContext() {
      const ctx = this.calibrationAudioContext
      if (!ctx) return
      void ctx.close().catch(() => {})
      this.calibrationAudioContext = null
    },
    async handleIncomingSyncTone(payload: { targetDeviceId?: string; sessionId?: string; iteration?: number }) {
      if (!payload.targetDeviceId || payload.targetDeviceId !== this.currentDeviceId) return
      console.log("[SyncSound калибровка] Ведомое: воспроизвожу эталонный тон", {
        deviceId: this.currentDeviceId,
        sessionId: payload.sessionId,
        iteration: payload.iteration
      })
      try {
        this.calibrationAudioContext = await resumeOrCreateAudioContext(this.calibrationAudioContext)
        playSyncTonePattern(this.calibrationAudioContext)
      } catch (error) {
        console.warn("[SyncSound калибровка] ведомое не смогло воспроизвести эталонный тон", error)
        this.pendingUnlockForAudioReady = true
        if (!this.audioStatusMessage)
          this.audioStatusMessage = "Калибровка: нажмите «Активировать звук», затем запустите калибровку снова."
      }
    },
    sendSyncLatencyReportWs(targetDeviceId: string, lagMs: number) {
      if (!this.roomSocket || this.roomSocket.readyState !== WebSocket.OPEN) return
      const payload = { type: "sync-latency-report", deviceId: targetDeviceId, lagMs }
      wsDebugLog("исходящее сообщение", payload)
      this.roomSocket.send(JSON.stringify(payload))
    },
    sendCalibrationLockMessage(messageType: "start-calibration") {
      if (!this.roomSocket || this.roomSocket.readyState !== WebSocket.OPEN) return
      const payload = { type: messageType }
      wsDebugLog("исходящее сообщение", payload)
      this.roomSocket.send(JSON.stringify(payload))
    },
    stopCalibrationWaveform() {
      if (!this.calibrationWaveformStop) return
      this.calibrationWaveformStop()
      this.calibrationWaveformStop = null
    },
    async runPlaybackCalibration() {
      if (!this.canStartPlaybackCalibration) return
      if (!this.roomSocket || this.roomSocket.readyState !== WebSocket.OPEN) {
        this.errorMessage = "Для калибровки нужен активный WebSocket."
        return
      }

      let calibrationLockSent = false
      let micSession: CalibrationMicSession | null = null
      this.isSyncCalibrating = true
      this.errorMessage = ""
      this.audioStatusMessage =
        "Калибровка: разрешите доступ к микрофону в запросе браузера; держите ведомое устройство рядом."

      try {
        try {
          micSession = await openCalibrationMicSession()
          console.log("[SyncSound калибровка] Микрофон открыт (один поток на всю серию замеров).")
          await this.$nextTick()
          const waveCanvas = this.$refs.calibrationWaveCanvas
          if (waveCanvas instanceof HTMLCanvasElement)
            this.calibrationWaveformStop = startMicWaveformAnimation(waveCanvas, micSession.visualAnalyser)
        } catch (error) {
          this.errorMessage = formatMicrophoneOpenError(error)
          this.audioStatusMessage = ""
          console.warn("[SyncSound калибровка] микрофон недоступен", error)
          return
        }

        this.sendCalibrationLockMessage("start-calibration")
        calibrationLockSent = true
        await sleep(250)
        console.log("[SyncSound калибровка] Начинаю измерения задержки по очереди для каждого ведомого устройства.")

        try {
          this.calibrationAudioContext = await resumeOrCreateAudioContext(this.calibrationAudioContext)
        } catch {
          /* дальше попытаемся снова на первом тоне */
        }

        for (const slave of this.slaveDevicesForCalibration) {
          console.log(`[SyncSound калибровка] --- Устройство ${slave.deviceId}: серия замеров ---`)
          const samples: number[] = []
          const maxAttempts = 32
          let micFailureAbort = false

          for (let attempt = 0; attempt < maxAttempts; attempt++) {
            const sessionId = `${slave.deviceId}-${attempt}-${Date.now()}`
            const body = {
              type: "sync-tone-start",
              targetDeviceId: slave.deviceId,
              sessionId,
              iteration: attempt + 1
            }
            const json = JSON.stringify(body)
            const sendPerf = performance.now()
            this.roomSocket.send(json)

            try {
              const lagMs = await measureLagMsAfterSendWithSession(sendPerf, micSession!)
              samples.push(lagMs)
              console.log(
                `[SyncSound калибровка] ${slave.deviceId}: замер ${samples.length}/${maxAttempts}, задержка ${lagMs.toFixed(2)} ms (цель: 5 замеров подряд с разбросом ≤10 ms)`
              )
              if (samples.length >= 5 && isStableWithinMs(samples, 10)) break
            } catch (error) {
              console.warn(`[SyncSound калибровка] ${slave.deviceId}: замер ${attempt + 1} не удался`, error)
              const msg = error instanceof Error ? error.message : ""
              const isMicAccessLost =
                msg.includes("getUserMedia") ||
                msg.startsWith("MIC_") ||
                msg === "WEB_AUDIO_UNAVAILABLE" ||
                msg.includes("NotReadableError") ||
                msg.includes("TrackStartError") ||
                msg.includes("NotAllowedError") ||
                msg.includes("PermissionDeniedError")
              if (isMicAccessLost) {
                micFailureAbort = true
                break
              }
            }

            await sleep(420)
          }

          if (micFailureAbort) {
            this.errorMessage = "Потерян доступ к микрофону во время калибровки. Запустите снова и не закрывайте разрешение."
            return
          }

          if (samples.length < 5) {
            this.errorMessage = `Не удалось стабилизировать задержку для ${slave.deviceId}. Проверьте громкость ведомого и положение микрофона.`
            return
          }

          if (!isStableWithinMs(samples, 10))
            console.warn(
              `[SyncSound калибровка] ${slave.deviceId}: разброс последних 5 замеров > 10 ms — используем среднее последних 5`
            )

          const avgLag = averageLast(samples, 5)
          console.log(
            `[SyncSound калибровка] ${slave.deviceId}: итоговая задержка (в карточку на сервер) ${avgLag.toFixed(2)} ms`
          )
          this.sendSyncLatencyReportWs(slave.deviceId, avgLag)
          await sleep(280)
        }

        console.log("[SyncSound калибровка] Все ведомые обработаны успешно.")
        this.audioStatusMessage = "Калибровка синхронизации завершена. Можно нажимать Play."
      } finally {
        this.stopCalibrationWaveform()
        if (micSession) {
          micSession.stop()
          micSession = null
        }
        if (calibrationLockSent) void calibrationLockSent
        this.isSyncCalibrating = false
      }
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
            this.roomCalibrationLocked = Boolean(payload.room.isCalibrationLocked)
            await this.syncAudioState(payload.room)
            this.$nextTick(() => this.logRoomCalibrationState("после room-state (WS)"))
            return
          }

          if (payload.type === "sync-tone-start") {
            const tonePayload = payload as { targetDeviceId?: string; sessionId?: string; iteration?: number }
            wsDebugLog("входящее sync-tone-start", tonePayload)
            await this.handleIncomingSyncTone(tonePayload)
            return
          }

          if (payload.type === "play-audio") {
            const playPayload = payload as {
              revision?: number
              serverStartMs?: number
              maxSyncLagMs?: number
            }
            wsDebugLog("входящее play-audio", playPayload)
            if (typeof playPayload.serverStartMs === "number" && typeof playPayload.maxSyncLagMs === "number")
              await this.schedulePlayRoomAudio(playPayload.serverStartMs, playPayload.maxSyncLagMs)
            else await this.playRoomAudio()
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
    this.disposeCalibrationAudioContext()
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

.master-hint {
  margin: 0 0 12px;
  font-size: 14px;
  color: var(--text-muted);
  line-height: 1.4;
}

.calibration-hint {
  flex: 1 1 100%;
  margin: 4px 0 0;
  font-size: 13px;
  color: rgba(200, 214, 255, 0.85);
  line-height: 1.35;
  text-align: center;
}

.calibration-banner {
  margin: 0 0 12px;
  padding: 10px 12px;
  border-radius: 12px;
  border: 1px solid rgba(255, 196, 94, 0.55);
  background: rgba(60, 40, 8, 0.45);
  color: rgba(255, 224, 170, 0.95);
  font-size: 14px;
  line-height: 1.35;
}

.device-lag-line {
  margin: 0 0 8px;
  font-size: 12px;
  color: rgba(180, 200, 255, 0.78);
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

.device-indicators {
  position: absolute;
  right: 10px;
  bottom: 8px;
  display: inline-flex;
  align-items: center;
  gap: 6px;
}

.audio-ready-dot {
  display: inline-block;
  width: 12px;
  height: 12px;
  border-radius: 999px;
  border: 1px solid rgba(180, 191, 235, 0.7);
  background: rgba(180, 191, 235, 0.12);
}

.sync-ready-dot {
  display: inline-block;
  width: 12px;
  height: 12px;
  border-radius: 999px;
  border: 1px solid rgba(147, 178, 255, 0.55);
  background: rgba(147, 178, 255, 0.12);
}

.sync-ready-dot--ready {
  border-color: rgba(120, 190, 255, 0.95);
  background: rgba(120, 190, 255, 0.9);
  box-shadow: 0 0 8px rgba(120, 190, 255, 0.45);
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

.device-title-row {
  margin: 0 0 8px;
  display: inline-flex;
  align-items: center;
  gap: 8px;
}

.device-type-line {
  margin: 0;
  font-size: 13px;
  color: rgba(232, 236, 255, 0.72);
}

.device-name-text {
  font-weight: 700;
  color: var(--brand-strong);
}

.device-activity-corner {
  position: absolute;
  right: 46px;
  bottom: 7px;
  margin: 0;
  font-size: 12px;
  color: var(--text-muted);
  line-height: 1;
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

.device-title-row:hover .name-edit-btn {
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

.calibration-wave-panel {
  margin-top: 16px;
  width: 100%;
  text-align: center;
}

.audio-transfer-panel {
  margin-top: 14px;
  width: 100%;
  max-width: 520px;
  margin-left: auto;
  margin-right: auto;
  text-align: left;
}

.audio-transfer-head {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-bottom: 8px;
  font-size: 13px;
  color: rgba(190, 220, 205, 0.9);
}

.audio-transfer-pct {
  font-variant-numeric: tabular-nums;
  color: rgba(140, 255, 200, 0.95);
}

.audio-transfer-track {
  height: 10px;
  border-radius: 999px;
  background: rgba(0, 0, 0, 0.32);
  border: 1px solid rgba(100, 160, 130, 0.35);
  overflow: hidden;
}

.audio-transfer-fill {
  height: 100%;
  border-radius: inherit;
  background: linear-gradient(90deg, rgba(60, 160, 110, 0.88), rgba(110, 255, 185, 0.95));
  transition: width 0.12s ease-out;
}

.audio-transfer-track--busy .audio-transfer-fill {
  background: linear-gradient(
    90deg,
    rgba(45, 95, 70, 0.55) 0%,
    rgba(110, 255, 185, 0.8) 45%,
    rgba(45, 95, 70, 0.55) 100%
  );
  background-size: 220% 100%;
  animation: audio-transfer-shimmer 1s linear infinite;
}

@keyframes audio-transfer-shimmer {
  0% {
    background-position: 220% 0;
  }
  100% {
    background-position: -220% 0;
  }
}

.calibration-wave-label {
  margin: 0 0 8px;
  font-size: 13px;
  color: rgba(190, 220, 205, 0.9);
}

.calibration-wave-wrap {
  width: 100%;
  min-height: 132px;
  border-radius: 12px;
  border: 1px solid rgba(100, 160, 130, 0.35);
  overflow: hidden;
  background: rgba(4, 14, 10, 0.65);
}

.calibration-wave-canvas {
  display: block;
  width: 100%;
  vertical-align: middle;
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

.save-btn:disabled {
  opacity: 0.55;
  cursor: not-allowed;
  filter: grayscale(0.25);
}
</style>
