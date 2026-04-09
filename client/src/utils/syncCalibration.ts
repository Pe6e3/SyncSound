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
  /** Замеры задержки (короткое окно FFT). */
  analyser: AnalyserNode
  /** Отдельный анализатор для осциллограммы — не конкурирует за buffer с measureLagMsAfterSendWithSession. */
  visualAnalyser: AnalyserNode
  buffer: Uint8Array
  stop: () => void
}

function isLocalhostHostname(hostname: string): boolean {
  return hostname === "localhost" || hostname === "127.0.0.1" || hostname === "[::1]"
}

/** Микрофон разрешён только в безопасном контексте: HTTPS или http://localhost (и аналоги). По http://192.168.x.x браузер запрос не покажет. */
export function isMicrophoneContextOk(): boolean {
  if (typeof window === "undefined") return false
  if (window.isSecureContext) return true
  return isLocalhostHostname(window.location.hostname)
}

const MIC_HTTPS_HINT =
  "Микрофон для калибровки доступен только в безопасном контексте: HTTPS или адрес localhost. Если открываете приложение по http:// и IP (например 192.168.…), браузер не покажет окно доступа к микрофону — настройте HTTPS или заходите с https:// или с этого компьютера через http://localhost."

export function formatMicrophoneOpenError(error: unknown): string {
  if (error instanceof Error && error.message === "MIC_INSECURE_CONTEXT") return MIC_HTTPS_HINT

  if (typeof window !== "undefined" && !window.isSecureContext && !isLocalhostHostname(window.location.hostname))
    return MIC_HTTPS_HINT

  if (error instanceof DOMException) {
    const msg = (error.message || "").toLowerCase()
    if (
      (error.name === "NotAllowedError" || error.name === "PermissionDeniedError") &&
      (msg.includes("insecure") || msg.includes("secure context"))
    )
      return MIC_HTTPS_HINT
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

  return "Не удалось включить микрофон для калибровки. Проверьте разрешения и что сайт открыт по HTTPS или localhost."
}

/**
 * Один запрос доступа к микрофону на всю сессию калибровки.
 * Вызывайте сразу из обработчика клика (до длительных await), иначе браузер может заблокировать запрос.
 */
export async function openCalibrationMicSession(): Promise<CalibrationMicSession> {
  if (typeof window === "undefined") throw new DOMException("no window", "NotSupportedError")

  if (!isMicrophoneContextOk()) throw new Error("MIC_INSECURE_CONTEXT")

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
  const visualAnalyser = ctx.createAnalyser()
  visualAnalyser.fftSize = 2048
  visualAnalyser.smoothingTimeConstant = 0.55
  source.connect(analyser)
  source.connect(visualAnalyser)
  const buffer = new Uint8Array(analyser.frequencyBinCount)

  await ctx.resume().catch(() => {})

  return {
    analyser,
    visualAnalyser,
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
  const thresholdRms = 0.065
  const minAfterSendMs = 20
  const deadline = sendTimePerf + 4500

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
        // Код без слова «микрофон»: иначе в RoomView ошибочно принимали таймаут за потерю доступа к микрофону.
        reject(new Error("CALIBRATION_SIGNAL_TIMEOUT"))
        return
      }

      requestAnimationFrame(tick)
    }

    requestAnimationFrame(tick)
  })
}

/**
 * Лента громкости во времени:
 * - по оси X: последние 30 секунд (слева -> справа),
 * - по оси Y: относительная амплитуда (пики).
 */
export function startMicWaveformAnimation(canvas: HTMLCanvasElement, visualAnalyser: AnalyserNode): () => void {
  const ctx2d = canvas.getContext("2d")
  if (!ctx2d) return () => {}

  const timeData = new Uint8Array(visualAnalyser.frequencyBinCount)
  let rafId = 0
  let stopped = false
  let cssW = 640
  let cssH = 132
  const timelineMs = 30_000
  const sampleStepMs = 120
  const pointsCount = Math.max(60, Math.floor(timelineMs / sampleStepMs))
  const peaks = new Array<number>(pointsCount).fill(0)
  let writeIndex = 0
  let lastSampleAt = 0

  const fitCanvas = () => {
    const wrapper = canvas.parentElement
    cssW = wrapper ? Math.max(280, Math.floor(wrapper.clientWidth)) : 640
    cssH = 132
    const dpr = Math.min(window.devicePixelRatio || 1, 2.5)
    canvas.width = Math.floor(cssW * dpr)
    canvas.height = Math.floor(cssH * dpr)
    canvas.style.width = `${cssW}px`
    canvas.style.height = `${cssH}px`
  }

  fitCanvas()
  const ro = typeof ResizeObserver !== "undefined" ? new ResizeObserver(() => fitCanvas()) : null
  if (ro && canvas.parentElement) ro.observe(canvas.parentElement)

  const draw = () => {
    if (stopped) return

    visualAnalyser.getByteTimeDomainData(timeData as Parameters<AnalyserNode["getByteTimeDomainData"]>[0])

    const dpr = canvas.width / cssW
    ctx2d.setTransform(dpr, 0, 0, dpr, 0, 0)

    const now = performance.now()
    let rms = 0
    let peak = 0
    for (let i = 0; i < timeData.length; i++) {
      const n = Math.abs((timeData[i]! - 128) / 128)
      rms += n * n
      if (n > peak) peak = n
    }
    rms = Math.sqrt(rms / timeData.length)
    const value = Math.max(peak, rms * 1.25)

    if (lastSampleAt === 0 || now - lastSampleAt >= sampleStepMs) {
      peaks[writeIndex] = value
      writeIndex = (writeIndex + 1) % pointsCount
      lastSampleAt = now
    }

    ctx2d.fillStyle = "rgba(5, 18, 13, 0.98)"
    ctx2d.fillRect(0, 0, cssW, cssH)

    ctx2d.strokeStyle = "rgba(80, 120, 100, 0.35)"
    ctx2d.lineWidth = 1
    ctx2d.beginPath()
    ctx2d.moveTo(0, cssH / 2)
    ctx2d.lineTo(cssW, cssH / 2)
    ctx2d.stroke()

    // Вертикальные пики по таймлайну: слева старые, справа новые.
    const xStep = cssW / Math.max(1, pointsCount - 1)
    ctx2d.strokeStyle = "rgba(110, 255, 185, 0.95)"
    ctx2d.lineWidth = 1.15
    ctx2d.shadowColor = "rgba(80, 255, 170, 0.30)"
    ctx2d.shadowBlur = 4
    ctx2d.beginPath()
    for (let i = 0; i < pointsCount; i++) {
      const idx = (writeIndex + i) % pointsCount
      const p = Math.min(1, peaks[idx]!)
      const x = i * xStep
      const h = p * (cssH * 0.92)
      const yTop = cssH - h
      ctx2d.moveTo(x, cssH)
      ctx2d.lineTo(x, yTop)
    }
    ctx2d.stroke()
    ctx2d.shadowBlur = 0

    ctx2d.fillStyle = "rgba(180, 235, 210, 0.65)"
    ctx2d.font = "12px system-ui, sans-serif"
    ctx2d.fillText("30 c", 8, 18)
    ctx2d.fillText("сейчас", Math.max(8, cssW - 50), 18)
    ctx2d.fillText(`уровень ≈ ${(value * 100).toFixed(1)}%`, 8, cssH - 8)

    rafId = requestAnimationFrame(draw)
  }

  rafId = requestAnimationFrame(draw)

  return () => {
    stopped = true
    cancelAnimationFrame(rafId)
    if (ro) ro.disconnect()
  }
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
