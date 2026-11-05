<template>
  <div v-loading="loading" class="usage-panel">
    <div class="panel-head">
      <h3>AI 调用观测</h3>
      <el-radio-group v-model="days" size="small" @change="load">
        <el-radio-button :value="7">近 7 天</el-radio-button>
        <el-radio-button :value="30">近 30 天</el-radio-button>
        <el-radio-button :value="90">近 90 天</el-radio-button>
      </el-radio-group>
      <el-button size="small" text type="primary" @click="load">刷新</el-button>
    </div>

    <!-- 汇总卡片 -->
    <el-row :gutter="12" class="cards">
      <el-col :span="4" v-for="c in cards" :key="c.label">
        <el-card shadow="never" class="stat-card">
          <div class="stat-value">{{ c.value }}<span v-if="c.unit" class="stat-unit">{{ c.unit }}</span></div>
          <div class="stat-label">{{ c.label }}</div>
        </el-card>
      </el-col>
    </el-row>

    <!-- 每日趋势（纯 CSS 条形，零图表依赖） -->
    <el-card shadow="never" class="section">
      <template #header>每日调用 / 失败 / Token</template>
      <div v-if="daily.length === 0" class="empty-hint">暂无数据</div>
      <div v-else class="bars">
        <div v-for="d in daily" :key="d.date" class="bar-col">
          <div class="bar-stack">
            <div class="bar bar-ok" :style="{ height: hPct(d.invocations) }" :title="`${d.date}：${d.invocations} 次调用`" />
            <div class="bar bar-fail" :style="{ height: hPct(d.failures) }" :title="`${d.date}：${d.failures} 次失败`" />
          </div>
          <div class="bar-label">{{ d.date.slice(5, 10) }}</div>
          <div class="bar-tokens">{{ d.tokens ? fmt(d.tokens) : '' }}</div>
        </div>
      </div>
    </el-card>

    <!-- 最近调用明细 -->
    <el-card shadow="never" class="section">
      <template #header>最近调用明细</template>
      <el-table :data="recent" size="small" stripe>
        <el-table-column prop="createdAt" label="时间" width="160">
          <template #default="{ row }">{{ fmtTime(row.createdAt) }}</template>
        </el-table-column>
        <el-table-column prop="userName" label="用户" width="110" show-overflow-tooltip />
        <el-table-column prop="model" label="模型" width="120" show-overflow-tooltip />
        <el-table-column prop="question" label="问题" min-width="200" show-overflow-tooltip />
        <el-table-column label="Token（入/出/总）" width="150">
          <template #default="{ row }">{{ row.inputTokens ?? '-' }} / {{ row.outputTokens ?? '-' }} / {{ row.totalTokens ?? '-' }}</template>
        </el-table-column>
        <el-table-column label="首字" width="80">
          <template #default="{ row }">{{ row.firstTokenMs != null ? row.firstTokenMs + 'ms' : '-' }}</template>
        </el-table-column>
        <el-table-column label="总耗时" width="90">
          <template #default="{ row }">{{ row.totalMs }}ms</template>
        </el-table-column>
        <el-table-column prop="toolCalls" label="工具" width="60" />
        <el-table-column prop="retrievedChunks" label="命中" width="60" />
        <el-table-column label="状态" width="80">
          <template #default="{ row }">
            <el-tag size="small" :type="row.success ? 'success' : 'danger'">{{ row.success ? '成功' : '失败' }}</el-tag>
          </template>
        </el-table-column>
        <el-table-column prop="errorMessage" label="错误" min-width="140" show-overflow-tooltip />
      </el-table>
    </el-card>
  </div>
</template>

<script setup>
import { computed, onMounted, ref } from 'vue'
import { api } from '../api'

const loading = ref(false)
const days = ref(30)
const summary = ref(null)
const daily = ref([])
const recent = ref([])

const fmt = (n) => (n >= 10000 ? (n / 1000).toFixed(1) + 'k' : n)
// createdAt 为 UTC，按本地时区展示（国内 +8），不直接截取 ISO 字符串
const fmtTime = (s) => (s ? new Date(s).toLocaleString('zh-CN', { hour12: false }) : '')

const cards = computed(() => {
  const s = summary.value
  return [
    { label: '调用次数', value: s?.totalInvocations ?? 0 },
    { label: '成功率', value: s ? s.successRate : 0, unit: '%' },
    { label: '总 Token', value: s?.totalTokens ? fmt(s.totalTokens) : 0 },
    { label: '平均首字', value: s?.avgFirstTokenMs ?? '-', unit: 'ms' },
    { label: '平均总耗时', value: s?.avgTotalMs ?? '-', unit: 'ms' },
    { label: '活跃用户', value: s?.distinctUsers ?? 0 }
  ]
})

const maxInv = computed(() => Math.max(1, ...daily.value.map((d) => d.invocations)))
const hPct = (n) => (n > 0 ? Math.max(4, Math.round((n / maxInv.value) * 100)) + '%' : '0%')

async function load() {
  loading.value = true
  try {
    const [s, d, r] = await Promise.all([
      api.usage.summary(days.value),
      api.usage.daily(days.value),
      api.usage.recent(50)
    ])
    // axios 响应拦截器已解包 resp.data，后端直接返回对象/数组，不能再取一次 .data
    summary.value = s || null
    daily.value = Array.isArray(d) ? d : []
    recent.value = Array.isArray(r) ? r : []
  } finally {
    loading.value = false
  }
}

onMounted(load)
</script>

<style scoped>
.usage-panel { max-width: 1200px; }
.panel-head { display: flex; align-items: center; gap: 16px; margin-bottom: 14px; }
.panel-head h3 { margin: 0; flex: 1; }
.cards { margin-bottom: 14px; }
.stat-card { text-align: center; }
.stat-value { font-size: 22px; font-weight: 700; color: #1f3a5f; }
.stat-unit { font-size: 12px; color: #909399; margin-left: 2px; }
.stat-label { font-size: 12px; color: #909399; margin-top: 4px; }
.section { margin-bottom: 14px; }
.empty-hint { color: #909399; text-align: center; padding: 24px 0; }
.bars { display: flex; align-items: flex-end; gap: 4px; height: 140px; overflow-x: auto; padding-top: 4px; }
.bar-col { min-width: 18px; flex: 1; display: flex; flex-direction: column; align-items: center; height: 100%; }
.bar-stack { flex: 1; width: 100%; display: flex; flex-direction: column; justify-content: flex-end; }
.bar { width: 100%; }
.bar-ok { background: #409eff; border-radius: 2px 2px 0 0; }
.bar-fail { background: #f56c6c; }
.bar-label { font-size: 10px; color: #909399; margin-top: 4px; white-space: nowrap; }
.bar-tokens { font-size: 9px; color: #c0c4cc; white-space: nowrap; }
</style>
