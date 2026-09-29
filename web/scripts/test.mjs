// Runs the unit tests. Vitest cannot resolve modules when the project path contains '#'
// (it builds file URLs, and everything after '#' is dropped), which a checkout under a
// folder such as "C#" hits. Say so plainly instead of Vitest's "Cannot find module".
import { spawnSync } from 'node:child_process'
import { fileURLToPath } from 'node:url'

const root = fileURLToPath(new URL('..', import.meta.url))
if (root.includes('#')) {
  console.error(
    `Cannot run the web app's unit tests from ${root}: Vitest does not support a '#' in the path.\n` +
      'Clone or copy the repository to a folder without one (for example F:/Projects/CSharp/...) to run them.\n' +
      'The web build and the C# tests are not affected.',
  )
  process.exit(1)
}

const result = spawnSync('npx', ['vitest', 'run', ...process.argv.slice(2)], { stdio: 'inherit', shell: true, cwd: root })
process.exit(result.status ?? 1)
