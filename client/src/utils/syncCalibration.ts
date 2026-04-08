const TONE_FREQUENCY = 880

function getAudioContextCtor(): typeof AudioContext | null {
  if (typeof window === "undefined") return null
  const w = window as unknown as { webkitAudioContext?: typeof AudioContext }
  return window.AudioContext ?? w.webkitAudioContext ?? null
}

export async function resumeOrCreateAudioContext(existing: AudioContext | null): Promise<AudioContext> {
  const Ctor = getAudioContextCtor()
  if (!Ctor) throw new Error("Web Audio API недоступен")

  const ctx = existing ?? new Ctor()
  if (ctx.state === "suspended") await ctx.resume()
  return ctx
}

/**
 * Паттерн: 100 ms звук, 50 тишина, 50 звук, 30 silence, 30 звук.
 */
export function playSyncTonePattern(ctx: AudioContext): void {
  const osc = ctx.createOscillator()
  const gain = ctx.createGain()
  osc.type = "sine"
  osc.frequency.value = TONE_FREQUENCY
  osc.connect(gain)
  gain.connect(ctx.destination)

  let t = ctx.currentTime + 0.06
  const volume = 0.22

  const beep = (durationSec: number) => {
    gain.gain.setValueAtTime(volume, t)
    t += durationSec
    gain.gain.setValueAtTime(0, t)
  }
  const gap = (durationSec: number) => {
    t += durationSec
  }

  beep(0.1)
  gap(0.05)
  beep(0.05)
  gap(0.03)
  beep(0.03)

  osc.start(ctx.currentTime)
  osc.stop(t + 0.02)
}

export async function fetchServerTimeSkewMs(): Promise<number> {
  const t0 = Date.now()
  const response = await fetch("/api/time-ms")
  if (!response.ok) throw new Error(`time-ms: ${response.status}`)
  const payload = (await response.json()) as { unixMs?: number }
  if (typeof payload.unixMs !== "number") throw new Error("time-ms: некорректный ответ")

  const t1 = Date.now()
  const midpoint = (t0 + t1) / 2
  return payload.unixMs - midpoint
}

export type CalibrationMicSession = {
  analyser: AnalyserNode
  buffer: Uint8Array
  stop: () => void
}

function isLocalhostHostname(hostname: string): boolean {
  return hostname === "localhost" || hostname === "127.0.0.1" || hostname === "[::1]"
}

export function formatMicrophoneOpenError(error: unknown): string {
  if (typeof window !== "undefined" && !window.isSecureContext && !isLocalhostHostname(window.location.hostname))
    return "Микрофон доступен только по HTTPS (или на localhost). Откройте сайт с защищённым соединением и повторите калибровку."

  if (error instanceof DOMException) {
    if (error.name === "NotAllowedError" || error.name === "PermissionDeniedError")
      return "Доступ к микрофону отклонён. Разрешите запись в настройках браузера для этого сайта и нажмите «Калибровка» снова."
    if (error.name === "NotFoundError" || error.name === "DevicesNotFoundError")
      return "Микрофон не найден. Подключите микрофон и повторите попытку."
    if (error.name === "NotReadableError" || error.name === "TrackStartError")
      return "Микрофон занят другим приложением. Закройте его и повторите калибровку."
  }

  if (error instanceof Error && error.message === "MIC_GETUSERMEDIA_MISSING")
    return "Браузер не поддерживает доступ к микрофону (getUserMedia)."

  if (error instanceof Error && error.message === "WEB_AUDIO_UNAVAILABLE") return "Web Audio API недоступен в этом браузере."

  return "Не удалось включить микрофон для калибровки. Проверьте разрешения и HTTPS."
}

/**
 * Один запрос доступа к микрофону на всю сессию калибровки.
 * Вызывайте сразу из обработчика клика (до длительных await), иначе браузер может заблокировать запрос.
 */
export async function openCalibrationMicSession(): Promise<CalibrationMicSession> {
  if (typeof window === "undefined") throw new DOMException("no window", "NotSupportedError")

  if (!window.isSecureContext && !isLocalhostHostname(window.location.hostname))
    throw new DOMException("insecure context", "NotAllowedError")

  if (!navigator.mediaDevices?.getUserMedia) throw new Error("MIC_GETUSERMEDIA_MISSING")

  const stream = await navigator.mediaDevices.getUserMedia({
    audio: { echoCancellation: false, noiseSuppression: false, autoGainControl: false },
    video: false
  })

  const Ctor = getAudioContextCtor()
  if (!Ctor) {
    stream.getTracks().forEach(track => track.stop())
    throw new Error("WEB_AUDIO_UNAVAILABLE")
  }

  const ctx = new Ctor()
  const source = ctx.createMediaStreamSource(stream)
  const analyser = ctx.createAnalyser()
  analyser.fftSize = 1024
  source.connect(analyser)
  const buffer = new Uint8Array(analyser.frequencyBinCount)

  await ctx.resume().catch(() => {})

  return {
    analyser,
    buffer,
    stop: () => {
      try {
        source.disconnect()
      } catch {
        /* ignore */
      }
      stream.getTracks().forEach(track => track.stop())
      void ctx.close()
    }
  }
}

/** Один замер по уже открытому микрофону (поток не останавливается). */
export function measureLagMsAfterSendWithSession(sendTimePerf: number, session: CalibrationMicSession): Promise<number> {
  const { analyser, buffer } = session
  const thresholdRms = 0.1
  const minAfterSendMs = 12
  const deadline = sendTimePerf + 2800

  return new Promise((resolve, reject) => {
    const tick = () => {
      analyser.getByteTimeDomainData(buffer as Parameters<AnalyserNode["getByteTimeDomainData"]>[0])
      let sumSquares = 0
      for (let i = 0; i < buffer.length; i++) {
        const normalized = (buffer[i]! - 128) / 128
        sumSquares += normalized * normalized
      }
      const rms = Math.sqrt(sumSquares / buffer.length)
      const now = performance.now()

      if (rms > thresholdRms && now >= sendTimePerf + minAfterSendMs) {
        resolve(now - sendTimePerf)
        return
      }

      if (now > deadline) {
        reject(new Error("Таймаут ожидания сигнала калибровки на микрофоне мастера"))
        return
      }

      requestAnimationFrame(tick)
    }

    requestAnimationFrame(tick)
  })
}

export function isStableWithinMs(samples: number[], maxSpreadMs: number): boolean {
  if (samples.length < 5) return false
  const last = samples.slice(-5)
  const min = Math.min(...last)
  const max = Math.max(...last)
  return max - min <= maxSpreadMs
}

export function averageLast(samples: number[], count: number): number {
  const slice = samples.slice(-count)
  if (!slice.length) return 0
  return slice.reduce((a, b) => a + b, 0) / slice.length
}
