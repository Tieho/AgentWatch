# AgentWatch

A small Windows app (WPF, .NET 10) that shows what **Claude Code** and **OpenAI Codex** are running right now:
every shell command and subagent, foreground or background, with a live timer, an estimated progress bar,
and the latest output line.

![AgentWatch showing Claude Code and Codex sessions with running commands, the processes they are running, progress bars and an approval prompt](docs/screenshot.png)

## Requirements

- Windows 10 or 11
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Claude Code and/or OpenAI Codex, used on the same machine

## Build and run

```
git clone https://github.com/Tieho/AgentWatch.git
cd AgentWatch
dotnet build -c Release
src\AgentWatch\bin\Release\net10.0-windows\AgentWatch.exe
```

**Running now** groups everything by session, and each session has its own accent color (stripe and faint tint).
A working session gets an activity card showing what the agent is doing right now (thinking, writing,
reading or editing a file, running a command, using a subagent or MCP tool), how long it has been working,
and its last few steps. Below that card are the session's running commands with timers and progress.
A session that is idle but still has background jobs shows as **waiting** in the Sessions list.

Each running command also shows a **Running process** block: the programs actually executing under the
agent's shell right now (e.g. `dotnet build …` inside a long chained command), each with its CPU use, age and pid,
and which of its child processes are busy. Hover a line for the full command line and process chain.
- Git Bash pipelines and `( … &)` subshells are followed through Git Bash's own process table, since they lose
  their Windows parent link.
- **DETACHED**: a program the command started whose launcher has exited.
- **RELATED**: an already-running program the command drives, when the command names it and it works in the same
  project folder (e.g. the Unity editor behind `unity command …`).
- While the shell itself has no child process, the block says what it ran last and when that ended.

When an agent is blocked on you (a permission prompt, a question, plan approval, a dialog, or a Codex approval
request), its card gets a red **✋ Needs your input** banner with how long it has waited, its Sessions dot turns red,
the window title and summary show ✋, and a tray notification appears.

Closing the window hides it to the tray. Use the tray icon's **Exit** to quit. Tray notifications show when
a background task, or any task longer than 15 s, finishes.

## Where the data comes from (read-only)

| Source | What is read |
|---|---|
| `~/.claude/sessions/<pid>.json` | Live Claude Code sessions (name, cwd, busy/idle) |
| `~/.claude/projects/*/<session>.jsonl` (+ `subagents/`) | Tool calls, background task IDs, `.output` files, completion notices |
| `~/.codex/state_5.sqlite` | Codex threads (name, cwd, rollout file) |
| `~/.codex/sessions/**/rollout-*.jsonl` | In-flight Codex commands while a thread is active |
| `~/.codex/thread_history_1.sqlite` | Finished Codex commands, busy turns |
| Windows process list (+ Git Bash's `ps`) | Programs running under each agent's shell, their command lines and CPU use |

## Progress estimates

- If a background task's output contains `NN%` or `[n/m]`, that is used directly ("from output").
- Otherwise the app uses the median of past durations of the same command, matched on the exact normalized
  command, then the task description, then a coarse key such as `dotnet build` or `unity -executeMethod X`.
- On first start it learns from all existing transcripts and Codex history (a few seconds). After that it records
  each command it sees finish. History is stored in `%LOCALAPPDATA%\AgentWatch\history.json`, and settings
  in `settings.json` in the same folder.

`AgentWatch.exe --dump` writes what is currently detected to `%LOCALAPPDATA%\AgentWatch\dump.txt`
(for debugging).

## Privacy

AgentWatch only reads the local files and process information listed above. It never writes to them, and it makes no network
connections. Everything it stores stays in `%LOCALAPPDATA%\AgentWatch`.

## License

[MIT](LICENSE)
