<template>
  <aside
    class="sidebar"
    :class="{ collapsed: collapsed }"
    @click="handleClick"
  >
    <div class="brand">
      <div class="logo-icon">AI</div>
      <span class="brand-text">Vision System</span>
    </div>

    <nav class="menu">
  <router-link 
    v-for="item in filteredMenu" 
    :key="item.name"
    :to="item.path" 
    class="menu-item" 
    @click.stop
  >
    <i class="icon">{{ item.icon }}</i>
    <span class="menu-item-text">{{ item.text }}</span>
  </router-link>
</nav>
    <div class="sidebar-footer">
      <span class="version-text">v1.0.0 Stable</span>
    </div>
  </aside>
</template>
<script src="./script.js"></script>
<style src="./style.css" scoped></style>