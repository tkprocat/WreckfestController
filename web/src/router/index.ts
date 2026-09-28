import { createRouter, createWebHistory } from 'vue-router'

// The controller answers every path outside /api and /hubs with index.html, so these
// routes work as deep links and on reload.
export const router = createRouter({
  history: createWebHistory(),
  routes: [
    { path: '/', name: 'home', component: () => import('@/views/HomeView.vue') },
    { path: '/login', name: 'login', component: () => import('@/views/LoginView.vue') },
    { path: '/:pathMatch(.*)*', name: 'not-found', component: () => import('@/views/NotFoundView.vue') },
  ],
})
