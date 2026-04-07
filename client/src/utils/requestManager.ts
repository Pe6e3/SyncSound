const inFlightRequests = new Map<string, Promise<unknown>>()

async function deduplicatedRequest<T>(url: string, task: () => Promise<T>): Promise<T> {
  const existingRequest = inFlightRequests.get(url)
  if (existingRequest) return existingRequest as Promise<T>

  const requestPromise = task()
  inFlightRequests.set(url, requestPromise)

  try {
    return await requestPromise
  } finally {
    inFlightRequests.delete(url)
  }
}

export async function requestJson<T>(url: string, init: RequestInit): Promise<T> {
  return deduplicatedRequest<T>(url, async () => {
    const response = await fetch(url, init)
    if (!response.ok) throw new Error(`Request failed with status ${response.status}`)

    const responseText = await response.text()
    if (!responseText) return undefined as T
    return JSON.parse(responseText) as T
  })
}

export async function requestBlob(url: string, init: RequestInit): Promise<Blob> {
  return deduplicatedRequest<Blob>(url, async () => {
    const response = await fetch(url, init)
    if (!response.ok) throw new Error(`Request failed with status ${response.status}`)
    return await response.blob()
  })
}
