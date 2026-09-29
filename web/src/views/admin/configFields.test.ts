import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { describe, expect, it } from 'vitest'
import { FIELDS, previewText } from './configFields'

// The committed contract the web app is typed against (kept in step with the API by
// OpenApiContractTests): every setting the API serves must be on the page, once.
// Vitest runs from web/.
const contract = JSON.parse(readFileSync(resolve(process.cwd(), 'src/api/openapi.json'), 'utf8')) as {
  components: { schemas: { ServerConfig: { properties: Record<string, unknown> } } }
}

describe('configFields', () => {
  it('covers every server setting the API serves, each once', () => {
    const served = Object.keys(contract.components.schemas.ServerConfig.properties).sort()
    const onThePage = FIELDS.map((def) => def.field).sort()

    expect(onThePage).toEqual(served)
  })

  // log= names a file: neither served nor on the page.
  it('has no log field', () => {
    expect(FIELDS.some((def) => (def.field as string) === 'log')).toBe(false)
  })

  it('previews each setting under its server_config.cfg key, then the rotation', () => {
    const text = previewText(
      { serverName: 'Race night', maxPlayers: 20, carClassRestriction: '' },
      { serverName: 'server_name', maxPlayers: 'max_players', carClassRestriction: 'car_class_restriction' },
      { collectionName: 'Evening', tracks: [{ track: 'fields14', laps: 3, gamemode: null }] },
    )

    expect(text).toContain('server_name=Race night')
    expect(text).toContain('max_players=20')
    expect(text).toContain('car_class_restriction=')
    expect(text).toContain('#CollectionName Evening')
    expect(text).toContain('el_add=fields14\nel_laps=3')
    expect(text).not.toContain('el_gamemode')
  })
})
