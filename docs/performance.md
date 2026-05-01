# Macros Extension — Performance Baselines (v1.0)

## Overview

This document captures measured performance baselines for the seven hot paths that
run on every VS interaction. Baselines were established to support v1.0 ship criteria
and to give future contributors a concrete regression budget.

All numbers were measured with the xUnit-based micro-benchmarks in
`tests\Macros.Tests\Performance\PerformanceBenchmarks.cs` running on .NET Framework 4.8
inside a Debug build.

---

## Methodology

| Item | Detail |
|------|--------|
| **Framework** | xUnit [Fact] tests — no BenchmarkDotNet to keep the test project lightweight |
| **Measurement tool** | `System.Diagnostics.Stopwatch` |
| **Warm-up** | 10–100 iterations before the timed loop to prime JIT and CPU caches |
| **Iterations** | 1 000 – 10 000 per scenario (see table below) |
| **Reported metrics** | Mean µs/call *or* P50/P95 depending on variance |
| **Hardware fingerprint** | AMD/Intel x64 dev box, Windows 11, .NET Framework 4.8.9325 |
| **Build config** | Debug (JIT-optimised code paths are the same; no `[MethodImpl(NoInlining)]` guards needed) |

### How to run

```powershell
dotnet test tests\Macros.Tests --filter "FullyQualifiedName~Performance" `
    --logger "console;verbosity=detailed"
```

Each test prints a one-line summary to the xUnit output helper:

```
[1] CommandObserver.Exec (skipped)  N=10,000  mean=0.09 µs  target=<50 µs  threshold=<10 µs
```

---

## Measured Baselines

| # | Scenario | Iterations | Metric | **Measured** | Target SLA | CI Threshold |
|---|----------|-----------|--------|-------------|-----------|-------------|
| 1 | `CommandObserver.Exec` — noise/skip path | 10 000 | mean µs/call | **0.09 µs** | < 50 µs | < 10 µs |
| 2 | `CommandObserver.Exec` — Phase A recording capture | 1 000 | mean µs/call | **0.50 µs** | < 200 µs | < 10 µs |
| 3 | `RecordingSession.OnTextEdit` — idle (not capturing) | 10 000 | mean µs/call | **0.03 µs** | < 100 µs | < 5 µs |
| 4 | `MacroEventBus.Subscribe` — first-time lazy attach | 20 | P95 ms | **0.49 ms** | < 5 ms | < 5 ms |
| 5 | `MacroEventBus` — synchronous dispatch × 1 000 | 1 000 | total ms | **0.84 ms** | < 5 ms | < 10 ms |
| 6 | `MacroPlayer` — cold Roslyn compile (~100-char script) | 1 | ms | **138–172 ms** | < 500 ms | < 10 000 ms |
| 7 | `MacroPlayer` — cache-hit replay | 5 | P95 ms/play | **< 1 ms** | < 20 ms | < 1 000 ms |
| 8 | `RecordingSession` — 100 commands + drain | 1 | ms | **3–10 ms** | < 200 ms | < 500 ms |
| 9 | `MacrosToolWindowViewModel.LoadAsync` — 100 macros | 2 | ms | **1–3 ms** | < 500 ms | < 500 ms |

> **CI Threshold** — the value asserted in the xUnit test. Must pass on any reasonable CI agent.
> It is intentionally more generous than the Target SLA to absorb cold-JIT overhead and
> scheduler variance in shared CI environments.

---

## Scenario Notes

### 1. CommandObserver.Exec — skip path (0.09 µs)

The most critical hot path: invoked on **every** VS command, even idle pumping. The
`IsSkipped()` check is a `HashSet<(Guid, uint)>.Contains` call — O(1), allocation-free.
A regression here (e.g., accidental LINQ query or COM call) would be immediately visible.

### 2. CommandObserver.Exec — recording (0.50 µs)

Phase A: service-accessor delegate call → `CurrentSession.IsCapturing` → `RecordingSession.OnCommand`
(lock + `StepAggregator.Push`). DTE command-name resolution is excluded from this measurement
(cache hit = zero COM calls; cache miss handled asynchronously on warmup).

### 3. RecordingSession.OnTextEdit — idle (0.03 µs)

`TextEditObserver.OnTextBufferChanged` fires on every keystroke. When not recording, the
hot path is: `ReplayGuard.IsReplaying` (thread-static) → service-accessor → `IsCapturing`
(lock + bool). This test measures only the `OnTextEdit` delegate path after guards pass.

### 4 & 5. MacroEventBus.Subscribe / Dispatch

Subscribe lazily calls `EventInfo.AddEventHandler` via reflection — once per canonical name.
Subsequent subscriptions are `List<Action>.Add` only. Dispatch is a lock snapshot +
`foreach` over listeners — no allocation on the fast path.

### 6. MacroPlayer — Cold Compile (138–172 ms)

First-time Roslyn compilation of a small script. On a warm developer machine this is
138–172 ms. On a cold CI agent where Roslyn assemblies have not been JIT'd yet, it may
reach several seconds — hence the 10 s CI threshold. The 500 ms target SLA is the
developer-experience goal after the extension has been loaded once in a VS session.

### 7. MacroPlayer — Cache Hit (< 1 ms P95)

`ScriptCompilationCache.GetOrAdd` returns the pre-compiled `Script<object>` from an LRU
dictionary (SHA-256 keyed, no lock contention after the first hit). The sub-millisecond
measurement confirms that repeated macro plays are effectively free from the compilation
perspective. The 1 000 ms CI threshold accounts for JTF thread-switch overhead on
shared CI schedulers.

### 8. Recording 100 Commands + Drain (3–10 ms)

`RecordingSession.OnCommand` acquires `_gate`, calls `StepAggregator.Push`, and releases.
Draining via `StopRecordingAsync` performs one final `_aggregator.Flush()` pass.
100 commands is the typical upper end of a recorded macro; the budget is 200 ms.

### 9. ToolWindowViewModel.LoadAsync — 100 macros (1–3 ms)

`ListAllAsync` returns an in-memory list; the ViewModel partitions it into
`ObservableCollection<MacroGroupViewModel>` in a single pass. The 500 ms SLA is
dominated by the file-system enumeration in production (not measured here — this
benchmark uses a `FakeStorage`). The in-memory portion is trivially fast.

---

## Regression Response Playbook

If a benchmark **fails CI**:

1. **Identify the change**: `git bisect` against the failing threshold.
2. **Micro-scenarios (1–3)**: Any allocation (check with `dotnet-counters` `gen-0-gc-count`) or
   additional lock acquisition in the hot path is the likely culprit.
3. **Bus scenarios (4–5)**: Check for extra reflection, LINQ, or `Dictionary.TryGetValue` misses.
4. **Roslyn (6–7)**: A new assembly reference in `BuildScriptOptions()` forces recompile of the
   script runner — cache hit becomes cold again until the process is restarted.
5. **Recording / ToolWindow (8–9)**: Check for unbounded collection growth or synchronous I/O
   introduced in the recording session or storage layer.
6. **Adjust the threshold** only after verifying the new baseline is acceptable (document in this
   file and update the assertion constant in `PerformanceBenchmarks.cs`).

---

## Next Steps

- [ ] Add memory-allocation assertions (gen-0 GC count) for scenarios 1–3.
- [ ] Measure cold compile on a fresh CI image without NGen/R2R to tighten the 10 s threshold.
- [ ] Profile `TextEditObserver.OnTextBufferChanged` end-to-end (including WPF dispatcher
      overhead) in a hosted VS process.
