// Runs the build's npm steps one at a time across processes. The controller and the portal
// both build from this folder, and a solution build runs them side by side: two `npm ci`
// at once fail with EPERM, and two `vue-tsc -b` write the same build info.
//
//   node scripts/web-lock.mjs install      npm ci, unless the packages are already current
//   node scripts/web-lock.mjs run <script> npm run <script>
//
// The lock is an address only one process can listen on: a named pipe on Windows, an
// abstract socket on Linux. The operating system releases it when its process ends,
// however it ends, so there is no lock file to go stale and nothing to take over. Each web
// folder has its own, so worktrees do not wait on each other. Elsewhere (macOS), where
// either would leave a file behind, the steps run without one.
//
// Uses only Node's own modules: it runs before the packages are installed.
import { spawnSync } from 'node:child_process'
import { createHash } from 'node:crypto'
import { statSync, writeFileSync } from 'node:fs'
import { createServer } from 'node:net'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'

const root = fileURLToPath(new URL('..', import.meta.url))
const stamp = fileURLToPath(new URL('../node_modules/.wfc-installed', import.meta.url))

const id = createHash('sha256').update(root.toLowerCase()).digest('hex').slice(0, 16)
const addresses = {
  win32: String.raw`\\.\pipe\wfc-web-build-` + id,
  linux: '\0wfc-web-build-' + id,
}
const address = addresses[process.platform] ?? null

function listen() {
  return new Promise((resolve) => {
    const server = createServer((socket) => socket.end())
    server.once('error', (error) => resolve({ server: null, error }))
    server.listen(address, () => resolve({ server, error: null }))
  })
}

async function acquire() {
  if (address === null) return null

  let told = false
  for (;;) {
    const { server, error } = await listen()
    if (server) return server
    if (error.code !== 'EADDRINUSE') throw error

    if (!told) {
      console.log('Waiting for another web build...')
      told = true
    }
    await new Promise((resolve) => setTimeout(resolve, 500))
  }
}

function modified(path) {
  try {
    return statSync(path).mtimeMs
  } catch {
    return null
  }
}

function npm(...args) {
  const result = spawnSync('npm', args, { stdio: 'inherit', shell: true, cwd: root })
  return result.status ?? 1
}

const [command, ...rest] = process.argv.slice(2)
if (command !== 'install' && !(command === 'run' && rest.length === 1)) {
  console.error('Usage: node scripts/web-lock.mjs install | run <script>')
  process.exit(2)
}

const lock = await acquire()
let status
try {
  if (command === 'install') {
    // Another build may have installed them while this one waited.
    const installed = modified(stamp)
    const current = installed !== null
      && installed >= (modified(join(root, 'package.json')) ?? Infinity)
      && installed >= (modified(join(root, 'package-lock.json')) ?? Infinity)
    if (current) {
      console.log('Web app packages are already installed.')
      status = 0
    } else {
      console.log('Installing web app packages (npm ci)...')
      status = npm('ci', '--no-audit', '--no-fund')
      // npm's own marker files keep old timestamps, so this keeps its own.
      if (status === 0) writeFileSync(stamp, '')
    }
  } else {
    status = npm('run', rest[0])
  }
} finally {
  lock?.close()
}

process.exit(status)
