import { useRouter } from 'vue-router'
import { chatRoute, type ChatArea } from '../../shared/chatRoute'

export function useChatNavigation(area: ChatArea = 'general') {
  const router = useRouter()
  return (focus?: { entityType: string; entityId: string; entityVersion: number }) => router.push(chatRoute(area, focus))
}
