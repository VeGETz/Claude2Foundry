import { render } from 'preact'
import { LocationProvider } from 'preact-iso'
import { App } from './App'
import '@picocss/pico'
import './styles/app.css'

render(
  <LocationProvider>
    <App />
  </LocationProvider>,
  document.getElementById('app')!,
)
