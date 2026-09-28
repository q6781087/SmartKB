<template>
  <el-container class="chat-page">
    <!-- 顶栏 -->
    <el-header class="topbar">
      <div class="brand">SmartKB · 企业知识库智能问答</div>
      <div class="topbar-right">
        <el-select v-model="selectedKbIds" multiple collapse-tags placeholder="全部有权限的知识库"
                   style="width: 300px" size="default">
          <el-option v-for="kb in kbs" :key="kb.id" :value="kb.id" :label="kb.name" />
        </el-select>
        <el-button v-if="auth.canManage" @click="$router.push('/admin')">管理后台</el-button>
        <span class="user-name">{{ auth.user?.displayName }}</span>
        <el-button link type="danger" @click="logout">退出</el-button>
      </div>
    </el-header>

    <el-container>
      <!-- 会话列表 -->
      <el-aside width="240px" class="sessions">
        <el-button type="primary" style="width:100%" @click="newSession">＋ 新会话</el-button>
        <div class="session-list">
          <div v-for="s in sessions" :key="s.id"
               class="session-item" :class="{ active: s.id === currentSessionId }"
               @click="openSession(s.id)">
            <span class="session-title">{{ s.title }}</span>
            <el-icon class="session-del" @click.stop="removeSession(s)"><Delete /></el-icon>
          </div>
        </div>
      </el-aside>

      <!-- 消息区 -->
      <el-main class="chat-main">
        <div ref="msgBox" class="msg-box">
          <el-empty v-if="messages.length === 0" description="向企业知识库提问，例如：XX合同的付款条件是什么？" />
          <div v-for="m in messages" :key="m.id" class="msg-row" :class="m.role === 1 ? 'user' : 'ai'">
            <div class="avatar">{{ m.role === 1 ? '我' : 'AI' }}</div>
            <div class="bubble">
              <!-- 用户消息纯文本；AI 消息 markdown 渲染 -->
              <div v-if="m.role === 1" class="plain">{{ m.content }}</div>
              <div v-else class="md" v-html="renderMarkdown(m.content)"></div>

              <!-- 流式生成中的光标 -->
              <span v-if="m.streaming" class="cursor">▌</span>

              <!-- 引用出处卡片 -->
              <div v-if="m.citations?.length" class="citations">
                <div class="citations-title">引用来源（{{ m.citations.length }}）</div>
                <el-popover v-for="c in m.citations" :key="c.chunkId" placement="top" :width="420">
                  <template #reference>
                    <div class="citation-chip">
                      📄 {{ c.fileName }} · 第{{ c.seq + 1 }}段
                    </div>
                  </template>
                  <div class="citation-detail">{{ c.excerpt }}</div>
                </el-popover>
              </div>
            </div>
          </div>
        </div>

        <!-- 输入区 -->
        <div class="input-area">
          <el-input v-model="question" type="textarea" :rows="2" resize="none"
                    placeholder="输入问题，Enter 发送，Shift+Enter 换行"
                    :disabled="streaming" @keydown.enter.exact.prevent="ask" />
          <el-button type="primary" :loading="streaming" :disabled="!question.trim()"
                     @click="ask">发送</el-button>
        </div>
      </el-main>
    </el-container>
  </el-container>
</template>

