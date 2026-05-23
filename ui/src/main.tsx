import { render } from 'preact'
import { App } from './App'
import '@picocss/pico'
import './styles/app.css'

render(<App />, document.getElementById('app')!)
