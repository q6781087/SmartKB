<template>
  <el-card>
    <div class="toolbar">
      <h3>用户管理</h3>
      <el-button type="primary" @click="openEdit()">新增用户</el-button>
    </div>

    <el-table :data="users" border>
      <el-table-column prop="username" label="用户名" width="140" />
      <el-table-column prop="displayName" label="姓名" width="140" />
      <el-table-column prop="department" label="部门" width="140" />
      <el-table-column label="角色">
        <template #default="{ row }">
          <el-tag v-for="r in row.roles" :key="r.id" size="small" style="margin-right:4px">{{ r.name }}</el-tag>
          <el-tag v-if="row.isAdmin" type="danger" size="small">管理员</el-tag>
        </template>
      </el-table-column>
      <el-table-column label="状态" width="80">
        <template #default="{ row }">
          <el-tag :type="row.isActive ? 'success' : 'info'" size="small">
            {{ row.isActive ? '启用' : '停用' }}
          </el-tag>
        </template>
      </el-table-column>
      <el-table-column label="操作" width="240">
        <template #default="{ row }">
          <el-button size="small" @click="openEdit(row)">编辑</el-button>
          <el-button size="small" @click="resetPwd(row)">重置密码</el-button>
          <el-button size="small" type="danger" :disabled="row.isAdmin" @click="remove(row)">删除</el-button>
        </template>
      </el-table-column>
    </el-table>

    <!-- 编辑弹窗 -->
    <el-dialog v-model="dialog" :title="form.id ? '编辑用户' : '新增用户'" width="480">
      <el-form :model="form" label-width="90px">
        <el-form-item label="用户名" required>
          <el-input v-model="form.username" :disabled="!!form.id" />
        </el-form-item>
        <el-form-item v-if="!form.id" label="初始密码" required>
          <el-input v-model="form.password" type="password" show-password />
        </el-form-item>
        <el-form-item label="姓名" required>
          <el-input v-model="form.displayName" />
        </el-form-item>
        <el-form-item label="部门">
          <el-input v-model="form.department" />
        </el-form-item>
        <el-form-item label="管理员">
          <el-switch v-model="form.isAdmin" />
        </el-form-item>
        <el-form-item label="角色">
          <el-select v-model="form.roleIds" multiple style="width:100%">
            <el-option v-for="r in roles" :key="r.id" :value="r.id" :label="r.name" />
          </el-select>
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="dialog = false">取消</el-button>
        <el-button type="primary" @click="save">保存</el-button>
      </template>
    </el-dialog>
  </el-card>
</template>

<script setup>
import { onMounted, reactive, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { api } from '../api'

const users = ref([])
const roles = ref([])
const dialog = ref(false)
const form = reactive({ id: null, username: '', password: '', displayName: '', department: '', isAdmin: false, roleIds: [] })

async function load() {
  // /api/users 返回分页对象 { items, total, page, pageSize }，兼容裸数组
  const res = await api.users.list()
  users.value = Array.isArray(res) ? res : (res.items ?? [])
  roles.value = await api.roles.list()
}

function openEdit(row) {
  Object.assign(form, row
    ? { id: row.id, username: row.username, password: '', displayName: row.displayName,
        department: row.department, isAdmin: row.isAdmin, roleIds: (row.roles || []).map((r) => r.id) }
    : { id: null, username: '', password: '', displayName: '', department: '', isAdmin: false, roleIds: [] })
  dialog.value = true
}

async function save() {
  try {
    if (form.id) {
      await api.users.update(form.id, {
        displayName: form.displayName, department: form.department || null,
        isActive: true, isAdmin: form.isAdmin, roleIds: form.roleIds
      })
    } else {
      await api.users.create({
        username: form.username, password: form.password, displayName: form.displayName,
        department: form.department || null, isAdmin: form.isAdmin, roleIds: form.roleIds
      })
    }
    ElMessage.success('已保存')
    dialog.value = false
    load()
  } catch (e) { ElMessage.error(e.message) }
}

async function resetPwd(row) {
  const { value } = await ElMessageBox.prompt(`为「${row.displayName}」设置新密码：`, '重置密码', { inputType: 'password' })
  await api.users.resetPassword(row.id, { newPassword: value })
  ElMessage.success('密码已重置')
}

async function remove(row) {
  await ElMessageBox.confirm(`删除用户「${row.displayName}」？`, '确认', { type: 'warning' })
  await api.users.remove(row.id)
  ElMessage.success('已删除')
  load()
}

onMounted(load)
</script>

<style scoped>
.toolbar { display: flex; justify-content: space-between; align-items: center; margin-bottom: 12px; }
.toolbar h3 { margin: 0; }
</style>
