import { createRouter, createWebHistory } from "vue-router"
import SoundHomeView from "@/views/SoundHomeView.vue"
import RoomView from "@/views/RoomView.vue"

const router = createRouter({
  history: createWebHistory(),
  routes: [
    {
      path: "/",
      redirect: "/sound"
    },
    {
      path: "/sound",
      name: "sound-home",
      component: SoundHomeView
    },
    {
      path: "/sound/:roomId",
      name: "sound-room",
      component: RoomView,
      props: true
    }
  ]
})

export default router
