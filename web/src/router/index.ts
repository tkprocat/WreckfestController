import { createRouter, createWebHistory } from 'vue-router'
import { useAuthStore } from '@/stores/auth'
import { redirectTarget } from './redirect'

declare module 'vue-router' {
  interface RouteMeta {
    /** Only for a signed-in admin; anyone else is sent to sign in first. */
    requiresAuth?: boolean
  }
}

// The controller answers every path outside /api and /hubs with index.html, so these
// routes work as deep links and on reload.
export const router = createRouter({
  history: createWebHistory(),
  routes: [
    { path: '/', name: 'home', component: () => import('@/views/HomeView.vue') },
    { path: '/login', name: 'login', component: () => import('@/views/LoginView.vue') },
    {
      path: '/admin',
      component: () => import('@/layouts/AdminLayout.vue'),
      meta: { requiresAuth: true },
      children: [
        { path: '', name: 'admin-dashboard', component: () => import('@/views/admin/DashboardView.vue') },
        { path: 'server', name: 'admin-server', component: () => import('@/views/admin/ServerControlView.vue') },
        { path: 'settings', name: 'admin-settings', component: () => import('@/views/admin/SettingsView.vue') },
        { path: 'users', name: 'admin-users', component: () => import('@/views/admin/UsersView.vue') },
        { path: 'profile', name: 'admin-profile', component: () => import('@/views/admin/ProfileView.vue') },
      ],
    },
    { path: '/:pathMatch(.*)*', name: 'not-found', component: () => import('@/views/NotFoundView.vue') },
  ],
})

router.beforeEach(async (to) => {
  const auth = useAuthStore()
  // A controller that cannot be reached must not stop public pages rendering: load()
  // never throws, and until it succeeds nobody counts as signed in.
  if (!auth.loaded) {
    await auth.load()
  }

  if (to.meta.requiresAuth && !auth.authenticated) {
    return { name: 'login', query: { redirect: to.fullPath } }
  }

  // Already signed in: the sign-in page has nothing to do, so go where it would have.
  if (to.name === 'login' && auth.authenticated) {
    return redirectTarget(to.query.redirect)
  }

  return true
})
