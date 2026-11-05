<template>
  <div v-loading="loading" class="settings-panel">
    <div class="panel-head">
      <h3>RAG / Agent 运行时配置</h3>
      <el-tag size="small" type="info">配置版本 {{ effective?.version ?? '-' }}</el-tag>
      <el-button size="small" text type="primary" @click="load">重新加载</el-button>
    </div>

    <el-alert type="info" :closable="false" show-icon class="tip">
      保存后<strong>立即对新问答生效</strong>，无需重启或重新发版（各实例最多 10 秒缓存延迟）。
      数据库连接串与 LLM / Rerank 的 ApiKey <strong>只走环境变量</strong>，不在此处配置。
    </el-alert>

    <el-tabs v-model="activeGroup">
      <el-tab-pane v-for="g in groups" :key="g.key" :label="g.label" :name="g.key">
        <el-card v-for="item in itemsOf(g.key)" :key="item.key" shadow="never" class="item">
          <div class="item-head">
            <div class="item-title">
              {{ item.label }}
              <el-tag size="small" :type="item.source === 'database' ? 'warning' : 'info'" effect="plain">
                {{ item.source === 'database' ? '已覆盖' : '默认值' }}
              </el-tag>
              <span v-if="item.updatedAt" class="updated">更新于 {{ fmtTime(item.updatedAt) }}</span>
            </div>
            <code class="key">{{ item.key }}</code>
          </div>

          <div class="item-body">
            <el-switch v-if="item.type === 'bool'" v-model="form[item.key]" />
            <el-select v-else-if="item.type === 'enum'" v-model="form[item.key]" style="width: 220px">
              <el-option label="Heuristic（零依赖，默认）" value="Heuristic" />
              <el-option label="Api（外部 rerank 模型）" value="Api" />
            </el-select>
            <el-input v-else-if="item.key === 'rag.system_prompt'" v-model="form[item.key]"
                      type="textarea" :rows="10" resize="vertical" />
            <el-input v-else-if="item.type === 'int' || item.type === 'number'"
                      v-model="form[item.key]" type="number" :step="item.type === 'number' ? 0.1 : 1"
                      style="width: 220px" placeholder="留空恢复默认值" />
            <el-input v-else v-model="form[item.key]" style="width: 320px" />
            <span class="desc">{{ item.description }}</span>
          </div>
        </el-card>
      </el-tab-pane>
    </el-tabs>

    <div class="actions">
      <el-button type="primary" :disabled="dirtyCount === 0" :loading="saving" @click="save">
        保存修改<span v-if="dirtyCount">（{{ dirtyCount }} 项）</span>
      </el-button>
      <el-button :disabled="dirtyCount > 0" @click="load">放弃修改</el-button>
      <el-button type="danger" plain :loading="saving" @click="reset">恢复全部默认值</el-button>
    </div>

    <el-card shadow="never" class="section">
      <template #header>当前生效值（服务端返回）</template>
      <el-descriptions :column="4" size="small" border>
        <el-descriptions-item label="TopK">{{ effective?.topK ?? '-' }}</el-descriptions-item>
        <el-descriptions-item label="候选池">{{ effective?.candidateTopK ?? '-' }}</el-descriptions-item>
        <el-descriptions-item label="混合检索">{{ yn(effective?.hybridEnabled) }}</el-descriptions-item>
        <el-descriptions-item label="元数据过滤">{{ yn(effective?.metadataFilterEnabled) }}</el-descriptions-item>
        <el-descriptions-item label="重排">{{ yn(effective?.rerankEnabled) }}</el-descriptions-item>
        <el-descriptions-item label="重排实现">{{ effective?.rerankProvider ?? '-' }}</el-descriptions-item>
        <el-descriptions-item label="温度">{{ effective?.temperature ?? '-' }}</el-descriptions-item>
        <el-descriptions-item label="最大输出">{{ effective?.maxOutputTokens ?? '-' }}</el-descriptions-item>
      </el-descriptions>
    </el-card>
  </div>
</template>

<script setup>
import { computed, onMounted, reactive, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { api } from '../api'

const groups = [
  { key: 'prompt', label: '提示词' },
  { key: 'retrieval', label: '检索' },
  { key: 'rerank', label: '重排' },
  { key: 'generation', label: '生成' }
]

const loading = ref(false)
const saving = ref(false)
const items = ref([])
const effective = ref(null)
const activeGroup = ref('retrieval')
const form = reactive({})
const original = {}

const itemsOf = (group) => items.value.filter((i) => i.group === group)
const yn = (v) => (v == null ? '-' : v ? '开启' : '关闭')
const fmtTime = (s) => (s ? s.replace('T', ' ').slice(0, 19) : '')

const isDirty = (item) =>
  String(original[item.key] ?? '') !== String(form[item.key] ?? '')

const dirtyCount = computed(() => items.value.filter(isDirty).length)

async function load() {
  loading.value = true
  try {
    const res = await api.settings.list()
    items.value = res.items || []
    effective.value = res.effective || null
    for (const item of items.value) {
      form[item.key] = item.value
      original[item.key] = item.value
    }
  } finally {
    loading.value = false
  }
}

function normalize(item, raw) {
  if (item.type !== 'int' && item.type !== 'number') return raw
  if (raw === '' || raw === null || raw === undefined) return null
  const n = Number(raw)
  if (Number.isNaN(n)) throw new Error(`${item.label} 必须是数字`)
  return item.type === 'int' ? Math.trunc(n) : n
}

async function save() {
  const payload = {}
  try {
    for (const item of items.value) {
      if (isDirty(item)) payload[item.key] = normalize(item, form[item.key])
    }
  } catch (e) {
    ElMessage.error(e.message)
    return
  }
  if (Object.keys(payload).length === 0) return

  saving.value = true
  try {
    const res = await api.settings.update(payload)
    ElMessage.success(res?.message || '配置已保存，后续问答立即生效')
    await load()
  } catch (e) {
    ElMessage.error(e.message)
  } finally {
    saving.value = false
  }
}

async function reset() {
  await ElMessageBox.confirm(
    '将删除 sys_settings 中所有 rag.* 覆盖记录，恢复为 appsettings 默认值。已保存的提示词会丢失，确定继续？',
    '恢复默认值', { type: 'warning' }
  )
  saving.value = true
  try {
    await api.settings.reset()
    ElMessage.success('已恢复默认配置')
    await load()
  } catch (e) {
    ElMessage.error(e.message)
  } finally {
    saving.value = false
  }
}

onMounted(load)
</script>

<style scoped>
.settings-panel { max-width: 1000px; }
.panel-head { display: flex; align-items: center; gap: 12px; margin-bottom: 14px; }
.panel-head h3 { margin: 0; flex: 1; }
.tip { margin-bottom: 14px; }
.item { margin-bottom: 10px; }
.item-head { display: flex; justify-content: space-between; align-items: center; margin-bottom: 8px; }
.item-title { display: flex; align-items: center; gap: 8px; font-weight: 600; }
.key { color: #909399; font-size: 12px; background: #f5f7fa; padding: 2px 6px; border-radius: 3px; }
.updated { color: #c0c4cc; font-size: 11px; font-weight: 400; }
.item-body { display: flex; align-items: flex-start; gap: 14px; flex-wrap: wrap; }
.desc { color: #909399; font-size: 12px; line-height: 32px; flex: 1; min-width: 240px; }
.actions { margin: 16px 0; display: flex; gap: 8px; }
.section { margin-bottom: 14px; }
</style>
