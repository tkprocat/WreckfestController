# Wreckfest design system

The public spectator front page is the reference. Use its spacious hierarchy, charcoal or soft light surfaces, warm accent, readable text, and explicit status language throughout the application. Vue and Naive UI continue to own interactions; this system supplies their presentation.

## Foundations

The source of truth is `web/src/theme/tokens.ts`: `design` defines sizes and status fills, `palettes` defines light/dark surfaces and semantic text/brand colors, and `themeVariables` exposes the same values to CSS. `themeOverrides` configures Naive UI from those values. Do not maintain an independent page palette.

| Role | Token | Reference size |
| --- | --- | --- |
| Reading text | --font-body | 18px |
| Controls and tables | --font-control | 16px |
| Help, timestamps, technical IDs | --font-meta | 16px |
| Short section labels | --font-label | 15px |
| Supporting list text | --font-support | 17px |
| List positions/counts | --font-count | 20px |
| Event heading | --font-event | 24px |
| Roster names | --font-roster | 19px |
| Prominent list items | --font-item | 22px |
| Section heading | --font-section | 30px |
| Page title | --font-title | 24–36px, responsive |
| Current race/display heading | --font-display | 36–64px, responsive |

Use --space-2/3/4/6/8/9/10/12 (8/12/16/24/32/36/40/48px). Prefer 24–48px between meaningful groups and 16–24px between list rows. Wide pages cap at --content-wide (1600px); ordinary forms at --content-form (840px), larger forms at --content-form-wide (1120px). --panel-padding is 32px, falling to 20px on phones. Text wraps, controls stack, and wide tables scroll within their container. Keep numeric data aligned and distinguish technical IDs from names.

## Shared components

- **PageHeader**: one h1, description, optional status/actions slots. Use on management pages.
- **StatusBadge**: `tone="positive" | "negative" | "warning" | "neutral"`, with explicit text in its default slot. Positive and negative use the front page's green/red fills and white text. Warning is for warmup, unsaved or stale states; neutral is for unknown/checking or contextual states. Use `compact` in tables and summaries. Color never replaces the words.
- **ContentSection**: a native section for open, spacious compositions like the roster, rotation and events. Supply an h2 in the slot. Use Naive UI Card when a form or grouped controls need a contained surface; its shared theme supplies padding and type.
- **ResourceTable**: responsive search/filter/results toolbar and contained data table. Preserve keyboard-accessible actions and empty/error states.
- **FormField** and existing form section/grid/action classes: preserve labels, help/error associations, busy guards and conflict handling while using the shared type and spacing scale.

Example: `<StatusBadge tone="positive">UP</StatusBadge>`. Badges are static by default. Set `live` only for an important standalone status that should announce changes, as on Home. Avoid live badges in rows and repeated checking indicators. Choose the tone from confirmed state in the owning view, not inside the presentation component. A lost transport connection is NO CONNECTION; an HTTP refresh failure is NOT UPDATING. Retain useful prior data with a nearby Last known marker. The front page's absolute Last confirmed timestamp belongs in its footer.

## Data and accessibility

Game server-name color codes are data formatting, not brand or status tokens. They must remain escaped text, with theme-adjusted colors. Keep destructive actions red, the brand accent for primary actions, and muted text readable. Preserve visible focus, real accessible names, keyboard focus restoration, native heading order and reduced motion. List order in the DOM should match the visual reading order.

## Adoption and verification

The public page, login/404, admin shells and shared tables/forms and modal editors now consume these foundations. Home, Dashboard, Server control, Users, Cups, and the rotation source/notice summaries reuse StatusBadge; descriptive chips such as Bot, Hidden and catalogue tags remain Naive UI Tags with a 15px minimum reading size. Page-level layouts remain purpose-specific: the public race hero is not repeated on every management page.

For a change, run the frontend tests and production build. Check both themes at 320, 390, 768 and 1440px; check wide layouts at 1920/2560px. Inspect populated and empty/error states, dialogs, navigation, contained tables and zoom. Tests should cover behavior; do not assert decorative class names merely to lock in CSS. This document defines the system, not a claim of complete accessibility certification.

## Visual references

These production-build renders use deterministic API fixtures. They show the source front-page design alongside its application to management screens and an editor.

| Reference | Screenshot |
| --- | --- |
| Public page, dark desktop | [Home](ui-redesign/design-system/home-dark-1440.png) |
| Dashboard, dark desktop | [Dashboard](ui-redesign/design-system/admin-dark-1440.png) |
| Resource table, light mobile | [Tracks](ui-redesign/design-system/admin-tracks-light-320.png) |
| Account statuses, light desktop | [Users](ui-redesign/design-system/admin-users-light-1440.png) |
| Cup phases, dark desktop | [Cups](ui-redesign/design-system/admin-cups-dark-1440.png) |
| Modal editor, dark mobile | [Track editor](ui-redesign/design-system/track-dialog-dark-390.png) |

Verified on 6 October 2026: 275 tests in 35 files, production build, 112 route/theme/viewport smoke cases and four theme/viewport interaction passes. The interaction passes covered search/filter reset, editor cancellation, theme persistence and drawer focus restoration.

## Control and status conventions

Controls use 36/42/48px heights from the shared foundation; tag chips use 28/32/36px heights and 15–16px text. Semantic status badges use their own high-contrast fills, while descriptive tag chips use the theme's contextual colors. Form validation colors remain readable theme foreground colors; they are not badge backgrounds.

Use the label style for eyebrows (15px, secondary text, .06em tracking), the item style for server action headings (22px), and metadata style for explanations (16px). Native section attributes such as aria-labelledby pass through ContentSection without an extra component API.
