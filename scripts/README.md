# Wrapper scripts

`c2f.sh` (POSIX) and `c2f.cmd` (Windows) wrap the Claude2Foundry binary in an exit-75 restart loop.

## When to use

Use the wrapper if you want one-click restart from the Adapter Console UI. When you click **Restart adapter** in the UI, the adapter exits with code 75 and the wrapper relaunches it automatically, reconnecting the browser within 60 s.

If you prefer to restart manually (kill and re-run), you don't need the wrapper.

## Usage

```sh
# Linux / macOS
chmod +x scripts/c2f.sh
./scripts/c2f.sh

# Windows
scripts\c2f.cmd
```

## Binary in another path

```sh
C2F_BIN=/opt/c2f/Claude2Foundry ./scripts/c2f.sh
```

```cmd
set C2F_BIN=C:\tools\Claude2Foundry.exe
scripts\c2f.cmd
```

## How it works

The wrapper sets `C2F_WRAPPER=1` in the environment before launching the binary. The adapter checks this env var on startup; the Console's Restart button is only enabled when `C2F_WRAPPER` is present. Any exit code other than 75 ends the loop and propagates to the parent shell.
