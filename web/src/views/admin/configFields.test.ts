import { describe, expect, it } from 'vitest'
import openapi from '@/api/openapi.json'
import { FIELDS, previewText } from './configFields'

// The committed contract the web app is typed against (kept in step with the API by
// OpenApiContractTests): every setting the API serves must be on the page, once.
const contract = openapi as unknown as {
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

  // A setting the file has no active line for is not used by the server: the summary must
  // not show it as if it were.
  it('shows a setting that is not active in the file commented out', () => {
    const text = previewText(
      { adminSteamIds: '123', serverName: 'Race night' },
      { adminSteamIds: 'admin_steam_ids', serverName: 'server_name' },
      null,
      new Set(['adminSteamIds']),
    )

    expect(text).toContain('#admin_steam_ids=123   (not active in server_config.cfg)')
    expect(text).toContain('\nserver_name=Race night')
  })

  // The reference gives the deathmatch time limit a unit but no range: none is invented.
  it('does not cap the deathmatch time limit', () => {
    const timeLimit = FIELDS.find((def) => def.field === 'timeLimit')

    expect(timeLimit).toMatchObject({ kind: 'number', min: 1 })
    expect(timeLimit && 'max' in timeLimit ? timeLimit.max : undefined).toBeUndefined()
  })
})
