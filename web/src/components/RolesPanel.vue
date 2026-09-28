<template>
  <el-card>
    <div class="toolbar">
      <h3>角色管理</h3>
      <el-button type="primary" @click="create">新增角色</el-button>
    </div>

    <el-table :data="roles" border>
      <el-table-column prop="id" label="ID" width="70" />
      <el-table-column prop="name" label="角色名" width="160" />
      <el-table-column prop="description" label="说明" />
      <el-table-column label="内置" width="80">
        <template #default="{ row }">
          <el-tag v-if="row.isSystem" size="small" type="warning">内置</el-tag>
        </template>
      </el-table-column>
      <el-table-column label="操作" width="100">
        <template #default="{ row }">
          <el-button size="small" type="danger" :disabled="row.isSystem" @click="remove(row)">删除</el-button>
        </template>
      </el-table-column>
    </el-table>
  </el-card>
</template>

<script setup>
import { onMounted, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { api } from '../api'

const roles = ref([])

async function load() { roles.value = await api.roles.list() }

async function create() {
  const { value: name } = await ElMessageBox.prompt('角色名：', '新增角色')
  await api.roles.create({ name, description: null })
  ElMessage.success('已创建')
  load()
}

async function remove(row) {
  await ElMessageBox.confirm(`删除角色「${row.name}」？`, '确认', { type: 'warning' })
  await api.roles.remove(row.id)
  ElMessage.success('已删除')
  load()
}

onMounted(load)
</script>

<style scoped>
.toolbar { display: flex; justify-content: space-between; align-items: center; margin-bottom: 12px; }
.toolbar h3 { margin: 0; }
</style>
