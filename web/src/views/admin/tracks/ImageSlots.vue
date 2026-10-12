<script setup lang="ts">
import { computed, ref } from 'vue'
import { NButton, useMessage } from 'naive-ui'
import { api } from '@/api/client'
import { NO_ANSWER, send } from '@/crud/outcome'
import {
  IMAGE_SIZES,
  imagesVersion,
  trackImageUrl,
  variantImageUrl,
  variantLayoutUrl,
  type CatalogueImage,
  type ImageSize,
} from '@/api/images'

/**
 * A track's or variant's menu images in its editor (#260): what shows at each size, and an
 * admin's own image, which shows instead of an imported one until it is removed. Changes
 * are saved at once, apart from the editor's Save: images have their own endpoints.
 */
const props = defineProps<{
  kind: 'track' | 'variant'
  /** The row's database id. */
  id: number
  /** The track key or variant id the image links use. */
  name: string
  /** The images that can show for this row: its own, and its track's or variants'. */
  images: CatalogueImage[]
}>()

const emit = defineEmits<{
  /** The row's own images after an upload or removal. */
  changed: [own: CatalogueImage[]]
}>()

const message = useMessage()
const busy = ref<ImageSize | null>(null)
const broken = ref(new Set<string>())
const inputs: Partial<Record<ImageSize, HTMLInputElement | null>> = {}

const own = computed(() =>
  props.images.filter((i) => (props.kind === 'track' ? i.trackId === props.id : i.variantId === props.id)),
)

const SIZE_LABELS: Record<ImageSize, string> = { Small: 'Small (lists, 250x116)', Large: 'Large (track page, 832x386)' }

/** What an image of the row's own is, in words. */
function describe(image: CatalogueImage): string {
  const from = image.origin === 'Upload' ? 'Your uploaded' : 'Imported'
  return image.isOverlay ? `${from} layout outline, drawn over the picture.` : `${from} picture.`
}

/** A variant's outline, drawn over each preview as the game does. */
const layout = computed(() =>
  props.kind === 'variant' && own.value.some((i) => i.isOverlay) ? variantLayoutUrl(props.name, imagesVersion(own.value)) : null,
)

const slots = computed(() =>
  IMAGE_SIZES.map((size) => {
    // An upload shows over an import, so it is described first.
    const mine = own.value.filter((i) => i.size === size).sort((a) => (a.origin === 'Upload' ? -1 : 1))
    const version = imagesVersion(props.images)
    const url = props.kind === 'track' ? trackImageUrl(props.name, size, version) : variantImageUrl(props.name, size, version)
    const parts = mine.map(describe)
    if (!mine.some((i) => !i.isOverlay)) {
      parts.push(
        props.kind === 'track'
          ? "No picture of its own: shows a variant's, or the other size, if there is one."
          : "No picture of its own: shows its track's, or the other size, if there is one.",
      )
    }
    return { size, label: SIZE_LABELS[size], url, uploaded: mine.some((i) => i.origin === 'Upload'), source: parts.join(' ') }
  }),
)

const isImages = (body: unknown): body is CatalogueImage[] => Array.isArray(body)

const path = (size: ImageSize) => ({ params: { path: { id: props.id, size } } })

async function upload(size: ImageSize, event: Event) {
  const input = event.target as HTMLInputElement
  const file = input.files?.[0]
  input.value = ''
  if (!file || busy.value) return

  busy.value = size
  try {
    const form = new FormData()
    form.append('file', file, file.name)
    const options = { ...path(size), body: {} as never, bodySerializer: () => form }
    const outcome = await send(
      () =>
        props.kind === 'track'
          ? api.PUT('/api/catalogue/tracks/{id}/images/{size}', options)
          : api.PUT('/api/catalogue/variants/{id}/images/{size}', options),
      isImages,
      'The image was not uploaded.',
    )
    if (outcome.kind === 'ok' && isImages(outcome.row)) {
      broken.value.delete(size)
      emit('changed', outcome.row)
      message.success('Image uploaded.')
    } else {
      message.error(outcome.kind === 'no-answer' ? NO_ANSWER : 'message' in outcome ? outcome.message : 'The image was not uploaded.')
    }
  } finally {
    busy.value = null
  }
}

