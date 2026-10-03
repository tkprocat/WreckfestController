/**
 * One size rule for editor dialogs: as wide as asked, never wider than the screen less a
 * gutter, and never taller than it. The body scrolls; the title stays, and the form's
 * sticky `.form-actions` stay at the bottom of what scrolls.
 *
 *   <NModal preset="card" v-bind="modalSize(720)" ...>
 */
export function modalSize(width: number) {
  return {
    style: { width: `min(${width}px, calc(100vw - 32px))` },
    contentStyle: { maxHeight: 'calc(100dvh - 160px)', overflowY: 'auto' as const },
  }
}
