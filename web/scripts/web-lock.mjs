// Runs the build's npm steps one at a time across processes. The controller and the portal
// both build from this folder, and a solution build runs them side by side: two `npm ci`
// at once fail with EPERM, and two `vue-tsc -b` write the same build info.
//
//   node scripts/web-lock.mjs install      npm ci, unless the packages are already current
//   node scripts/web-lock.mjs run <script> npm run <script>
//
// Uses only Node's own modules: it runs before the packages are installed.
import { spawnSync } from 'node:child_process'
import { mkdirSync, readFileSync, rmSync, statSync, writeFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'

const root = fileURLToPath(new URL('..', import.meta.url))
const lock = fileURLToPath(new URL('../.build-lock', import.meta.url))
const owner = lock + '/owner.json'
const stamp = fileURLToPath(new URL('../node_modules/.wfc-installed', import.meta.url))

// A lock this old is from a build that was killed, whatever its process is now.
const STALE_MS = 30 * 60 * 1000

function alive(pid) {
  try {
    process.kill(pid, 0)
    return true
  } catch (error) {
    return error.code === 'EPERM'
  }
}

async function acquire() {
  let told = false
  for (;;) {
    try {
      mkdirSync(lock)
      writeFileSync(owner, JSON.stringify({ pid: process.pid, at: Date.now() }))
      return
    } catch (error) {
      if (error.code !== 'EEXIST') throw error
    }

    let holder = null
    try {
      holder = JSON.parse(readFileSync(owner, 'utf8'))
    } catch {
      // Being created or removed right now: look again shortly.
    }

    if (holder && (!alive(holder.pid) || Date.now() - holder.at > STALE_MS)) {
      rmSync(lock, { recursive: true, force: true })
      continue
    }

    if (!told) {
      console.log(`Waiting for another web build${holder ? ` (process ${holder.pid})` : ''}...`)
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

await acquire()
let status
try {
  if (command === 'install') {
    // Another build may have installed them while this one waited.
    const installed = modified(stamp)
    const current = installed !== null
      && installed >= (modified(root + '/package.json') ?? Infinity)
      && installed >= (modified(root + '/package-lock.json') ?? Infinity)
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
  rmSync(lock, { recursive: true, force: true })
}

process.exit(status)
