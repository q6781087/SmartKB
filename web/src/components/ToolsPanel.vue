<template>
  <div v-loading="loading" class="tools-panel">
    <div class="panel-head">
      <h3>工具注册表</h3>
      <el-button size="small" text type="primary" @click="load">重新加载</el-button>
      <el-button size="small" type="primary" @click="openCreate">登记工具</el-button>
    </div>

    <el-alert type="info" :closable="false" show-icon class="tip">
      登记一个内部 HTTP 接口后，模型在问答时会按需调用它（与知识库检索、台账查询并列）。
      <strong>密钥不入库</strong>：只填环境变量名，由运维在服务端设置；
      <strong>域名固定</strong>，模型给的参数只能进路径、查询串和请求体，无法改写到其他主机。
      建议只登记<strong>只读接口</strong>。保存后新问答立即生效。
    </el-alert>

    <el-table :data="rows" size="small" border>
      <el-table-column prop="displayName" label="名称" min-width="130" />
      <el-table-column prop="name" label="函数名" min-width="150">
        <template #default="{ row }"><code>{{ row.name }}</code></template>
      </el-table-column>
      <el-table-column prop="httpMethod" label="方法" width="80" />
      <el-table-column prop="urlTemplate" label="URL 模板" min-width="230" show-overflow-tooltip />
      <el-table-column label="密钥" width="100">
        <template #default="{ row }">
          <el-tag v-if="!row.authEnvVar" size="small" type="info">无需</el-tag>
          <el-tag v-else-if="row.secretConfigured" size="small" type="success">已配置</el-tag>
          <el-tag v-else size="small" type="danger">未配置</el-tag>
        </template>
      </el-table-column>
      <el-table-column label="调用权限" width="100">
        <template #default="{ row }">
          <el-tag :type="row.restricted ? 'warning' : 'info'" size="small">
            {{ row.restricted ? '受限' : '公开' }}
          </el-tag>
        </template>
      </el-table-column>
      <el-table-column label="知识库范围" width="110">
        <template #default="{ row }">
          <el-tag v-if="!(row.allowedKbIds || []).length" size="small" type="info">全部</el-tag>
          <el-tag v-else size="small">{{ row.allowedKbIds.length }} 个库</el-tag>
        </template>
      </el-table-column>
      <el-table-column label="启用" width="80">
        <template #default="{ row }">
          <el-switch :model-value="row.enabled" size="small" @change="(v) => toggle(row, v)" />
        </template>
      </el-table-column>
      <el-table-column label="操作" width="290" fixed="right">
        <template #default="{ row }">
          <el-button size="small" text type="primary" @click="openEdit(row)">编辑</el-button>
          <el-button size="small" text type="primary" @click="openGrants(row)">授权</el-button>
          <el-button size="small" text type="primary" @click="openTest(row)">试运行</el-button>
          <el-button size="small" text type="danger" @click="remove(row)">删除</el-button>
        </template>
      </el-table-column>
    </el-table>

    <el-empty v-if="!rows.length" description="尚未登记工具" :image-size="80" />

    <!-- 编辑抽屉 -->
    <el-drawer v-model="editing" :title="editId ? '编辑工具' : '登记工具'" size="62%" destroy-on-close>
      <el-form :model="form" label-width="130px" label-position="left">
        <el-form-item label="函数名" required>
          <el-input v-model="form.name" placeholder="如 query_device_status，字母开头，仅字母/数字/下划线" />
          <div class="hint">模型靠这个名字调用，创建后不建议修改；不能与内置工具 search_knowledge / query_ledger 重名。</div>
        </el-form-item>
        <el-form-item label="显示名称" required>
          <el-input v-model="form.displayName" placeholder="如 设备状态查询" />
        </el-form-item>
        <el-form-item label="工具描述" required>
          <el-input v-model="form.description" type="textarea" :rows="3"
                    placeholder="写给模型看：什么情况下应该调用、能查到什么、参数含义" />
          <div class="hint">描述决定模型是否调用，务必写清楚业务场景。</div>
        </el-form-item>
        <el-form-item label="HTTP 方法">
          <el-select v-model="form.httpMethod" style="width: 140px">
            <el-option label="GET" value="GET" />
            <el-option label="POST" value="POST" />
          </el-select>
        </el-form-item>
        <el-form-item label="URL 模板" required>
          <el-input v-model="form.urlTemplate" placeholder="https://erp.internal/api/device/{device_no}?year={year}" />
          <div class="hint">必须是 http/https 绝对地址；{param} 占位符必须在下方参数表中声明，域名不会被参数改写。</div>
        </el-form-item>

        <el-form-item label="参数定义">
          <div class="param-editor">
            <el-table :data="params" size="small" border>
              <el-table-column label="参数名" width="150">
                <template #default="{ row }"><el-input v-model="row.name" size="small" placeholder="device_no" /></template>
              </el-table-column>
              <el-table-column label="类型" width="120">
                <template #default="{ row }">
                  <el-select v-model="row.type" size="small">
                    <el-option v-for="t in ['string', 'number', 'integer', 'boolean']" :key="t" :label="t" :value="t" />
                  </el-select>
                </template>
              </el-table-column>
              <el-table-column label="位置" width="115">
                <template #default="{ row }">
                  <el-select v-model="row.location" size="small">
                    <el-option label="path" value="path" />
                    <el-option label="query" value="query" />
                    <el-option label="body" value="body" />
                  </el-select>
                </template>
              </el-table-column>
              <el-table-column label="必填" width="70">
                <template #default="{ row }"><el-switch v-model="row.required" size="small" /></template>
              </el-table-column>
              <el-table-column label="说明">
                <template #default="{ row }"><el-input v-model="row.description" size="small" placeholder="给模型看的说明" /></template>
              </el-table-column>
              <el-table-column label="" width="60">
                <template #default="{ $index }">
                  <el-button size="small" text type="danger" @click="params.splice($index, 1)">删</el-button>
                </template>
              </el-table-column>
            </el-table>
            <el-button size="small" class="add-param" @click="addParam">+ 添加参数</el-button>
            <div class="hint">path/query 对应 URL 中的占位符；body 仅 POST 可用，位置字段仅作说明，服务端按模板替换。</div>
          </div>
        </el-form-item>

        <el-form-item v-if="form.httpMethod === 'POST'" label="请求体模板">
          <el-input v-model="form.bodyTemplate" type="textarea" :rows="4"
                    placeholder='{"deviceNo":{device_no},"includeHistory":{flag}}' />
          <div class="hint">占位符写 {param}（<strong>不要加引号</strong>），字符串会自动加引号并转义，避免 JSON 注入。</div>
        </el-form-item>

        <el-divider content-position="left">鉴权（可选）</el-divider>
        <el-form-item label="请求头名">
          <el-input v-model="form.authHeaderName" placeholder="Authorization / X-Api-Key" style="width: 260px" />
        </el-form-item>
        <el-form-item label="密钥环境变量名">
          <el-input v-model="form.authEnvVar" placeholder="ERP_API_TOKEN" style="width: 260px" />
          <div class="hint">只存变量名。运维在 API 容器里设置同名环境变量，系统运行时读取，值不会出现在页面、日志或返回文本中。</div>
        </el-form-item>
        <el-form-item label="值前缀">
          <el-input v-model="form.authValuePrefix" placeholder="Bearer " style="width: 260px" />
        </el-form-item>

        <el-divider content-position="left">限制</el-divider>
        <el-form-item label="超时（秒）">
          <el-input-number v-model="form.timeoutSeconds" :min="1" :max="30" />
        </el-form-item>
        <el-form-item label="响应上限（字符）">
          <el-input-number v-model="form.maxResponseChars" :min="200" :max="20000" :step="500" />
          <div class="hint">超出会截断，防止上游大响应挤占模型上下文。</div>
        </el-form-item>
        <el-form-item label="排序">
          <el-input-number v-model="form.sortOrder" :min="0" :max="999" />
        </el-form-item>
        <el-form-item label="知识库范围">
          <el-select v-model="form.allowedKbIds" multiple filterable clearable collapse-tags
                     style="width: 100%" placeholder="不选 = 全部知识库可用">
            <el-option v-for="k in kbOptions" :key="k.id" :label="k.name" :value="k.id" />
          </el-select>
          <div class="hint">限定工具只服务于特定知识库：问答指定的范围与其无交集时，本次不注入该工具。</div>
        </el-form-item>
        <el-form-item label="启用">
          <el-switch v-model="form.enabled" />
          <span class="hint">同时最多启用 20 个工具。</span>
        </el-form-item>
      </el-form>

      <template #footer>
        <el-button @click="editing = false">取消</el-button>
        <el-button type="primary" :loading="saving" @click="save">保存</el-button>
      </template>
    </el-drawer>

    <!-- 试运行 -->
    <el-dialog v-model="testing" title="试运行工具" width="620px" destroy-on-close>
      <el-alert type="warning" :closable="false" show-icon class="tip">
        会真实调用上游接口，请确认该操作无副作用。响应只回显文本且按配置截断。
      </el-alert>
      <el-form label-width="140px" label-position="left">
        <el-form-item v-for="p in testParams" :key="p.name" :label="p.name">
          <el-input v-model="testArgs[p.name]" :placeholder="p.description || p.type" />
        </el-form-item>
        <el-empty v-if="!testParams.length" description="该工具无参数" :image-size="60" />
      </el-form>
      <template #footer>
        <el-button @click="testing = false">关闭</el-button>
        <el-button type="primary" :loading="running" @click="runTest">执行</el-button>
      </template>
      <div v-if="testResult" class="test-result">
        <div class="result-head">
          <el-tag :type="testResult.success ? 'success' : 'danger'" size="small">
            {{ testResult.success ? '调用成功' : '调用失败' }}
          </el-tag>
          <span>{{ testResult.elapsedMs }} ms</span>
        </div>
        <pre>{{ testResult.output }}</pre>
      </div>
    </el-dialog>

    <!-- 调用授权白名单 -->
    <el-dialog v-model="granting" :title="`调用授权 · ${grantToolName}`" width="640px" destroy-on-close>
      <el-alert type="info" :closable="false" show-icon class="tip">
        <strong>一个都不选 = 公开</strong>，所有可登录用户都能在问答中间接调用此工具；
        一旦选中任意用户或角色，则<strong>仅这些对象与管理员</strong>可用。
        网关（服务间调用）同样按绑定账号的权限过滤。保存后新问答立即生效。
      </el-alert>
      <el-form label-width="90px" label-position="left" v-loading="grantLoading">
        <el-form-item label="授权用户">
          <el-select v-model="grantUserIds" multiple filterable clearable collapse-tags
                     style="width: 100%" placeholder="不选表示不按用户限制">
            <el-option v-for="u in grantUsers" :key="u.id" :label="u.label" :value="u.id" />
          </el-select>
        </el-form-item>
        <el-form-item label="授权角色">
          <el-select v-model="grantRoleIds" multiple filterable clearable collapse-tags
                     style="width: 100%" placeholder="不选表示不按角色限制">
            <el-option v-for="r in grantRoles" :key="r.id" :label="r.label" :value="r.id" />
          </el-select>
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="granting = false">取消</el-button>
        <el-button type="primary" :loading="grantSaving" @click="saveGrants">保存授权</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup>
