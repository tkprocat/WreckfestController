import { afterEach, expect, it } from 'vitest'
import { DOMWrapper, flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import ResourceTable from './ResourceTable.vue'

let wrapper: VueWrapper | undefined
afterEach(() => {
  wrapper?.unmount()
  document.body.innerHTML = ''
})

it('distinguishes page filters from an empty catalogue and clears external and search filters together', async () => {
  wrapper = mount(ResourceTable, {
    attachTo: document.body,
    props: {
      rows: [] as { id: number; name: string }[],
      columns: [{ title: 'Name', key: 'name' }],
      searchFields: ['id'],
      what: 'tracks',
      totalRows: 3,
      filtersActive: true,
    },
  })
  const body = new DOMWrapper(document.body)
  expect(body.text()).toContain('0 of 3 tracks')
  expect(body.text()).toContain('No tracks match the current filters.')

  await body.find('input[aria-label="Search tracks"]').setValue('quarry')
  await body.findAll('button').find((button) => button.text() === 'Clear filters')!.trigger('click')
  await flushPromises()
  expect(wrapper.emitted('resetFilters')).toHaveLength(1)
  expect((body.find('input[aria-label="Search tracks"]').element as HTMLInputElement).value).toBe('')

  await wrapper.setProps({ filtersActive: false, totalRows: 0 })
  expect(body.text()).toContain('No tracks yet.')
})

it('keeps results navigable beyond the first 25 rows', async () => {
  const rows = Array.from({ length: 30 }, (_, index) => ({ id: index + 1, name: 'Track ' + (index + 1) }))
  wrapper = mount(ResourceTable, {
    attachTo: document.body,
    props: {
      rows,
      columns: [{ title: 'Name', key: 'name' }],
      searchFields: ['id'],
      what: 'tracks',
    },
  })
  const body = new DOMWrapper(document.body)
  expect(body.findAll('tbody tr')).toHaveLength(25)
  await body.findAll('.n-pagination-item').find((item) => item.text().trim() === '2')!.trigger('click')
  await flushPromises()
  expect(body.findAll('tbody tr').map((row) => row.text()).join(' ')).toContain('Track 30')
  expect(body.findAll('tbody tr')).toHaveLength(5)
})
