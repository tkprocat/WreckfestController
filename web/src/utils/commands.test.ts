import { describe, expect, it } from 'vitest'
import { disruptiveCommand } from './commands'

describe('disruptiveCommand', () => {
  it('knows the commands that disconnect players, with or without a slash', () => {
    for (const command of ['exit', '/exit', 'quit', '/restart', 'restart', '  EXIT ']) {
      expect(disruptiveCommand(command), command).toBe(true)
    }
  })

  it('leaves everyday commands alone', () => {
    for (const command of ['/message Hello', 'list', 'restarted', '/kick Bob', '/message restart soon']) {
      expect(disruptiveCommand(command), command).toBe(false)
    }
  })
})
