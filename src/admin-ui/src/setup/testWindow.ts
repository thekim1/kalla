/**
 * The test sign-in's window (identity design §19, "The test belongs to one browser"): the wizard opens it, the provider sends
 * it back to `/setup?test=done`, and that page tells the wizard and closes. The message carries nothing but its type: the
 * wizard reads the result from `GET /bff/setup/state`.
 */
export const TEST_WINDOW_NAME = 'kalla-setup-test'
export const TEST_DONE_MESSAGE = 'kalla-setup-test-done'
const CHANNEL = 'kalla-setup'

/**
 * Opens the window from the click itself (a window opened after an await is blocked), empty until the test's address is known.
 * Null when the browser blocked it. Not `noopener`: the done page posts to its opener.
 */
export function openTestWindow(): Window | null {
  return window.open('about:blank', TEST_WINDOW_NAME, 'popup,width=520,height=720')
}

function channel(): BroadcastChannel | null {
  return typeof BroadcastChannel === 'undefined' ? null : new BroadcastChannel(CHANNEL)
}

/** From the done page: tells the wizard over the opener (same origin only) and a broadcast channel. Whether there was an opener. */
export function announceTestDone(): boolean {
  const bus = channel()
  bus?.postMessage(TEST_DONE_MESSAGE)
  bus?.close()
  const opener = window.opener as Window | null
  if (!opener) return false
  try {
    opener.postMessage(TEST_DONE_MESSAGE, window.location.origin)
  } catch {
    return false
  }
  return true
}

/** In the wizard: calls `onDone` when the done page announces itself; returns the function that stops listening. */
export function listenForTestDone(onDone: () => void): () => void {
  const onMessage = (event: MessageEvent) => {
    if (event.origin === window.location.origin && event.data === TEST_DONE_MESSAGE) onDone()
  }
  window.addEventListener('message', onMessage)
  const bus = channel()
  if (bus) bus.onmessage = (event: MessageEvent) => event.data === TEST_DONE_MESSAGE && onDone()
  return () => {
    window.removeEventListener('message', onMessage)
    bus?.close()
  }
}