<script setup>
import { nextTick, onBeforeUnmount, onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import { ElMessage, ElMessageBox } from 'element-plus'
import { Delete } from '@element-plus/icons-vue'
import * as signalR from '@microsoft/signalr'
import { api } from '../api'
import { useAuthStore } from '../stores/auth'
import { renderMarkdown } from '../lib/markdown'

const router = useRouter()
const auth = useAuthStore()

const kbs = ref([])
const selectedKbIds = ref([])
const sessions = ref([])
const currentSessionId = ref(null)
const messages = ref([])
const question = ref('')
const streaming = ref(false)
const msgBox = ref(null)

let hub = null

// ---------- SignalR ----------
function buildHub() {
  hub = new signalR.HubConnectionBuilder()
    .withUrl('/hubs/chat', { accessTokenFactory: () => auth.token })
    .withAutomaticReconnect()
    .build()

  // 打字机增量
  hub.on('token', (text) => {
    const last = messages.value[messages.value.length - 1]
    if (last?.streaming) {
      last.content += text
      scrollToBottom()
    }
  })

  // 引用出处
  hub.on('citations', (citations) => {
    const last = messages.value[messages.value.length - 1]
    if (last?.streaming) last.citations = citations
  })

  // 完成：落库确认 + 光标消失
  hub.on('done', ({ sessionId }) => {
    const last = messages.value[messages.value.length - 1]
    if (last?.streaming) last.streaming = false
    if (!currentSessionId.value && sessionId) {
      currentSessionId.value = sessionId
      loadSessions()
    }
    streaming.value = false
  })

  hub.on('error', (message) => {
    const last = messages.value[messages.value.length - 1]
    if (last?.streaming) {
      last.streaming = false
      if (!last.content) last.content = `⚠️ ${message}`
    }
    streaming.value = false
    ElMessage.error(message)
  })

  hub.start().catch((e) => ElMessage.error(`SignalR 连接失败：${e.message}`))
}

function scrollToBottom() {
  nextTick(() => {
    if (msgBox.value) msgBox.value.scrollTop = msgBox.value.scrollHeight
  })
}

// ---------- 会话 ----------
async function loadSessions() {
  sessions.value = await api.chats.list()
}

async function newSession() {
  currentSessionId.value = null
  messages.value = []
}

async function openSession(id) {
  currentSessionId.value = id
  const list = await api.chats.messages(id)
  messages.value = list.map((m) => ({
    id: m.id,
    role: m.role,
    content: m.content,
    citations: m.citationsJson ? JSON.parse(m.citationsJson) : null
  }))
  scrollToBottom()
}

async function removeSession(s) {
  await ElMessageBox.confirm(`删除会话「${s.title}」？`, '确认', { type: 'warning' })
  await api.chats.remove(s.id)
  if (currentSessionId.value === s.id) newSession()
  loadSessions()
}

// ---------- 提问 ----------
async function ask() {
  const q = question.value.trim()
  if (!q || streaming.value) return

  // 本地先渲染用户消息 + AI 占位（打字机挂到占位消息上）
  messages.value.push({ id: `u-${Date.now()}`, role: 1, content: q })
  messages.value.push({ id: `a-${Date.now()}`, role: 2, content: '', streaming: true, citations: null })
  question.value = ''
  streaming.value = true
  scrollToBottom()

  try {
    await hub.invoke('Ask', currentSessionId.value, selectedKbIds.value, q)
  } catch (e) {
    streaming.value = false
    const last = messages.value[messages.value.length - 1]
    if (last?.streaming) last.streaming = false
    ElMessage.error(e.message || '发送失败，请检查连接')
  }
}

function logout() {
  auth.logout()
  router.push('/login')
}

onMounted(async () => {
  buildHub()
  kbs.value = await api.kbs.list()
  loadSessions()
})

onBeforeUnmount(() => {
  if (hub) hub.stop()
})
</script>

<style scoped>
.chat-page { height: 100%; }
.topbar {
  display: flex; align-items: center; justify-content: space-between;
  background: #1f3a5f; color: #fff; height: 56px;
}
.brand { font-size: 17px; font-weight: 600; }
.topbar-right { display: flex; align-items: center; gap: 12px; }
.user-name { color: #d7e3f4; }

.sessions { background: #fff; border-right: 1px solid #e4e7ed; padding: 12px; display: flex; flex-direction: column; }
.session-list { margin-top: 12px; overflow-y: auto; flex: 1; }
.session-item {
  display: flex; align-items: center; justify-content: space-between;
  padding: 10px 12px; border-radius: 6px; cursor: pointer; margin-bottom: 4px;
  color: #333; font-size: 14px;
}
.session-item:hover { background: #f0f4fa; }
.session-item.active { background: #e6f0ff; color: #1f6feb; font-weight: 600; }
.session-title { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.session-del { color: #bbb; flex-shrink: 0; }
.session-del:hover { color: #f56c6c; }

.chat-main { display: flex; flex-direction: column; padding: 0; }
.msg-box { flex: 1; overflow-y: auto; padding: 20px 8%; }

.msg-row { display: flex; gap: 10px; margin-bottom: 18px; }
.msg-row.user { flex-direction: row-reverse; }
.avatar {
  width: 36px; height: 36px; border-radius: 50%; flex-shrink: 0;
  display: flex; align-items: center; justify-content: center;
  font-size: 13px; color: #fff; background: #9098a8;
}
.msg-row.ai .avatar { background: #1f6feb; }
.bubble { max-width: 76%; border-radius: 10px; padding: 10px 14px; background: #fff; box-shadow: 0 1px 2px rgba(0,0,0,.06); }
.msg-row.user .bubble { background: #1f6feb; color: #fff; }
.plain { white-space: pre-wrap; font-size: 14px; }

.md { font-size: 14px; line-height: 1.7; }
.md :deep(table) { border-collapse: collapse; margin: 8px 0; }
.md :deep(th), .md :deep(td) { border: 1px solid #dcdfe6; padding: 6px 10px; font-size: 13px; }
.md :deep(pre) { background: #f5f7fa; padding: 10px; border-radius: 6px; overflow-x: auto; }
.md :deep(code) { font-size: 13px; }
.md :deep(p) { margin: 6px 0; }

.cursor { color: #1f6feb; animation: blink 1s infinite; }
@keyframes blink { 50% { opacity: 0; } }

.citations { margin-top: 10px; border-top: 1px dashed #e4e7ed; padding-top: 8px; }
.citations-title { font-size: 12px; color: #909399; margin-bottom: 6px; }
.citation-chip {
  display: inline-block; margin: 0 6px 6px 0; padding: 3px 10px;
  background: #f0f6ff; color: #1f6feb; border-radius: 12px;
  font-size: 12px; cursor: pointer;
}
.citation-detail { font-size: 13px; color: #555; line-height: 1.7; max-height: 300px; overflow-y: auto; }

.input-area { display: flex; gap: 10px; padding: 14px 8%; background: #fff; border-top: 1px solid #e4e7ed; align-items: flex-end; }
.input-area .el-button { height: 54px; }
</style>
