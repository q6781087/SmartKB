import { createRouter, createWebHistory } from 'vue-router'
import { useAuthStore } from '../stores/auth'

const router = createRouter({
  history: createWebHistory(),
  routes: [
    { path: '/login', component: () => import('../views/Login.vue') },
    { path: '/', redirect: '/chat' },
    { path: '/chat', component: () => import('../views/Chat.vue') },
    {
      path: '/admin',
      component: () => import('../views/Admin.vue'),
      meta: { requiresAdminOrManager: true }
    },
    { path: '/:pathMatch(.*)*', redirect: '/chat' }
  ]
})

// 路由守卫：未登录去 /login；管理后台要求 admin / kb_manager
router.beforeEach((to) => {
  const auth = useAuthStore()
  if (to.path !== '/login' && !auth.token) return '/login'
  if (to.meta.requiresAdminOrManager && !auth.canManage) {
    return { path: '/chat', query: { denied: '1' } }
  }
  return true
})

export default router
