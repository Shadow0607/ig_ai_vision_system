/* src/views/NotFound/script.js */
// @ts-nocheck
import { onMounted } from 'vue';

export default {
    name: 'NotFound',
    setup() {
        onMounted(() => {

                console.warn('[System] 偵測到無效路由存取，已引導至 404 頁面');
            
        });

        return {};
    }
};