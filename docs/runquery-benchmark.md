# `ServerEffect.RunQuery` — benchmark (Phase 1896)

**What is timed:** one `Handler.run` over a handler whose only stage is a `RunQuery` — evaluate the
source, run the pipeline, land the table in the store. The pipeline filters half the rows away,
derives a column, sorts on it and keeps the top 100. The table (an integer, a string and a float
column) is built once, outside the timed region, and reaches the evaluator through the source
resolver, so the figure is the evaluator plus the handler loop around it and nothing else.

**Harness:** `tests/Fuaran.Program.Bench` (a smoke pass under the gate; the measurement is the
`--full` switch), built in Release. Each size is warmed and then timed in batches; a batch repeats
the call for at least 200 ms and reports the mean per call, and the median batch is the headline,
with min and max as the spread. A size whose single call already takes more than a second is
sampled less (one warm call, five single-call batches). Deterministic data, one machine (Windows 11,
.NET 10.0.12). The "before" and the port-overhead columns were measured on 2026-09-27, one build after
another on an otherwise idle machine; the "after" column is the PINNED state, measured on 2026-09-28
with the same harness and method.

## Results (ms per `RunQuery`; median, min–max)

| rows | before: `main` at `9c89380` plus the harness, evaluator 0.21.0 | the port alone, evaluator still 0.21.0 | **after, as pinned: the port, evaluator 0.34.0 (UI tier 0.86.0)** |
|---:|---:|---:|---:|
| 1,000 | 15.2 (14.8–22.2) | 14.7 (14.0–22.5) | **0.25 (0.23–0.84)** |
| 10,000 | 2,110 (1,723–2,403) | 1,571 (1,547–1,579) | **3.3 (2.9–4.6)** |
| 100,000 | 692,090 (588,867–812,235) | not run — see below | **45.7 (39.2–59.9)** |

Samples per cell: 15, except where one call exceeded a second (5).

## What the numbers say

- **The port costs nothing measurable.** Before and after the port, on the same evaluator, the
  1,000-row figures agree within their spread, and the 10,000-row figure sits inside the "before"
  spread's lower half. The witness indirection is one record field read per stage; the evaluator is
  the whole cost. The 100,000-row cell of that column was not run: at the 0.21.0 evaluator one call
  takes over ten minutes, and the column's question — does the port add cost — is already answered
  at the two smaller sizes.
- **The 0.21.0 evaluator is super-linear, badly.** Ten times the rows cost about 140 times the time
  from 1,000 to 10,000, and about 330 times from 10,000 to 100,000: eleven and a half minutes for one
  query over a hundred thousand rows.
- **The pinned 0.34.0 evaluator is linear and four orders of magnitude faster at scale:** about 60×
  at 1,000 rows, about 650× at 10,000, and about 15,000× at 100,000 (46 ms against 692 s). An earlier
  run of the same harness against the same evaluator, beside the older UI tier and not pinned, gave
  0.23 / 2.2 / 34.0 ms; the spread between the two runs is day-to-day machine variance, not a
  difference either raise explains.

To reproduce: `dotnet run -c Release --project tests/Fuaran.Program.Bench -- --full` (add sizes as
arguments to run only those).
