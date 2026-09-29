import { describe, expect, it } from 'vitest'
import { mount } from '@vue/test-utils'
import ConflictDialog from './ConflictDialog.vue'

const labels = { name: 'Name', laps: 'Laps', tags: 'Tags' }

describe('ConflictDialog', () => {
  // Only what differs, so the choice is about the real disagreement.
  it('shows only the fields that differ, yours and the saved ones', () => {
    const wrapper = mount(ConflictDialog, {
      props: {
        mine: { name: 'Evening', laps: 5, tags: ['dirt'] },
        theirs: { name: 'Evening', laps: 3, tags: [] },
        labels,
      },
    })

    const rows = wrapper.findAll('tbody tr').map((row) => row.findAll('td').map((cell) => cell.text()))
    expect(rows).toEqual([
      ['Laps', '5', '3'],
      ['Tags', 'dirt', '(empty)'],
    ])
  })

  it('offers both choices', async () => {
    const wrapper = mount(ConflictDialog, { props: { mine: { name: 'A' }, theirs: { name: 'B' }, labels: { name: 'Name' } } })

    await wrapper.findAll('button').find((b) => b.text() === 'Use theirs')!.trigger('click')
    await wrapper.findAll('button').find((b) => b.text() === 'Keep mine')!.trigger('click')

    expect(wrapper.emitted('theirs')).toHaveLength(1)
    expect(wrapper.emitted('mine')).toHaveLength(1)
  })

  it('says so when the values are the same after all', () => {
    const wrapper = mount(ConflictDialog, { props: { mine: { name: 'A' }, theirs: { name: 'A' }, labels: { name: 'Name' } } })

    expect(wrapper.text()).toContain('Your values and the saved ones are the same.')
  })
})
