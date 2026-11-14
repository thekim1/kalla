<script setup lang="ts">
import { computed } from 'vue'
import type { ClientAssistant } from '@/api/types'
import { PLATFORM_CLIENTS } from '@/utils/labels'
import CopyValue from './CopyValue.vue'
import SecretOnce from './SecretOnce.vue'
import { apiAddress, curlExample } from './clients'

// "How to connect" (admin design §5.9.1): what the app's owner needs, from what was just made or from the open app. An
// integration or internal app gets the API address, the key, the model names and one call to try; Open WebUI, which the
// deployment connects by itself (identity design §5.2), a sentence. `secret` is the key when it is on screen (just made,
// or shown through Show key in the drawer, which shows it beside the keys itself); without it the block says where to
// find one. The app's id is under Details, for support.
const props = defineProps<{ kind: string; clientId: string; apiBaseUrl: string | null; secret: string | null; assistants: ClientAssistant[]; created?: boolean }>()

const L = PLATFORM_CLIENTS.connect
const openWebUi = computed(() => props.kind === 'open-webui')
/** The call uses a model that answers: the first published assistant, else the first. */
const model = computed(() => (props.assistants.find((a) => a.published) ?? props.assistants[0])?.slug ?? null)
</script>

<template>
  <div class="grid gap-5" data-test="how-to-connect">
    <!-- Open WebUI is connected by the deployment (identity design §5.2, M-17 C4): nothing to copy. -->
    <p v-if="openWebUi" class="text-fg-2" data-test="connect-open-webui">{{ L.openWebUi }}</p>
    <template v-else>
      <div class="grid gap-1" data-test="connect-address">
        <h4 class="font-medium">{{ L.address }}</h4>
        <CopyValue v-if="apiBaseUrl" :value="apiAddress(apiBaseUrl)" :copy-label="L.copyAddress" />
        <p v-else class="text-fg-2">{{ L.noAddress }}</p>
      </div>
      <div class="grid gap-1" data-test="connect-key">
        <SecretOnce v-if="secret && created" :secret="secret" />
        <h4 v-else class="font-medium">{{ L.key }}</h4>
        <p v-if="!secret" class="text-fg-2" data-test="connect-no-key">{{ created ? L.noKey : L.showKey }}</p>
        <p class="text-small text-fg-3">{{ L.keyHeader }}</p>
      </div>
      <div class="grid gap-1" data-test="connect-models">
        <h4 class="font-medium">{{ L.models }}</h4>
        <p v-if="assistants.length === 0" class="text-fg-2">{{ L.noModels }}</p>
        <template v-else>
          <p class="text-small text-fg-3">{{ L.modelsHint }}</p>
          <ul class="grid gap-1">
            <li v-for="a in assistants" :key="a.id" class="flex flex-wrap items-center gap-x-3 gap-y-1">
              <CopyValue :value="a.slug" :copy-label="L.copyModel" />
              <span class="text-small text-fg-2">{{ a.name }}<template v-if="!a.published"> ({{ L.notPublished }})</template></span>
            </li>
          </ul>
        </template>
      </div>
      <div class="grid gap-2" data-test="connect-example">
        <h4 class="font-medium">{{ L.example }}</h4>
        <CopyValue :value="curlExample(apiBaseUrl, secret, model)" :copy-label="L.copyExample" block />
      </div>
    </template>
    <details v-if="created">
      <summary class="cursor-pointer text-small text-fg-2">{{ L.details }}</summary>
      <div class="mt-2 grid gap-1">
        <h4 class="font-medium">{{ L.clientId }}</h4>
        <CopyValue :value="clientId" data-test="client-id" />
      </div>
    </details>
  </div>
</template>
