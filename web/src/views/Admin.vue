<template>
  <div class="admin-page">
    <el-container>
      <el-aside width="200px" class="menu">
        <div class="menu-brand" @click="$router.push('/chat')">← SmartKB</div>
        <el-menu :default-active="tab" @select="(t) => (tab = t)">
          <el-menu-item index="users">用户管理</el-menu-item>
          <el-menu-item index="roles">角色管理</el-menu-item>
          <el-menu-item index="kbs">知识库管理</el-menu-item>
          <el-menu-item v-if="auth.isAdmin" index="tools">工具注册表</el-menu-item>
          <el-menu-item v-if="auth.isAdmin" index="usage">AI 观测</el-menu-item>
          <el-menu-item v-if="auth.isAdmin" index="settings">RAG 配置</el-menu-item>
          <el-menu-item v-if="auth.isAdmin" index="audit">审计日志</el-menu-item>
        </el-menu>
      </el-aside>
      <el-main>
        <UsersPanel v-if="tab === 'users'" />
        <RolesPanel v-else-if="tab === 'roles'" />
        <ToolsPanel v-else-if="tab === 'tools' && auth.isAdmin" />
        <UsagePanel v-else-if="tab === 'usage' && auth.isAdmin" />
        <SettingsPanel v-else-if="tab === 'settings' && auth.isAdmin" />
        <AuditPanel v-else-if="tab === 'audit' && auth.isAdmin" />
        <KbPanel v-else />
      </el-main>
    </el-container>
  </div>
</template>

<script setup>
import { ref } from 'vue'
import { useAuthStore } from '../stores/auth'
import UsersPanel from '../components/UsersPanel.vue'
import RolesPanel from '../components/RolesPanel.vue'
import KbPanel from '../components/KbPanel.vue'
import ToolsPanel from '../components/ToolsPanel.vue'
import UsagePanel from '../components/UsagePanel.vue'
import SettingsPanel from '../components/SettingsPanel.vue'
import AuditPanel from '../components/AuditPanel.vue'

const auth = useAuthStore()
const tab = ref('kbs')
</script>

<style scoped>
.admin-page { height: 100%; }
.menu { background: #fff; border-right: 1px solid #e4e7ed; }
.menu-brand { padding: 18px 20px; font-weight: 700; color: #1f3a5f; cursor: pointer; font-size: 16px; }
</style>
