export function collectDeviceInfo(): Record<string, string> {
  const navigatorData = window.navigator
  const screenData = window.screen

  return {
    userAgent: navigatorData.userAgent ?? "",
    platform: navigatorData.platform ?? "",
    language: navigatorData.language ?? "",
    languages: (navigatorData.languages ?? []).join(", "),
    timezone: Intl.DateTimeFormat().resolvedOptions().timeZone ?? "",
    cookieEnabled: String(navigatorData.cookieEnabled),
    doNotTrack: navigatorData.doNotTrack ?? "",
    online: String(navigatorData.onLine),
    hardwareConcurrency: String(navigatorData.hardwareConcurrency ?? ""),
    maxTouchPoints: String(navigatorData.maxTouchPoints ?? 0),
    vendor: navigatorData.vendor ?? "",
    screenResolution: `${screenData.width}x${screenData.height}`,
    viewportResolution: `${window.innerWidth}x${window.innerHeight}`,
    colorDepth: String(screenData.colorDepth),
    pixelRatio: String(window.devicePixelRatio ?? 1),
    memoryGb: String((navigatorData as Navigator & { deviceMemory?: number }).deviceMemory ?? "")
  }
}