async function remove(size: ImageSize) {
  if (busy.value) return
  busy.value = size
  try {
    const outcome = await send(
      () =>
        props.kind === 'track'
          ? api.DELETE('/api/catalogue/tracks/{id}/images/{size}', path(size))
          : api.DELETE('/api/catalogue/variants/{id}/images/{size}', path(size)),
      isImages,
      'The image was not removed.',
    )
    // A 404: it was gone already. Either way, it is not there now.
    if (outcome.kind === 'ok' || outcome.kind === 'refused') {
      broken.value.delete(size)
      emit('changed', own.value.filter((i) => !(i.size === size && i.origin === 'Upload')))
      if (outcome.kind === 'ok') message.success('Upload removed.')
      else message.warning(outcome.message)
    } else {
      message.error(outcome.kind === 'no-answer' ? NO_ANSWER : 'message' in outcome ? outcome.message : 'The image was not removed.')
    }
  } finally {
    busy.value = null
  }
}

function markBroken(size: ImageSize) {
  broken.value = new Set([...broken.value, size])
}
</script>

<template>
  <section class="images" aria-label="Images">
    <h3 class="heading">Images</h3>
    <div v-for="slot in slots" :key="slot.size" class="slot">
      <div class="preview" :class="slot.size.toLowerCase()">
        <img v-if="!broken.has(slot.size)" :src="slot.url" :alt="`${slot.label} image`" @error="markBroken(slot.size)" />
        <span v-else class="none">No picture</span>
        <img v-if="layout" class="layout" :src="layout" alt="Layout outline" />
      </div>
      <div class="detail">
        <span class="label">{{ slot.label }}</span>
        <span class="meta">{{ slot.source }}</span>
        <div class="actions">
          <!-- The button opens the hidden picker, so there is one control to reach, with a name. -->
          <input
            :ref="(el) => (inputs[slot.size] = el as HTMLInputElement | null)"
            class="visually-hidden"
            type="file"
            tabindex="-1"
            aria-hidden="true"
            accept="image/png,image/jpeg,image/webp"
            :disabled="busy !== null"
            @change="upload(slot.size, $event)"
          />
          <NButton
            size="small"
            :loading="busy === slot.size"
            :disabled="busy !== null"
            :aria-label="`Upload a ${slot.size.toLowerCase()} image`"
            @click="inputs[slot.size]?.click()"
          >
            Upload
          </NButton>
          <NButton v-if="slot.uploaded" size="small" :disabled="busy !== null" @click="remove(slot.size)">Remove upload</NButton>
        </div>
      </div>
    </div>
  </section>
</template>

<style scoped>
.images {
  margin: var(--space-4) 0;
}
.heading {
  font-size: var(--font-label);
  margin: 0 0 var(--space-2);
}
.slot {
  display: flex;
  gap: var(--space-4);
  align-items: flex-start;
  margin-bottom: var(--space-4);
  flex-wrap: wrap;
}
.preview {
  width: 160px;
  aspect-ratio: 250 / 116;
  background: var(--surface-muted, rgba(127, 127, 127, 0.12));
  border-radius: 4px;
  overflow: hidden;
  display: flex;
  align-items: center;
  justify-content: center;
  flex-shrink: 0;
  position: relative;
}
/* The outline is white on clear: over the picture, or over a dark ground when there is none. */
.preview:has(.layout) {
  background: #2b2f36;
}
.preview .layout {
  position: absolute;
  inset: 0;
}
.preview.large {
  aspect-ratio: 832 / 386;
}
.preview img {
  width: 100%;
  height: 100%;
  object-fit: cover;
}
.none,
.meta {
  color: var(--text-muted);
  font-size: var(--font-meta);
}
.detail {
  display: flex;
  flex-direction: column;
  gap: var(--space-1, 4px);
  min-width: 0;
  flex: 1;
}
.label {
  font-size: var(--font-label);
}
.actions {
  display: flex;
  gap: var(--space-2);
  margin-top: var(--space-1, 4px);
}
.visually-hidden {
  position: absolute;
  width: 1px;
  height: 1px;
  overflow: hidden;
  clip: rect(0 0 0 0);
  white-space: nowrap;
}
</style>
