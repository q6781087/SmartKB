<template>
  <div v-loading="loading" class="audit-panel">
    <div class="panel-head">
      <h3>操作审计日志</h3>
      <el-tag size="small" type="info">今日 {{ todayCount }} 次 · 失败 {{ todayFailures }} 次</el-tag>
      <el-button size="small" text type="primary" @click="load(page)">刷新</el-button>
    </div>

    <el-alert type="info" :closable="false" show-icon class="tip">
      自动记录所有写操作（登录、用户/角色/知识库/文档/授权/配置变更）。
      <strong>仅保存元数据</strong>，不记录请求体与响应体——密码、ApiKey、文档正文不会入库。
    </el-alert>

    <div class="filters">
      <el-select v-model="filters.category" placeholder="全部分类" clearable
                 style="width: 155px" @change="load(1)">
        <el-option v-for="c in categories" :key="c.value" :label="c.label" :value="c.value" />
      </el-select>
      <el-select v-model="filters.success" placeholder="全部结果" clearable
                 style="width: 120px" @change="load(1)">
        <el-option label="成功" value="true" />
        <el-option label="失败" value="false" />
      </el-select>
      <el-select v-model="filters.days" style="width: 120px" @change="load(1)">
        <el-option :value="1" label="近 1 天" />
        <el-option :value="7" label="近 7 天" />
        <el-option :value="30" label="近 30 天" />
        <el-option :value="90" label="近 90 天" />
      </el-select>
      <el-input v-model="filters.keyword" placeholder="用户名 / 路径 / 动作" clearable
                style="width: 230px" @keyup.enter="load(1)" @clear="load(1)" />
      <el-button type="primary" @click="load(1)">查询</el-button>
    </div>

    <el-table :data="rows" size="small" stripe border>
      <el-table-column label="时间" width="160">
        <template #default="{ row }">{{ fmtTime(row.createdAt) }}</template>
      </el-table-column>
      <el-table-column label="用户" width="120" show-overflow-tooltip>
        <template #default="{ row }">{{ row.userName || (row.userId ? `用户${row.userId}` : '匿名') }}</template>
      </el-table-column>
      <el-table-column label="操作" width="150" show-overflow-tooltip>
        <template #default="{ row }">
          {{ actionLabels[row.action] || row.action }}
          <code class="action-key">{{ row.action }}</code>
        </template>
      </el-table-column>
      <el-table-column label="分类" width="100">
        <template #default="{ row }">{{ categoryLabels[row.category] || row.category }}</template>
      </el-table-column>
      <el-table-column label="请求" min-width="220" show-overflow-tooltip>
        <template #default="{ row }">
          <span class="method">{{ row.method }}</span> {{ row.path }}
        </template>
      </el-table-column>
      <el-table-column label="状态" width="90">
        <template #default="{ row }">
          <el-tag size="small" :type="statusType(row.statusCode)">{{ row.statusCode }}</el-tag>
        </template>
      </el-table-column>
      <el-table-column label="耗时" width="80">
        <template #default="{ row }">{{ row.durationMs }}ms</template>
      </el-table-column>
      <el-table-column prop="ipAddress" label="来源 IP" width="130" show-overflow-tooltip />
      <el-table-column label="结果" width="80">
        <template #default="{ row }">
          <el-tag size="small" effect="plain" :type="row.success ? 'success' : 'danger'">
            {{ row.success ? '成功' : '失败' }}
          </el-tag>
        </template>
      </el-table-column>
      <el-table-column label="失败原因" min-width="160" show-overflow-tooltip>
        <template #default="{ row }">{{ row.errorMessage || '' }}</template>
      </el-table-column>
    </el-table>

    <el-pagination class="pager" background layout="total, sizes, prev, pager, next"
                   :total="total" :current-page="page" :page-size="pageSize"
                   :page-sizes="[20, 50, 100]"
                   @current-change="load" @size-change="onSizeChange" />
  </div>
</template>

<script setup>
import { onMounted, reactive, ref } from 'vue'
import { ElMessage } from 'element-plus'
import { api } from '../api'

const categories = [
  { value: 'auth', label: '认证' },
  { value: 'users', label: '用户' },
  { value: 'roles', label: '角色' },
  { value: 'kbs', label: '知识库' },
  { value: 'documents', label: '文档' },
  { value: 'permissions', label: '授权' },
  { value: 'settings', label: '配置' },
  { value: 'tools', label: '工具' },
  { value: 'chats', label: '会话' },
  { value: 'other', label: '其它' }
]

const categoryLabels = Object.fromEntries(categories.map((c) => [c.value, c.label]))
const actionLabels = {
  login: '登录', change_password: '修改密码',
  create_user: '新建用户', update_user: '编辑用户', reset_password: '重置密码',
  create_role: '新建角色', delete_role: '删除角色',
  create_kb: '新建知识库', update_kb: '编辑知识库', delete_kb: '删除知识库',
  upload_document: '上传文档', retry_document: '重试解析', delete_document: '删除文档',
  set_document_grants: '设置文档授权',
  grant_permission: '授予权限', revoke_permission: '撤销权限',
  create_session: '新建会话', delete_session: '删除会话',
  update_settings: '保存配置', reset_settings: '恢复默认配置',
  create_tool: '登记工具', update_tool: '编辑工具', delete_tool: '删除工具',
  toggle_tool: '启停工具', test_tool: '试运行工具',
  set_tool_grants: '设置工具授权'
}

const loading = ref(false)
const rows = ref([])
const total = ref(0)
const page = ref(1)
const pageSize = ref(20)
const todayCount = ref(0)
const todayFailures = ref(0)
const filters = reactive({ category: '', success: '', days: 30, keyword: '' })

const fmtTime = (s) => (s ? new Date(s).toLocaleString('zh-CN', { hour12: false }) : '')
const statusType = (code) => (code >= 500 ? 'danger' : code >= 400 ? 'warning' : 'success')

function onSizeChange(size) {
  pageSize.value = size
  load(1)
}

async function load(p = 1) {
  loading.value = true
  const params = { page: p, pageSize: pageSize.value }
  if (filters.category) params.category = filters.category
  if (filters.success !== '') params.success = filters.success === 'true'
  if (filters.keyword?.trim()) params.keyword = filters.keyword.trim()
  params.from = new Date(Date.now() - filters.days * 86400000).toISOString()

  try {
    // axios 拦截器已解包 resp.data，后端直接返回 AuditLogPageDto
    const res = await api.audit.list(params)
    rows.value = res.items || []
    total.value = res.total || 0
    page.value = res.page || p
    todayCount.value = res.todayCount || 0
    todayFailures.value = res.todayFailures || 0
  } catch (e) {
    ElMessage.error(e.message)
  } finally {
    loading.value = false
  }
}

onMounted(() => load(1))
</script>

<style scoped>
.audit-panel { max-width: 1300px; }
.panel-head { display: flex; align-items: center; gap: 12px; margin-bottom: 14px; }
.panel-head h3 { margin: 0; flex: 1; }
.tip { margin-bottom: 14px; }
.filters { display: flex; gap: 8px; flex-wrap: wrap; margin-bottom: 14px; }
.pager { margin-top: 14px; justify-content: flex-end; }
.action-key { color: #c0c4cc; font-size: 11px; margin-left: 4px; }
.method { color: #409eff; font-weight: 600; }
</style>
