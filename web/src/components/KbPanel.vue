<template>
  <el-card>
    <div class="toolbar">
      <h3>知识库管理</h3>
      <el-button type="primary" @click="openEdit()">新建知识库</el-button>
    </div>

    <!-- 知识库列表 -->
    <el-table :data="kbs" border highlight-current-row @current-change="selectKb">
      <el-table-column prop="id" label="ID" width="70" />
      <el-table-column prop="name" label="名称" width="180" />
      <el-table-column prop="description" label="说明" />
      <el-table-column prop="documentCount" label="文档数" width="90" />
      <el-table-column label="台账双轨" width="90">
        <template #default="{ row }">
          <el-tag :type="row.enableLedger ? 'success' : 'info'" size="small">
            {{ row.enableLedger ? '开启' : '关闭' }}
          </el-tag>
        </template>
      </el-table-column>
      <el-table-column label="操作" width="160">
        <template #default="{ row }">
          <el-button size="small" @click.stop="openEdit(row)">编辑</el-button>
          <el-button size="small" type="danger" @click.stop="removeKb(row)">删除</el-button>
        </template>
      </el-table-column>
    </el-table>

    <!-- 选中库详情 -->
    <div v-if="current" class="kb-detail">
      <el-tabs>
        <!-- 文档 -->
        <el-tab-pane label="文档管理">
          <div class="upload-row">
            <el-upload :show-file-list="false" :auto-upload="false" multiple
                       accept=".docx,.pdf,.xlsx,.txt,.md"
                       :on-change="(f) => upload(f.raw)">
              <el-button type="primary">上传文档（docx/pdf/xlsx/txt/md）</el-button>
            </el-upload>
            <el-button :loading="loadingDocs" @click="loadDocs">刷新状态</el-button>
            <span class="hint">上传后异步解析入库，状态自动刷新</span>
          </div>

          <el-table :data="docs" border size="small">
            <el-table-column prop="fileName" label="文件名" min-width="200" />
            <el-table-column label="类型" width="80">
              <template #default="{ row }">{{ typeText(row.docType) }}</template>
            </el-table-column>
            <el-table-column label="状态" width="100">
              <template #default="{ row }">
                <el-tag :type="statusTag(row.status)" size="small">{{ statusText(row.status) }}</el-tag>
              </template>
            </el-table-column>
            <el-table-column prop="chunkCount" label="分块数" width="80" />
            <el-table-column label="失败原因" min-width="160">
              <template #default="{ row }">
                <span v-if="row.errorMessage" class="err">{{ row.errorMessage }}</span>
              </template>
            </el-table-column>
            <el-table-column label="操作" width="140">
              <template #default="{ row }">
                <el-button size="small" :disabled="row.status !== 3" @click="retry(row)">重试</el-button>
                <el-button size="small" type="danger" @click="removeDoc(row)">删除</el-button>
              </template>
            </el-table-column>
          </el-table>
        </el-tab-pane>

        <!-- 字段 schema -->
        <el-tab-pane label="字段配置">
          <p class="hint">自定义元数据字段：文档上传时按此结构填写，用于台账过滤与扩展查询。</p>
          <el-table :data="fields" border size="small">
            <el-table-column label="字段名(name)" min-width="140">
              <template #default="{ row }"><el-input v-model="row.name" size="small" /></template>
            </el-table-column>
            <el-table-column label="显示名(label)" min-width="140">
              <template #default="{ row }"><el-input v-model="row.label" size="small" /></template>
            </el-table-column>
            <el-table-column label="类型" width="120">
              <template #default="{ row }">
                <el-select v-model="row.type" size="small">
                  <el-option value="string" /><el-option value="number" /><el-option value="date" />
                </el-select>
              </template>
            </el-table-column>
            <el-table-column label="可过滤" width="80">
              <template #default="{ row }"><el-switch v-model="row.filterable" /></template>
            </el-table-column>
            <el-table-column label="操作" width="80">
              <template #default="{ $index }">
                <el-button size="small" type="danger" @click="fields.splice($index, 1)">删</el-button>
              </template>
            </el-table-column>
          </el-table>
          <div class="schema-actions">
            <el-button size="small" @click="fields.push({ name: '', label: '', type: 'string', filterable: true })">＋ 加字段</el-button>
            <el-button size="small" type="primary" @click="saveSchema">保存字段配置</el-button>
          </div>
        </el-tab-pane>

        <!-- 权限 -->
        <el-tab-pane label="权限管理">
          <div class="upload-row">
            <el-select v-model="grantUser" placeholder="选择用户" style="width:200px" clearable>
              <el-option v-for="u in users" :key="`u-${u.id}`" :value="`u-${u.id}`" :label="u.displayName" />
            </el-select>
            <el-select v-model="grantRole" placeholder="或选择角色" style="width:200px" clearable>
              <el-option v-for="r in roles" :key="`r-${r.id}`" :value="`r-${r.id}`" :label="`角色：${r.name}`" />
            </el-select>
            <el-select v-model="grantLevel" style="width:130px">
              <el-option :value="1" label="只读（问答）" />
              <el-option :value="2" label="可管理" />
            </el-select>
            <el-button type="primary" @click="grant">授予</el-button>
          </div>

          <el-table :data="permissions" border size="small">
            <el-table-column label="被授权方">
              <template #default="{ row }">{{ row.userDisplayName || `角色：${row.roleName}` }}</template>
            </el-table-column>
            <el-table-column label="级别" width="110">
              <template #default="{ row }">
                <el-tag :type="row.level === 2 ? 'warning' : 'info'" size="small">
                  {{ row.level === 2 ? '可管理' : '只读' }}
                </el-tag>
              </template>
            </el-table-column>
            <el-table-column label="操作" width="90">
              <template #default="{ row }">
                <el-button size="small" type="danger" @click="revoke(row)">移除</el-button>
              </template>
            </el-table-column>
          </el-table>
        </el-tab-pane>
      </el-tabs>
    </div>

    <!-- 新建/编辑知识库 -->
    <el-dialog v-model="dialog" :title="kbForm.id ? '编辑知识库' : '新建知识库'" width="480">
      <el-form :model="kbForm" label-width="110px">
        <el-form-item label="名称" required><el-input v-model="kbForm.name" /></el-form-item>
        <el-form-item label="说明"><el-input v-model="kbForm.description" /></el-form-item>
        <el-form-item label="台账双轨入库">
          <el-switch v-model="kbForm.enableLedger" />
          <span class="hint" style="margin-left:8px">Excel 逐行入台账表，支持精确查询</span>
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="dialog = false">取消</el-button>
        <el-button type="primary" @click="saveKb">保存</el-button>
      </template>
    </el-dialog>
  </el-card>
