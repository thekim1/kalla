import { afterEach, vi } from 'vitest'
import { enableAutoUnmount } from '@vue/test-utils'

// jsdom lacks a few browser APIs used by the app.
if (!window.matchMedia) {
  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    configurable: true,
    value: (query: string) => ({
      matches: false,
      media: query,
      onchange: null,
      addEventListener: () => undefined,
      removeEventListener: () => undefined,
      addListener: () => undefined,
      removeListener: () => undefined,
      dispatchEvent: () => false,
    }),
  })
}

class ResizeObserverStub {
  observe(): void {}
  unobserve(): void {}
  disconnect(): void {}
}
if (!('ResizeObserver' in window)) {
  Object.defineProperty(window, 'ResizeObserver', { value: ResizeObserverStub, writable: true })
  Object.defineProperty(globalThis, 'ResizeObserver', { value: ResizeObserverStub, writable: true })
}

if (!Element.prototype.scrollIntoView) {
  Element.prototype.scrollIntoView = function scrollIntoView() {}
}
if (!Element.prototype.hasPointerCapture) {
  Element.prototype.hasPointerCapture = () => false
  Element.prototype.releasePointerCapture = () => undefined
}

// Unmount every wrapper before the body is emptied: a component left mounted would otherwise keep patching into nodes
// that are gone (a teleport's anchors in the body, for one).
enableAutoUnmount(afterEach)

afterEach(() => {
  document.body.innerHTML = ''
  vi.restoreAllMocks()
})
