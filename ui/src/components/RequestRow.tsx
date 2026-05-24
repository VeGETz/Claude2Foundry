import type { MonitorRow } from '../api/contracts'

interface Props {
  record: MonitorRow
  onClick: () => void
}

const STATUS_CLASS: Record<string, string> = {
  ok: 'pico-color-green',
  error: 'pico-color-red',
  running: 'pico-color-orange',
}

export function RequestRow({ record, onClick }: Props) {
  const shortId = record.id.slice(0, 8)
  const time = new Date(record.ts).toLocaleTimeString()
  return (
    <tr onClick={onClick} style="cursor:pointer">
      <td>{time}</td>
      <td><code title={record.id}>{shortId}</code></td>
      <td>{record.originalModel ?? record.model} → {record.mappedModel ?? '…'}</td>
      <td><span class={STATUS_CLASS[record.status] ?? ''}>{record.status}</span></td>
      <td>{record.latencyMs !== null ? `${record.latencyMs}ms` : '…'}</td>
      <td>{record.promptTokens ?? '–'}</td>
      <td>{record.completionTokens ?? '–'}</td>
      <td class="pico-color-red">{record.error ?? ''}</td>
    </tr>
  )
}