import { onMounted, reactive, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { api } from '../api'

const loading = ref(false)
const saving = ref(false)
const running = ref(false)
const rows = ref([])
const editing = ref(false)
const editId = ref(null)
const params = ref([])
const testing = ref(false)
const testParams = ref([])
const testArgs = reactive({})
const testResult = ref(null)
const testId = ref(null)
const granting = ref(false)
const grantLoading = ref(false)
const grantSaving = ref(false)
const grantToolId = ref(null)
const grantToolName = ref('')
const grantUsers = ref([])
const grantRoles = ref([])
const grantUserIds = ref([])
const grantRoleIds = ref([])
const kbOptions = ref([])

const blank = () => ({
  name: '', displayName: '', description: '', httpMethod: 'GET', urlTemplate: '',
  bodyTemplate: '', parametersJson: '[]', authHeaderName: '', authEnvVar: '',
  authValuePrefix: '', timeoutSeconds: 8, maxResponseChars: 4000,
  enabled: true, sortOrder: 0, allowedKbIds: []
})
const form = reactive(blank())

function fill(target, src) {
  Object.assign(target, blank(), {
    ...src,
    bodyTemplate: src.bodyTemplate || '',
    authHeaderName: src.authHeaderName || '',
    authEnvVar: src.authEnvVar || '',
    authValuePrefix: src.authValuePrefix || '',
    allowedKbIds: src.allowedKbIds || []
  })
}

function parseParams(json) {
  try { return JSON.parse(json || '[]') } catch { return [] }
}

const addParam = () => params.value.push({
  name: '', type: 'string', location: 'query', required: false, description: ''
})

async function load() {
  loading.value = true
  try {
    rows.value = await api.tools.list()
  } catch (e) {
    ElMessage.error(e.message)
  } finally {
    loading.value = false
  }
}

function openCreate() {
  editId.value = null
  fill(form, blank())
  params.value = []
  editing.value = true
}

function openEdit(row) {
  editId.value = row.id
  fill(form, row)
  params.value = parseParams(row.parametersJson)
  editing.value = true
}

async function save() {
  const names = params.value.map((p) => p.name.trim())
  if (names.some((n) => !n)) return ElMessage.warning('参数名不能为空')
  if (new Set(names).size !== names.length) return ElMessage.warning('参数名不能重复')

  const payload = {
    ...form,
    parametersJson: JSON.stringify(params.value.map((p) => ({ ...p, name: p.name.trim() }))),
    bodyTemplate: form.httpMethod === 'POST' ? form.bodyTemplate : ''
  }
  delete payload.id
  delete payload.secretConfigured
  delete payload.restricted
  delete payload.createdAt
  delete payload.updatedAt
  payload.allowedKbIds = form.allowedKbIds || []

  saving.value = true
  try {
    if (editId.value) await api.tools.update(editId.value, payload)
    else await api.tools.create(payload)
    ElMessage.success('已保存，新问答立即生效')
    editing.value = false
    await load()
  } catch (e) {
    ElMessage.error(e.message)
  } finally {
    saving.value = false
  }
}

async function toggle(row, value) {
  try {
    await api.tools.setEnabled(row.id, value)
    row.enabled = value
    ElMessage.success(value ? '已启用' : '已停用')
  } catch (e) {
    ElMessage.error(e.message)
    await load()
  }
}

async function remove(row) {
  try {
    await ElMessageBox.confirm(`确认删除工具「${row.displayName}」？`, '删除确认', { type: 'warning' })
    await api.tools.remove(row.id)
    ElMessage.success('已删除')
    await load()
  } catch (e) {
    if (e !== 'cancel' && e?.message) ElMessage.error(e.message)
  }
}

function openTest(row) {
  testId.value = row.id
  testParams.value = parseParams(row.parametersJson)
  Object.keys(testArgs).forEach((k) => delete testArgs[k])
  testResult.value = null
  testing.value = true
}

async function runTest() {
  running.value = true
  testResult.value = null
  try {
    const args = {}
    for (const p of testParams.value) {
      const v = testArgs[p.name]
      if (v === undefined || v === '') continue
      args[p.name] = p.type === 'boolean' ? (v === 'true' || v === '1')
        : p.type === 'string' ? v : Number(v)
    }
    testResult.value = await api.tools.test(testId.value, args)
  } catch (e) {
    ElMessage.error(e.message)
  } finally {
    running.value = false
  }
}

async function openGrants(row) {
  grantToolId.value = row.id
  grantToolName.value = row.displayName
  grantUserIds.value = []
  grantRoleIds.value = []
  granting.value = true
  grantLoading.value = true
  try {
    const d = await api.tools.grants(row.id)
    grantUserIds.value = d.userIds || []
    grantRoleIds.value = d.roleIds || []
    grantUsers.value = d.availableUsers || []
    grantRoles.value = d.availableRoles || []
  } catch (e) {
    ElMessage.error(e.message)
  } finally {
    grantLoading.value = false
  }
}

async function saveGrants() {
  grantSaving.value = true
  try {
    await api.tools.setGrants(grantToolId.value, {
      userIds: grantUserIds.value,
      roleIds: grantRoleIds.value
    })
    ElMessage.success('授权已保存，新问答立即生效')
    granting.value = false
    await load()
  } catch (e) {
    ElMessage.error(e.message)
  } finally {
    grantSaving.value = false
  }
}

onMounted(() => {
  load()
  api.kbs.list().then((kbs) => { kbOptions.value = kbs || [] }).catch(() => {})
})
</script>

<style scoped>
.panel-head { display: flex; align-items: center; gap: 10px; margin-bottom: 12px; }
.panel-head h3 { margin: 0; flex: 1; font-size: 16px; }
.tip { margin-bottom: 14px; line-height: 1.7; }
.hint { color: #909399; font-size: 12px; line-height: 1.6; margin-top: 2px; }
.param-editor { width: 100%; }
.add-param { margin-top: 8px; }
.test-result { margin-top: 12px; }
.result-head { display: flex; align-items: center; gap: 12px; margin-bottom: 8px; color: #909399; font-size: 12px; }
.test-result pre { background: #f5f7fa; padding: 12px; border-radius: 4px; max-height: 320px; overflow: auto;
  white-space: pre-wrap; word-break: break-all; font-size: 12px; }
</style>
