# Application logging

The editor's diagnostic log is written by `AppLogging.cs` (compiled into Compositor.App and
compile-linked into Compositor.App.Infrastructure.Tests (local development reference)
so the headless tests exercise the same implementation). `App.Log(string)` keeps its old shape:
it formats one record, parks it in a bounded ring, and returns. One background thread owns every
disk operation.

## Where the log lives

Default: `%LOCALAPPDATA%\Compositor\Logs\compositor-app-<pid>.log`, next to `recent.txt` and the
panel preferences the window already keeps. The executable directory is no longer involved, so an
install location that is not writable cannot silence the log, and the first `App.Log` call performs
no directory creation, open, flush, rotation or permission check.

Point a child process at an isolated directory by setting the variable in the launching shell — the
value is inherited by that child only, nothing is written to the machine or user environment:

    $env:COMPOSITOR_LOG_DIRECTORY = 'Windows/artifacts/qwen-task01-logs-single'
    .\Windows\tools\run-app-probe.ps1 -AppPath './Windows/artifacts/qwen-task01-debug-app/Compositor.App.exe' `
        -LogDirectory 'Windows/artifacts/qwen-task01-logs-single' -EvidencePrefix my-run -Argument '--logging-probe'

`tools/verify-port.ps1 -LaunchApp` sets the same variable inside its own smoke directory. Passing a
relative value resolves it against the current directory; a blank value falls back to the default.
`Windows/tools/run-app-probe.ps1` reads the file for one PID after that process exits, so a probe
run never inspects an older window's log.

A reader can open the file while the editor runs (`FileShare.Read`), which is how the slow-frame
series is inspected during a live session. The visible length can lag until the consumer's buffered
flush reaches the volume, so read the content rather than trusting a size check. Use `Flush()` on
the consumer, not flush-to-disk: a hard kill or power loss can still lose the tail.

## Limits

| Bound | Default | Effect |
| --- | --- | --- |
| Queue | 1024 records | The backlog lives in memory; worst case a few tens of MiB, in practice well under 1 MiB. |
| Record | 16 KiB UTF-8, formatted record included | Longer messages are cut on a character boundary and end with `…[truncated]`. |
| Current file | 2 MiB | Rotated before a write that would exceed it, so every file stays under the ceiling. |
| Retained files | current + 3 archives, per process | `compositor-app-<pid>.log`, `.log.1`, `.log.2`, `.log.3`; at most 8 MiB per session. |
| Superseded sessions | 4 in total including this one | Pruned once, on the consumer, at start-up. |
| Retry | 100 ms doubling to 10 s | A failing volume pauses writing instead of spinning. |
| Exit drain | 2 s total, then the process may leave | Bounded wait on the shutdown and crash paths only. |

## Order, drops and errors

Records reach the file in the order producers logged them. When the ring is full the logger drops an
ordinary diagnostic rather than waiting, and an incoming error evicts the oldest diagnostic so
slow-frame noise cannot crowd real failures out. Only a backlog made entirely of errors can lose an
error. Every drop increments a counter, and a coalesced `ERROR dropped N records since the last
notice (queue full)` line is written at most once every 10 seconds, so a script that greps the log
for unexpected exceptions fails loudly instead of reading a truncated log as a clean one.
`logging summary …` closes each session with the counters; `Windows/tools/run-app-probe.ps1` refuses
a run that lost records.

Classification happens in `AppLogging.Log(string)`: text containing `failed`, `Fatal`, `Unhandled`,
`missing resource`, `unreadable` or `unwritable` is an error, which covers the failure logs written
outside this file. `slow frame …` and `startup: …` stay diagnostics, and their line format is
unchanged so the existing band-size diagnosis method keeps working. A caller may pass
`LogLevel.Error` or `LogLevel.Diagnostic` explicitly.

## Two editors at once

Each process writes its own `<pid>` file, so starting a second window cannot truncate or rotate the
first one's output, and no cross-process lock enters the logging path. Cleanup only touches names
this implementation produces (`compositor-app-<digits>.log` plus its `.N` archives) — each candidate
is re-checked against that naming rule and must belong to the session being retired, so a user file
like `compositor-app-44444.log.backup-important.txt` beside the logs is never a delete target. A
session is retired only after its current file answers that nobody holds it open: an idle editor's
log stays open, and asking first is what keeps a running session's archives from being removed while
its current file survives. Sessions written within the last two minutes are left alone for the same
reason. Files with any other name in the log directory are never deleted.

## Failure behavior

A directory that cannot be created, a file held by another process, a read-only log file, a refused
rotation or a failed flush stay on the consumer thread: they are counted (`createFaults`,
`fileWriteFaults`, `fileFlushFaults`, `rotateFaults`, `pruneFaults`), the sink is closed and reopened
after the backoff, and the record that failed is put back at the head of the queue. Nothing re-enters
`App.Log`, and nothing surfaces to the canvas. A stop drains what the queue still holds and then
writes its summary line; `Shutdown` answers true only once the sink has actually been closed, not as
soon as the queue is empty, and the close is attempted even when the summary or the final flush
faults (each stage counts its own fault). The wait is bounded, so on a loaded machine the process may
still leave with records unwritten - that is the accepted cost of never blocking exit. Calls made
after the window has really closed are counted in `droppedLate`. A close the user cancels never
reaches the stop: `AppLogging.RequestShutdown()` runs from the window's `Closed` handler, after the
retirement work that may itself log. `args.Handled = true` in the global exception handlers is
unchanged — this page describes the recording mechanism, not exception recovery.
