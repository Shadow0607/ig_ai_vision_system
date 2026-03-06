// src/store/auth.ts (範例)
import { reactive } from 'vue';

export const authState = reactive({
  permissions: [] as any[],
  isLoaded: false
});