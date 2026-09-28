import axios from 'axios'
import { useAuthStore } from '../stores/auth'
import router from '../router'

const http = axios.create({ baseURL: '/api', timeout: 30000 })

// 请求拦截：附 JWT
http.interceptors.request.use((config) => {
  const auth = useAuthStore()
  if (auth.token) config.headers.Authorization = `Bearer ${auth.token}`
  return config
})

// 响应拦截：401 一律回登录页；业务错误统一 ElMessage
http.interceptors.response.use(
  (resp) => resp.data,
  (err) => {
    const auth = useAuthStore()
    if (err.response?.status === 401) {
      auth.logout()
      router.push('/login')
    }
    const msg = err.response?.data?.message || err.message || '请求失败'
    return Promise.reject(new Error(msg))
  }
)

// ---------- API 聚合 ----------

export const api = {
  // 认证
  login: (data) => http.post('/auth/login', data),
  changePassword: (data) => http.post('/auth/change-password', data),

  // 用户
  users: {
    list: () => http.get('/users'),
    create: (d) => http.post('/users', d),
    update: (id, d) => http.put(`/users/${id}`, d),
    resetPassword: (id, d) => http.put(`/users/${id}/password`, d),
    remove: (id) => http.delete(`/users/${id}`)
  },

  // 角色
  roles: {
    list: () => http.get('/roles'),
    create: (d) => http.post('/roles', d),
    remove: (id) => http.delete(`/roles/${id}`)
  },

  // 知识库
  kbs: {
    list: () => http.get('/kbs'),
    create: (d) => http.post('/kbs', d),
    update: (id, d) => http.put(`/kbs/${id}`, d),
    remove: (id) => http.delete(`/kbs/${id}`),
    permissions: (kbId) => http.get(`/kbs/${kbId}/permissions`),
    grant: (d) => http.post('/kbs/permissions', d),
    revoke: (permissionId) => http.delete(`/kbs/permissions/${permissionId}`)
  },

  // 文档（路由均带 kbId：/kbs/{kbId}/documents）
  docs: {
    list: (kbId) => http.get(`/kbs/${kbId}/documents`),
    upload: (kbId, file, metadataJson) => {
      const form = new FormData()
      form.append('file', file)
      if (metadataJson) form.append('metadataJson', metadataJson)
      return http.post(`/kbs/${kbId}/documents`, form, {
        headers: { 'Content-Type': 'multipart/form-data' },
        timeout: 120000
      })
    },
    retry: (kbId, id) => http.post(`/kbs/${kbId}/documents/${id}/retry`),
    remove: (kbId, id) => http.delete(`/kbs/${kbId}/documents/${id}`)
  },

  // 会话
  chats: {
    list: () => http.get('/chats'),
    messages: (id) => http.get(`/chats/${id}/messages`),
    remove: (id) => http.delete(`/chats/${id}`)
  }
}

export default http
