<template>
  <div class="app-shell">
    
    <Sidebar v-if="$route.name !== 'Login'" />

    <main :class="['main-container', { 'full-screen': $route.name === 'Login' }]">
      
      <header class="top-bar" v-if="$route.name !== 'Login'">
        <h1 class="page-title">{{ currentPageTitle }}</h1>
        
        <div class="user-info" @click="handleAuthAction" :title="isLoggedIn ? '點擊登出' : '點擊登入'">
          <template v-if="isLoggedIn">
            <span class="user-name">{{ currentUser.name }}</span>
            <span class="role-text">({{ displayRole }})</span>
            <span class="logout-icon" style="font-size: 0.8rem; margin-left: 8px;">⏻</span>
          </template>
          
          <template v-else>
            <span class="login-prompt">請登入 / Guest LOGIN 🔑</span>
          </template>
        </div>
      </header>

      <section class="page-content">
        <router-view v-slot="{ Component }">
          <transition name="page-fade" mode="out-in">
            <component :is="Component" />
          </transition>
        </router-view>
      </section>
    </main>
  </div>
</template>

<script src="./App.js"></script>
<style src="./App.css"></style>