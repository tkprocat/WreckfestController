import { createRouter, createWebHistory } from 'vue-router'
import { useAuthStore } from '@/stores/auth'
import { redirectTarget } from './redirect'
import { BASE } from '@/base'

declare module 'vue-router' {
  interface RouteMeta {
    /** Only for someone signed in: anyone else is sent to sign in first. */
    requiresAuth?: boolean
    /**
     * Only for someone who may manage this server: someone signed in without access to it is
     * told so. Their own profile is not the server's, so it turns this off.
     */
    requiresServer?: boolean
    /** Only for a global admin (the accounts). */
    requiresAdmin?: boolean
    /** A public page in the front page's wide layout. */
    spectator?: boolean
  }
}

// The portal answers every path under the site's own outside /api and /hubs with
// index.html, so these routes work as deep links and on reload.
export const router = createRouter({
  history: createWebHistory(BASE),
  routes: [
    { path: '/', name: 'home', component: () => import('@/views/HomeView.vue'), meta: { spectator: true } },
    { path: '/tracks', name: 'tracks', component: () => import('@/views/tracks/TrackListView.vue'), meta: { spectator: true } },
    // A layout's own address is what other pages link to; without one, the first is shown.
    { path: '/tracks/:track/:variant?', name: 'track', component: () => import('@/views/tracks/TrackView.vue'), meta: { spectator: true } },
    { path: '/ai', name: 'assistant', component: () => import('@/views/AssistantView.vue'), meta: { spectator: true } },
    { path: '/login', name: 'login', component: () => import('@/views/LoginView.vue') },
    { path: '/no-access', name: 'no-access', component: () => import('@/views/NoAccessView.vue') },
    {
      path: '/admin',
      component: () => import('@/layouts/AdminLayout.vue'),
      meta: { requiresAuth: true, requiresServer: true },
      children: [
        { path: '', name: 'admin-dashboard', component: () => import('@/views/admin/DashboardView.vue') },
        { path: 'server', name: 'admin-server', component: () => import('@/views/admin/ServerControlView.vue') },
        { path: 'config', name: 'admin-config', component: () => import('@/views/admin/ConfigView.vue') },
        { path: 'settings', name: 'admin-settings', component: () => import('@/views/admin/SettingsView.vue') },
        { path: 'assistant', name: 'admin-assistant', component: () => import('@/views/admin/AdminAssistantView.vue') },
        { path: 'tags', name: 'admin-tags', component: () => import('@/views/admin/TagsView.vue') },
        { path: 'tracks', name: 'admin-tracks', component: () => import('@/views/admin/TracksView.vue') },
        { path: 'rotation', name: 'admin-rotation', component: () => import('@/views/admin/RotationView.vue') },
        { path: 'cups', name: 'admin-cups', component: () => import('@/views/admin/CupsView.vue') },
        { path: 'collections', name: 'admin-collections', component: () => import('@/views/admin/CollectionsView.vue') },
        { path: 'users', name: 'admin-users', component: () => import('@/views/admin/UsersView.vue'), meta: { requiresAdmin: true } },
        { path: 'profile', name: 'admin-profile', component: () => import('@/views/admin/ProfileView.vue'), meta: { requiresServer: false } },
      ],
    },
    { path: '/:pathMatch(.*)*', name: 'not-found', component: () => import('@/views/NotFoundView.vue') },
  ],
})

router.beforeEach(async (to) => {
  const auth = useAuthStore()
  // A portal that cannot be reached must not stop public pages rendering: load()
  // never throws, and until it succeeds nobody counts as signed in.
  if (!auth.loaded) {
    await auth.load()
  }

  if (to.meta.requiresAuth && !auth.authenticated) {
    return { name: 'login', query: { redirect: to.fullPath } }
  }

  // Signed in, but not to manage this server, or not an admin: signing in again would not help.
  if ((to.meta.requiresServer && !auth.managesThisServer) || (to.meta.requiresAdmin && !auth.isAdmin)) {
    return { name: 'no-access', query: { from: to.fullPath } }
  }

  // Already signed in: the sign-in page has nothing to do, so go where it would have.
  if (to.name === 'login' && auth.authenticated) {
    return redirectTarget(to.query.redirect)
  }

  return true
})
