import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { defineComponent, h } from 'vue'
import { DOMWrapper, flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { NDialogProvider, NMessageProvider } from 'naive-ui'
import TagsView from './TagsView.vue'

const api = vi.hoisted(() => ({ GET: vi.fn(), POST: vi.fn(), PUT: vi.fn(), DELETE: vi.fn() }))
vi.mock('@/api/client', () => ({ api }))

const tag = (id: number, name: string, slug: string, color: string | null = null) => ({ id, name, slug, color })
const answer = (data: unknown, status = 200) => ({ data, error: undefined, response: new Response(null, { status }) })
const refused = (error: unknown, status: number) => ({ data: undefined, error, response: new Response(null, { status }) })

let wrapper: VueWrapper | undefined

function mountPage() {
  wrapper = mount(
    defineComponent({ render: () => h(NMessageProvider, () => h(NDialogProvider, () => h(TagsView))) }),
    { attachTo: document.body },
  )
  return wrapper
}

const body = () => new DOMWrapper(document.body)
const button = (label: string, within = body()) => within.findAll('button').find((b) => b.text().trim() === label)!
const rowOf = (name: string) => wrapper!.findAll('tr').find((row) => row.text().includes(name))!
const menuOption = (action: string) => body().findAll('.n-dropdown-option-body').find((option) => option.text().trim() === action)!
async function menuAction(row: string, action: string) {
  await body().find('[aria-label="More actions for ' + row + '"]').trigger('click')
  await flushPromises()
  await menuOption(action).trigger('click')
  await flushPromises()
}


beforeEach(() => {
  for (const fn of Object.values(api)) fn.mockReset()
  api.GET.mockResolvedValue(answer([tag(1, 'Dirt', 'dirt', '#aa5500'), tag(2, 'Night', 'night')]))
})

afterEach(() => {
  wrapper?.unmount()
  wrapper = undefined
  document.body.innerHTML = ''
})

describe('TagsView', () => {
  it('lists the tags and searches them', async () => {
    mountPage()
    await flushPromises()
    expect(wrapper!.text()).toContain('Dirt')

    await wrapper!.find('input[aria-label="Search tags"]').setValue('nig')

    expect(wrapper!.text()).toContain('Night')
    expect(wrapper!.text()).not.toContain('Dirt')
  })

  // A new tag's slug follows its name until edited by hand.
  it('adds a tag, with a slug made from its name', async () => {
    api.POST.mockResolvedValue(answer(tag(3, 'Snow & Ice', 'snow-ice'), 201))
    mountPage()
    await flushPromises()

    await button('Add tag').trigger('click')
    await flushPromises()
    await body().find('input[aria-label="Name"]').setValue('Snow & Ice')
    expect((body().find('input[aria-label="Slug"]').element as HTMLInputElement).value).toBe('snow-ice')
    await button('Add').trigger('click')
    await flushPromises()

    expect(api.POST).toHaveBeenCalledWith('/api/catalogue/tags', { body: { name: 'Snow & Ice', slug: 'snow-ice', color: null } })
    expect(wrapper!.text()).toContain('Snow & Ice')
    expect(body().text()).toContain('Tag saved.')
  })

  it('shows a taken slug under the slug field', async () => {
    api.POST.mockResolvedValue(refused({ title: 'Invalid', errors: { slug: ["A tag with slug 'dirt' already exists."] } }, 400))
    mountPage()
    await flushPromises()

    await button('Add tag').trigger('click')
    await flushPromises()
    await body().find('input[aria-label="Name"]').setValue('Dirt')
    await button('Add').trigger('click')
    await flushPromises()

    expect(body().text()).toContain("A tag with slug 'dirt' already exists.")
    expect(body().find('[role="dialog"][aria-label="Add tag"]').exists()).toBe(true)

    // Tied to the field, not only shown under it.
    const slug = body().find('input[aria-label="Slug"]')
    expect(slug.attributes('aria-invalid')).toBe('true')
    expect(document.getElementById(slug.attributes('aria-describedby')!)!.textContent).toBe("A tag with slug 'dirt' already exists.")
    expect(body().find('input[aria-label="Name"]').attributes('aria-invalid')).toBeUndefined()
  })

  it('edits a tag', async () => {
    api.PUT.mockResolvedValue(answer(tag(2, 'Night race', 'night')))
    mountPage()
    await flushPromises()

    await button('Edit', rowOf('Night')).trigger('click')
    await flushPromises()
    await body().find('input[aria-label="Name"]').setValue('Night race')
    await button('Save').trigger('click')
    await flushPromises()

    expect(api.PUT).toHaveBeenCalledWith('/api/catalogue/tags/{id}', {
      params: { path: { id: 2 } },
      body: { name: 'Night race', slug: 'night', color: null },
    })
    expect(wrapper!.text()).toContain('Night race')
  })

  it('asks before deleting, then deletes', async () => {
    api.DELETE.mockResolvedValue(answer(undefined, 204))
    mountPage()
    await flushPromises()

    await menuAction('Night', 'Delete')
    await flushPromises()
    expect(api.DELETE).not.toHaveBeenCalled()
    const dialog = body().find('.n-dialog[role="dialog"]')
    expect(document.getElementById(dialog.attributes('aria-labelledby')!)!.textContent).toBe('Delete tag')
    expect(document.getElementById(dialog.attributes('aria-describedby')!)!.textContent).toContain(
      'It is removed from every track layout',
    )

    await button('Delete', body().find('.n-dialog')).trigger('click')
    await flushPromises()

    expect(api.DELETE).toHaveBeenCalledWith('/api/catalogue/tags/{id}', { params: { path: { id: 2 } } })
    expect(wrapper!.text()).not.toContain('Night')
  })

  it('keeps a tag when the delete is cancelled', async () => {
    mountPage()
    await flushPromises()

    await menuAction('Night', 'Delete')
    await flushPromises()
    await button('Cancel', body().find('.n-dialog')).trigger('click')
    await flushPromises()

    expect(api.DELETE).not.toHaveBeenCalled()
    expect(wrapper!.text()).toContain('Night')
  })

  // Adding before the list has loaded would lose the new row to that load.
  it('offers Add tag only once the list has loaded', async () => {
    let finish!: (value: unknown) => void
    api.GET.mockReturnValue(new Promise((resolve) => (finish = resolve)))
    mountPage()
    await flushPromises()
    expect(button('Add tag').attributes('disabled')).toBeDefined()

    finish(answer([]))
    await flushPromises()
    expect(button('Add tag').attributes('disabled')).toBeUndefined()
  })
})
