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

export async function measureLagMsAfterSend(sendTimePerf: number): Promise<number> {
  if (!navigator.mediaDevices?.getUserMedia) throw new Error("Нет доступа к микрофону (getUserMedia)")

  const stream = await navigator.mediaDevices.getUserMedia({
    audio: { echoCancellation: false, noiseSuppression: false, autoGainControl: false },
    video: false
  })

  const Ctor = getAudioContextCtor()
  if (!Ctor) {
    stream.getTracks().forEach(track => track.stop())
    throw new Error("Web Audio API недоступен")
  }

  const ctx = new Ctor()
  const source = ctx.createMediaStreamSource(stream)
  const analyser = ctx.createAnalyser()
  analyser.fftSize = 1024
  source.connect(analyser)
  const buffer = new Uint8Array(analyser.frequencyBinCount)

  await ctx.resume().catch(() => {})

  const thresholdRms = 0.1
  const minAfterSendMs = 12
  const deadline = sendTimePerf + 2800

  return new Promise((resolve, reject) => {
    const cleanup = () => {
      stream.getTracks().forEach(track => track.stop())
      source.disconnect()
      void ctx.close()
    }

    const tick = () => {
      analyser.getByteTimeDomainData(buffer)
      let sumSquares = 0
      for (let i = 0; i < buffer.length; i++) {
        const normalized = (buffer[i]! - 128) / 128
        sumSquares += normalized * normalized
      }
      const rms = Math.sqrt(sumSquares / buffer.length)
      const now = performance.now()

      if (rms > thresholdRms && now >= sendTimePerf + minAfterSendMs) {
        cleanup()
        resolve(now - sendTimePerf)
        return
      }

      if (now > deadline) {
        cleanup()
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
