import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { SETUP_WIZARD } from '@/utils/labels'
import SetupTestDone from './SetupTestDone.vue'
import { TEST_DONE_MESSAGE } from './testWindow'

describe('the end of the test sign-in window (/setup?test=done)', () => {
  afterEach(() => {
    Object.defineProperty(window, 'opener', { value: null, configurable: true, writable: true })
  })

  it('tells its opener, on this origin only, and closes', () => {
    const postMessage = vi.fn()
    Object.defineProperty(window, 'opener', { value: { postMessage }, configurable: true, writable: true })
    const close = vi.spyOn(window, 'close').mockImplementation(() => undefined)
    const wrapper = mount(SetupTestDone)
    expect(postMessage).toHaveBeenCalledWith(TEST_DONE_MESSAGE, window.location.origin)
    expect(close).toHaveBeenCalled()
    expect(wrapper.find('[data-test=test-done-close]').exists()).toBe(false)
  })

  it('says the window can be closed when it has no opener', async () => {
    const close = vi.spyOn(window, 'close').mockImplementation(() => undefined)
    const wrapper = mount(SetupTestDone)
    await flushPromises()
    expect(close).not.toHaveBeenCalled()
    expect(wrapper.get('[data-test=test-done-close]').text()).toBe(SETUP_WIZARD.testDone.close)
  })
})
