<template>
  <div class="admin-page">
    <el-container>
      <el-aside width="200px" class="menu">
        <div class="menu-brand" @click="$router.push('/chat')">← SmartKB</div>
        <el-menu :default-active="tab" @select="(t) => (tab = t)">
          <el-menu-item index="users">用户管理</el-menu-item>
          <el-menu-item index="roles">角色管理</el-menu-item>
          <el-menu-item index="kbs">知识库管理</el-menu-item>
        </el-menu>
      </el-aside>
      <el-main>
        <UsersPanel v-if="tab === 'users'" />
        <RolesPanel v-else-if="tab === 'roles'" />
        <KbPanel v-else />
      </el-main>
    </el-container>
  </div>
</template>

<script setup>
import { ref } from 'vue'
import UsersPanel from '../components/UsersPanel.vue'
import RolesPanel from '../components/RolesPanel.vue'
import KbPanel from '../components/KbPanel.vue'

const tab = ref('kbs')
</script>

<style scoped>
.admin-page { height: 100%; }
.menu { background: #fff; border-right: 1px solid #e4e7ed; }
.menu-brand { padding: 18px 20px; font-weight: 700; color: #1f3a5f; cursor: pointer; font-size: 16px; }
</style>