</template>

<script setup>
import { onBeforeUnmount, onMounted, reactive, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { api } from '../api'

const kbs = ref([])
const users = ref([])
const roles = ref([])
const docs = ref([])
const permissions = ref([])
const fields = ref([])
const current = ref(null)
const dialog = ref(false)
const loadingDocs = ref(false)
const grantUser = ref('')
const grantRole = ref('')
const grantLevel = ref(1)
const kbForm = reactive({ id: null, name: '', description: '', enableLedger: true })

let pollTimer = null

async function load() {
  kbs.value = await api.kbs.list()
  // /api/users 返回分页对象 { items, total, page, pageSize }，兼容裸数组
  const res = await api.users.list()
  users.value = Array.isArray(res) ? res : (res.items ?? [])
  roles.value = await api.roles.list()
}

function selectKb(row) {
  if (!row) return
  current.value = row
  loadDocs()
  loadPermissions()
  try { fields.value = JSON.parse(row.fieldSchemaJson || '[]') } catch { fields.value = [] }
}

// ---------- 文档 ----------
async function loadDocs() {
  if (!current.value) return
  loadingDocs.value = true
  try { docs.value = await api.docs.list(current.value.id) }
  finally { loadingDocs.value = false }
}

async function upload(file) {
  if (!file) return
  try {
    await api.docs.upload(current.value.id, file)
    ElMessage.success(`「${file.name}」已上传，开始解析`)
    loadDocs()
  } catch (e) { ElMessage.error(e.message) }
}

async function retry(row) {
  await api.docs.retry(current.value.id, row.id)
  ElMessage.success('已重新排队解析')
  loadDocs()
}

async function removeDoc(row) {
  await ElMessageBox.confirm(`删除文档「${row.fileName}」及其全部向量数据？`, '确认', { type: 'warning' })
  await api.docs.remove(current.value.id, row.id)
  loadDocs()
  load()
}

// 存在解析中文档时轮询刷新
function startPoll() {
  pollTimer = setInterval(() => {
    if (current.value && docs.value.some((d) => d.status === 0 || d.status === 1)) loadDocs()
  }, 3000)
}

// ---------- 字段 schema ----------
async function saveSchema() {
  const cleaned = fields.value.filter((f) => f.name?.trim() && f.label?.trim())
  await api.kbs.update(current.value.id, {
    name: current.value.name,
    description: current.value.description || null,
    fieldSchemaJson: JSON.stringify(cleaned),
    enableLedger: current.value.enableLedger
  })
  ElMessage.success('字段配置已保存')
  load()
}

// ---------- 权限 ----------
async function loadPermissions() {
  permissions.value = await api.kbs.permissions(current.value.id)
}

async function grant() {
  if (grantUser.value && grantRole.value) { ElMessage.warning('用户和角色只能二选一'); return }
  const target = grantUser.value || grantRole.value
  if (!target) { ElMessage.warning('请选择用户或角色'); return }
  const [type, id] = target.split('-')
  await api.kbs.grant({
    kbId: current.value.id,
    userId: type === 'u' ? Number(id) : null,
    roleId: type === 'r' ? Number(id) : null,
    level: grantLevel.value
  })
  ElMessage.success('已授予')
  grantUser.value = ''
  grantRole.value = ''
  loadPermissions()
}

async function revoke(row) {
  await api.kbs.revoke(row.id)
  loadPermissions()
}

// ---------- 知识库 CRUD ----------
function openEdit(row) {
  Object.assign(kbForm, row
    ? { id: row.id, name: row.name, description: row.description, enableLedger: row.enableLedger }
    : { id: null, name: '', description: '', enableLedger: true })
  dialog.value = true
}

async function saveKb() {
  const payload = {
    name: kbForm.name, description: kbForm.description || null,
    fieldSchemaJson: kbForm.id ? (current.value?.fieldSchemaJson || '[]') : '[]',
    enableLedger: kbForm.enableLedger
  }
  if (kbForm.id) await api.kbs.update(kbForm.id, payload)
  else await api.kbs.create(payload)
  ElMessage.success('已保存')
  dialog.value = false
  load()
}

async function removeKb(row) {
  await ElMessageBox.confirm(`删除知识库「${row.name}」及其全部文档与向量？不可恢复！`, '危险操作', { type: 'error' })
  await api.kbs.remove(row.id)
  ElMessage.success('已删除')
  if (current.value?.id === row.id) { current.value = null; docs.value = [] }
  load()
}

const typeText = (t) => ({ 1: 'Word', 2: 'PDF', 3: 'Excel', 4: 'TXT', 5: 'MD' }[t] || t)
const statusText = (s) => ({ 0: '待解析', 1: '解析中', 2: '已入库', 3: '失败' }[s] ?? s)
const statusTag = (s) => ({ 0: 'info', 1: 'warning', 2: 'success', 3: 'danger' }[s] || 'info')

onMounted(() => { load(); startPoll() })
onBeforeUnmount(() => clearInterval(pollTimer))
</script>

<style scoped>
.toolbar { display: flex; justify-content: space-between; align-items: center; margin-bottom: 12px; }
.toolbar h3 { margin: 0; }
.kb-detail { margin-top: 20px; border-top: 1px solid #e4e7ed; padding-top: 12px; }
.upload-row { display: flex; align-items: center; gap: 10px; margin-bottom: 12px; flex-wrap: wrap; }
.hint { color: #909399; font-size: 12px; }
.err { color: #f56c6c; font-size: 12px; }
.schema-actions { margin-top: 8px; display: flex; gap: 8px; }
</style>
