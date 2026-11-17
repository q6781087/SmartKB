import { defineStore } from 'pinia'

const TOKEN_KEY = 'smartkb_token'
const USER_KEY = 'smartkb_user'

export const useAuthStore = defineStore('auth', {
  state: () => ({
    token: localStorage.getItem(TOKEN_KEY) || '',
    user: JSON.parse(localStorage.getItem(USER_KEY) || 'null')
  }),

  getters: {
    isLoggedIn: (s) => !!s.token,
    // admin 角色是唯一真相源；兼容旧登录响应里只有 isAdmin 字段的情况
    isAdmin: (s) => !!s.user && (!!s.user.isAdmin || (s.user.roles || []).includes('admin')),
    canManage(s) {
      return this.isAdmin || (!!s.user && (s.user.roles || []).includes('kb_manager'))
    }
  },

  actions: {
    setLogin({ token, user }) {
      this.token = token
      this.user = user
      localStorage.setItem(TOKEN_KEY, token)
      localStorage.setItem(USER_KEY, JSON.stringify(user))
    },

    logout() {
      this.token = ''
      this.user = null
      localStorage.removeItem(TOKEN_KEY)
      localStorage.removeItem(USER_KEY)
    }
  }
})
