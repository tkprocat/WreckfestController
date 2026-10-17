import { describe, expect, it } from 'vitest'
import { basePath, serverId, sitePath, withoutBase } from './base'

describe('basePath', () => {
  it('is / at the root', () => {
    expect(basePath('http://example.com/')).toBe('/')
  })

  it('is the path base with its slash', () => {
    expect(basePath('http://example.com/tvtp2/')).toBe('/tvtp2/')
  })

  it('drops a page name, as a document without <base> has its own address', () => {
    expect(basePath('http://example.com/tvtp2/tracks')).toBe('/tvtp2/')
  })
})

describe('sitePath', () => {
  it('puts a site path under the base', () => {
    expect(sitePath('/api/auth/state', '/tvtp2/')).toBe('/tvtp2/api/auth/state')
    expect(sitePath('hubs/server', '/tvtp2/')).toBe('/tvtp2/hubs/server')
  })

  it('leaves it as it is at the root', () => {
    expect(sitePath('/api/auth/state', '/')).toBe('/api/auth/state')
  })
})

describe('withoutBase', () => {
  it('takes the base off', () => {
    expect(withoutBase('/tvtp2/api/auth/login', '/tvtp2/')).toBe('/api/auth/login')
  })

  it('leaves a path outside the base alone', () => {
    expect(withoutBase('/other/api', '/tvtp2/')).toBe('/other/api')
    expect(withoutBase('/api/auth/login', '/')).toBe('/api/auth/login')
  })
})

describe('serverId', () => {
  it('is the site path without its slashes: the id the portal grants servers by', () => {
    expect(serverId('/tvtp2/')).toBe('tvtp2')
  })

  it('is empty at the root', () => {
    expect(serverId('/')).toBe('')
  })
})
