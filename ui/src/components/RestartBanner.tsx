import { adminFetch } from '../api/client'
import type { RestartResponse } from '../api/contracts'

interface Props {
  fields: string[]
  onDismiss: () => void
}

export function RestartBanner({ fields, onDismiss }: Props) {
  const handleRestart = async () => {
    try {
      await adminFetch<RestartResponse>('/restart', { method: 'POST' })
    } catch {
      // connection drop after restart is expected
    }
    onDismiss()
  }

  return (
    <div class="restart-banner" role="alert">
      <span>
        Bootstrap field(s) changed ({fields.join(', ')}). Adapter restart required.
      </span>
      <div class="restart-banner-actions">
        <button onClick={handleRestart}>Restart adapter</button>
        <button class="outline" onClick={onDismiss}>I'll restart manually</button>
      </div>
    </div>
  )
}
