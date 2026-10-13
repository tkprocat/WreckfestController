import { describe, expect, it } from 'vitest'
import { mount } from '@vue/test-utils'
import ChatMarkdown from './ChatMarkdown.vue'

const render = (text: string) => mount(ChatMarkdown, { props: { text } })

describe('ChatMarkdown', () => {
  it('formats bold, lists and tables', () => {
    const wrapper = render('**Top players**\n\n1. Procat - 3 wins\n2. Bob - 1 win\n\n| Name | Wins |\n| --- | --- |\n| Procat | 3 |')

    expect(wrapper.find('strong').text()).toBe('Top players')
    expect(wrapper.findAll('ol li').map((li) => li.text())).toEqual(['Procat - 3 wins', 'Bob - 1 win'])
    expect(wrapper.findAll('td').map((td) => td.text())).toEqual(['Procat', '3'])
  })

  it('keeps single line breaks', () => {
    expect(render('Line one\nLine two').find('br').exists()).toBe(true)
  })

  // A reply can quote a player's name, which anyone can pick.
  it('shows HTML as text', () => {
    const wrapper = render('<img src=x onerror=alert(1)><script>alert(1)</script>')

    expect(wrapper.find('img').exists()).toBe(false)
    expect(wrapper.find('script').exists()).toBe(false)
    expect(wrapper.text()).toContain('<img src=x onerror=alert(1)>')
  })

  it('makes no links or images', () => {
    const wrapper = render('[Claim your prize](https://evil.example) ![x](https://evil.example/x.png) <https://evil.example> https://evil.example')

    expect(wrapper.find('a').exists()).toBe(false)
    expect(wrapper.find('img').exists()).toBe(false)
    expect(wrapper.text()).toContain('Claim your prize')
  })
})
