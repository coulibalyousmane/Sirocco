## What each tool reports about this run

Requested load: **6,000 iterations** at a constant rate above the target's capacity. The target never refuses a request: it makes it wait.

| Tool | Open model | VU ceiling | Requests | Failures | Reported latency (p99) | Scheduling wait |
|---|---|---:|---:|---:|---:|---|
| **Tempest** | bounded | 50 | 6,000 | 0.0% | **28,311.6 ms** | **measured**: max debt 27,795.8 ms |
| k6 | bounded | 50 | 4,092 | 0.0% | 1,162.6 ms | no; `dropped_iterations` = 1,907 |
| Gatling | unbounded | — | 4,827 | 0.0% | 14,573.0 ms | no (no internal queue) |
| NBomber | unbounded | — | 5,967 | 0.2% | 27,672.6 ms | no (no internal queue) |

The latency column is not the same quantity everywhere, and that is documented rather
than smoothed over: `__iteration` Response for Tempest, `iteration_duration` for k6, the
`Global Information` block for Gatling — three ways of saying "the whole iteration". For
NBomber it is the `checkout` step alone: it does not aggregate per iteration.

## Load actually delivered

The same quantity for all four: successful `checkout` requests, out of the
6,000 requested.

| Tool | Delivered | Missing | What the tool says about it |
|---|---:|---:|---|
| **Tempest** | 6,000 | 0 | `droppedCount` = 0; debt published separately |
| k6 | 4,092 | 1,908 | `dropped_iterations` = 1,907 |
| Gatling | 4,827 | 1,173 | 1,173 × `j.n.NoRouteToHostException` ; 1,173 × `checkout: No attribute named 'token' is defined` |
| NBomber | 5,958 | 42 | `failCount` = 42 on the scenario |

## The same run, Tempest's two measurements

| Measurement | p50 | p95 | p99 |
|---|---:|---:|---:|
| **Service** — the request timed from the moment it was sent | 733.2 ms | 794.6 ms | 819.2 ms |
| **Response** — timed from when it *should* have been sent | 14,549.0 ms | 27,263.0 ms | **28,311.6 ms** |

- Gap at p99: **27,492.4 ms**
- Maximum scheduling debt: **27,795.8 ms**
- Iterations measured: 6,000 of 6,000 requested, 0 of them abandoned
- Failure rate: 0.0%
- Actual run duration: 88.6 s — the injector kept draining its backlog after the profile ended

## Where the debt lands

| Step | Samples | Response p99 | Service p99 | Max debt |
|---|---:|---:|---:|---:|
| `__iteration` | 6,000 | 28,311.6 ms | 819.2 ms | 27,795.8 ms |
| `login` | 6,000 | 27,656.2 ms | 157.7 ms | 27,795.8 ms |
| `checkout` | 6,000 | 688.1 ms | 688.1 ms | 0.0 ms |

## Control: the same profile against a target that sheds load

Tempest alone, exactly the same parameters. One single variable changes: the target
refuses after 50 ms instead of making the caller wait.

| Target | Failures | Service p99 | Response p99 | Gap at p99 | Max debt |
|---|---:|---:|---:|---:|---:|
| Queues | 0.0% | 819.2 ms | 28,311.6 ms | 27,492.4 ms | 27,795.8 ms |
| Sheds load (503) | 32.0% | 385.0 ms | 421.9 ms | 36.9 ms | 188.4 ms |

