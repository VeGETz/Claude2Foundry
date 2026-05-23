import type { RequestSummaryFull } from '../api/contracts'

interface Props {
  record: RequestSummaryFull
  onClick: () => void
}

const STATUS_CLASS: Record<string, string> = {
  ok: 'pico-color-green',
  error: 'pico-color-red',
  streaming: 'pico-color-orange',
  complete: 'pico-color-green',
}

export function RequestRow({ record, onClick }: Props) {
  const shortId = record.id.slice(0, 8)
  const time = new Date(record.ts).toLocaleTimeString()
  const status = record.status === 'complete' ? 'ok' : record.status
  return (
    <tr onClick={onClick} style="cursor:pointer">
      <td>{time}</td>
      <td><code title={record.id}>{shortId}</code></td>
      <td>{record.originalModel} → {record.resolvedModel ?? '…'}</td>
      <td><span class={STATUS_CLASS[status] ?? ''}>{status}</span></td>
      <td>{record.elapsedMs !== null ? `${record.elapsedMs}ms` : '…'}</td>
      <td>{record.usage?.input ?? '–'}</td>
      <td>{record.usage?.output ?? '–'}</td>
      <td class="pico-color-red">{record.error ?? ''}</td>
    </tr>
  )
}
