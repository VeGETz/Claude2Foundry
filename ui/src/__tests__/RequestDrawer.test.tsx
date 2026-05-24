import { describe, it, expect, vi, beforeEach } from 'vitest'
import { render, screen, waitFor } from '@testing-library/preact'
import { RequestDrawer } from '../components/RequestDrawer'
import type { MonitorRow } from '../api/contracts'

vi.mock('../api/client', () => ({
  adminFetch: vi.fn(),
}))

import { adminFetch } from '../api/client'
const mockFetch = vi.mocked(adminFetch)

const record: MonitorRow = {
  id: 'test-id-123',
  ts: '2026-05-23T10:00:00Z',
  model: 'claude-opus-4-7',
  originalModel: 'claude-opus-4-7',
  mappedModel: 'DeepSeek-V4-Pro',
  status: 'ok',
  latencyMs: 1234,
  promptTokens: 100,
  completionTokens: 50,
  error: null,
}

const runningRecord: MonitorRow = { ...record, status: 'running', latencyMs: null }

describe('RequestDrawer', () => {
  beforeEach(() => {
    vi.clearAllMocks()
  })

  it('auto-fetches detail on mount for completed requests', async () => {
    mockFetch.mockResolvedValueOnce({
      id: 'test-id-123',
      ts: '2026-05-23T10:00:00Z',
      model: 'claude-opus-4-7',
      status: 'ok',
      latencyMs: 1234,
      anthropicBody: { type: 'message' },
      openaiBody: null,
      openaiResponse: null,
      anthropicResponse: null,
      headers: null,
      error: null,
    })

    render(<RequestDrawer record={record} onClose={() => {}} />)
    expect(mockFetch).toHaveBeenCalledOnce()
    await waitFor(() => expect(screen.queryByText('loading…')).toBeFalsy())
  })

  it('does not fetch for in-progress requests', () => {
    render(<RequestDrawer record={runningRecord} onClose={() => {}} />)
    expect(mockFetch).not.toHaveBeenCalled()
  })

  it('shows in-progress message for running requests', () => {
    render(<RequestDrawer record={runningRecord} onClose={() => {}} />)
    expect(screen.getByText(/in progress/)).toBeTruthy()
  })

  it('renders anthropicResponse from detail', async () => {
    mockFetch.mockResolvedValueOnce({
      id: 'test-id-123',
      ts: '2026-05-23T10:00:00Z',
      model: 'claude-opus-4-7',
      status: 'ok',
      latencyMs: 1234,
      anthropicBody: null,
      openaiBody: null,
      openaiResponse: null,
      anthropicResponse: { type: 'message', content: [{ type: 'text', text: 'hi' }] },
      headers: null,
      error: null,
    })

    const { container } = render(<RequestDrawer record={record} onClose={() => {}} />)
    // navigate to anthropic-resp tab
    const tabs = container.querySelectorAll('nav a')
    const respTab = Array.from(tabs).find(t => t.textContent === 'anthropic-resp')
    if (respTab) (respTab as HTMLElement).click()
    await waitFor(() => expect(screen.getByText(/"type": "message"/)).toBeTruthy())
  })
})
