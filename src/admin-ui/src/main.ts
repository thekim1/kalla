import { createApp } from 'vue'
import { createPinia } from 'pinia'
import './styles/main.css'
import App from './App.vue'
import { createAppRouter } from './router'
import { configureClient } from './api/client'
import { useAuthStore } from './stores/auth'
import { useUiStore } from './stores/ui'

const app = createApp(App)
app.use(createPinia())

useUiStore().init()

const auth = useAuthStore()
// No change-propagation header here: the admin API writes the outbox in the change's transaction (admin design §4.1).
configureClient({ onUnauthorized: () => auth.setUnauthenticated() })

const router = createAppRouter()
app.use(router)

void router.isReady().then(() => app.mount('#app'))
