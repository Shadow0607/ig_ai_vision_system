<template>
  <div class="media-wrapper">
    <template v-if="isVideo">
      <video
        ref="videoRef"
        :src="streamUrl"
        controls
        muted
        playsinline
        preload="metadata"
        class="media-content"
        @mouseenter="playVideo"
        @mouseleave="pauseVideo"
      ></video>
    </template>

    <template v-else>
      <img
        :src="streamUrl"
        loading="lazy"
        class="media-content"
        alt="Safe Media"
      />
    </template>
  </div>
</template>

<script src="./script.js"></script>
<style src="./style.css" scoped></style>