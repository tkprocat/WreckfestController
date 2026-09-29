/**
 * Console commands that disconnect players, like the Stop and Restart buttons do: they
 * get the same confirmation. `exit`/`quit` end the server; `restart` restarts the event.
 * The leading slash is optional, as the server accepts both.
 */
const DISRUPTIVE = /^\/?(exit|quit|restart)\b/i

export function disruptiveCommand(text: string): boolean {
  return DISRUPTIVE.test(text.trim())
}
