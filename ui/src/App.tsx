import { useState } from 'preact/hooks'
import { Router, Route } from 'preact-iso'
import { Config } from './pages/Config'
import { Monitor } from './pages/Monitor'
import { Health } from './pages/Health'
import { Test } from './pages/Test'
import { RestartBanner } from './components/RestartBanner'

export interface Toast {
  id: number
  message: string
  type: 'success' | 'error'
}

export interface AppState {
  restartRequired: boolean
  restartFields: string[]
  setRestartRequired: (fields: string[]) => void
  clearRestart: () => void
  addToast: (msg: string, type: Toast['type']) => void
}

import { createContext } from 'preact'
import { useContext } from 'preact/hooks'

export const AppCtx = createContext<AppState>({
  restartRequired: false,
  restartFields: [],
  setRestartRequired: () => {},
  clearRestart: () => {},
  addToast: () => {},
})

export const useApp = () => useContext(AppCtx)

let toastSeq = 0

export function App() {
  const [restartRequired, setRestart] = useState(false)
  const [restartFields, setRestartFields] = useState<string[]>([])
  const [toasts, setToasts] = useState<Toast[]>([])

  const addToast = (message: string, type: Toast['type']) => {
    const id = ++toastSeq
    setToasts(t => [...t, { id, message, type }])
    setTimeout(() => setToasts(t => t.filter(x => x.id !== id)), 4000)
  }

  const state: AppState = {
    restartRequired,
    restartFields,
    setRestartRequired: (fields) => { setRestart(true); setRestartFields(fields) },
    clearRestart: () => { setRestart(false); setRestartFields([]) },
    addToast,
  }

  return (
    <AppCtx.Provider value={state}>
      <nav class="app-nav">
        <ul>
          <li><strong>Adapter Console</strong></li>
        </ul>
        <ul>
          <li><a href="/">Config</a></li>
          <li><a href="/monitor">Monitor</a></li>
          <li><a href="/health">Health</a></li>
          <li><a href="/test">Test</a></li>
        </ul>
      </nav>

      {restartRequired && (
        <RestartBanner fields={restartFields} onDismiss={state.clearRestart} />
      )}

      <main class="container">
        <Router>
          <Route path="/" component={Config} />
          <Route path="/monitor" component={Monitor} />
          <Route path="/health" component={Health} />
          <Route path="/test" component={Test} />
        </Router>
      </main>

      <div class="toast-container" aria-live="polite">
        {toasts.map(t => (
          <div key={t.id} class={`toast toast-${t.type}`} role="alert">
            {t.message}
          </div>
        ))}
      </div>
    </AppCtx.Provider>
  )
}
