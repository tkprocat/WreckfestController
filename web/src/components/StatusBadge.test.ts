import { describe, expect, it } from 'vitest'
import { mount } from '@vue/test-utils'
import StatusBadge from './StatusBadge.vue'

describe('StatusBadge announcements', () => {
  it('does not create live regions for routine row statuses', () => {
    const wrapper = mount(StatusBadge, { props: { tone: 'positive', compact: true }, slots: { default: 'Active' } })
    expect(wrapper.text()).toBe('Active')
    expect(wrapper.attributes('role')).toBeUndefined()
    expect(wrapper.attributes('aria-live')).toBeUndefined()
  })
  it('announces changes only when the owner opts in', () => {
    const wrapper = mount(StatusBadge, { props: { live: true }, slots: { default: 'NO CONNECTION' } })
    expect(wrapper.attributes('role')).toBe('status')
    expect(wrapper.text()).toBe('NO CONNECTION')
  })
})
