const inFlightRequests = new Map<string, Promise<unknown>>()

export async function requestJson<T>(url: string, init: RequestInit): Promise<T> {
  const existingRequest = inFlightRequests.get(url)
  if (existingRequest) return existingRequest as Promise<T>

  const requestPromise: Promise<T> = (async () => {
    const response = await fetch(url, init)
    if (!response.ok) throw new Error(`Request failed with status ${response.status}`)

    const responseText = await response.text()
    if (!responseText) return undefined as T
    return JSON.parse(responseText) as T
  })()

  inFlightRequests.set(url, requestPromise)

  try {
    return await requestPromise
  } finally {
    inFlightRequests.delete(url)
  }
}
