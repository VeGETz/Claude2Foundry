import { describe, it, expect, vi, beforeEach } from 'vitest'
import { render, screen, fireEvent, waitFor } from '@testing-library/preact'
import { RequestDrawer } from '../components/RequestDrawer'
import type { RequestSummaryFull } from '../api/contracts'

// Mock adminFetch
vi.mock('../api/client', () => ({
  adminFetch: vi.fn(),
}))

import { adminFetch } from '../api/client'
const mockFetch = vi.mocked(adminFetch)

const record: RequestSummaryFull = {
  id: 'test-id-123',
  ts: '2026-05-23T10:00:00Z',
  originalModel: 'claude-opus-4-7',
  resolvedModel: 'DeepSeek-V4-Pro',
  status: 'complete',
  elapsedMs: 1234,
  usage: { input: 100, output: 50 },
  error: null,
  phase: 'complete',
  stream: false,
}

describe('RequestDrawer', () => {
  beforeEach(() => {
    vi.clearAllMocks()
  })

  it('shows Load full bodies button by default (no auto-fetch)', () => {
    render(
      <RequestDrawer
        record={record}
        anthropicAssembledCache={null}
        onClose={() => {}}
      />
    )
    expect(screen.getByText('Load full bodies')).toBeTruthy()
    expect(mockFetch).not.toHaveBeenCalled()
  })

  it('fetches bodies only after button click', async () => {
    mockFetch.mockResolvedValueOnce({
      expired: false,
      id: 'test-id-123',
      phase: 'complete',
      anthropicBody: { type: 'message' },
      openaiBody: null,
      responseBody: null,
      anthropicAssembled: null,
      headers: {},
    })

    render(
      <RequestDrawer
        record={record}
        anthropicAssembledCache={null}
        onClose={() => {}}
      />
    )

    fireEvent.click(screen.getByText('Load full bodies'))
    expect(mockFetch).toHaveBeenCalledOnce()
    await waitFor(() => expect(screen.queryByText('loading…')).toBeFalsy())
  })

  it('shows Bodies expired message when expired: true', async () => {
    mockFetch.mockResolvedValueOnce({ expired: true })

    render(
      <RequestDrawer
        record={record}
        anthropicAssembledCache={null}
        onClose={() => {}}
      />
    )

    fireEvent.click(screen.getByText('Load full bodies'))
    await waitFor(() => expect(screen.getByText('Bodies expired')).toBeTruthy())
  })

  it('renders anthropicAssembled from SSE cache without loading bodies', () => {
    const assembled = { type: 'message', content: [{ type: 'text', text: 'hi' }] }
    render(
      <RequestDrawer
        record={record}
        anthropicAssembledCache={assembled}
        onClose={() => {}}
      />
    )
    // Navigate to anthropic-resp tab
    fireEvent.click(screen.getByText('anthropic-resp'))
    expect(screen.getByText(/\"type\": \"message\"/)).toBeTruthy()
    // No fetch should have happened
    expect(mockFetch).not.toHaveBeenCalled()
  })
})
