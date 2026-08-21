# Zero errors, and still unusable: scheduling debt

*Version française : [Zéro erreur, et pourtant inutilisable](dette-ordonnancement.md).*

Four load-testing tools, the same scenario, the same target, the same rate, the same minute. The
99th percentiles they report range from **one second to nearly thirty**.

None of the four is lying, and none is concealing anything: each accounts, in its own way, for what
got away from it. But the numbers line up according to a rule no report displays: **the less of the
requested load a tool actually delivered, the more flattering its 99th percentile.** And only one of
the four delivered all of it.

What separates them has a name — **scheduling debt** — and it does not depend on the tool's brand
but on a design choice every injector makes. This article explains which choice, why most
load-testing campaigns never see it, and how to catch it with the tool you already use, Tempest or
not.

## What the open model guarantees, and what it does not

A short recap, because everything rests on it. In a **closed model**, a virtual user sends a
request, waits for the response, then sends the next one. When the target slows down, the injector
slows down with it: it sends fewer requests, and the ones it does not send are exactly the ones
that would have been slow. The report improves as the system degrades. This is *coordinated
omission*, as described by Gil Tene.

The **open model** (arrival rate) fixes that: requests leave at a rate set by the clock, not by the
target. k6, Gatling, JMeter, NBomber and Tempest all have one. "We avoid coordinated omission" is
therefore nobody's differentiator.

But the open model is a promise about **intent**: *n* requests per second will be **scheduled**. It
says nothing about them being **sent**. An injector has finite resources — a virtual-user ceiling,
sockets, cores. The moment it can no longer send on time, the open model quietly degenerates into
something else.

That moment is what this article is about.

## Scheduling debt, defined

For a given request, three instants matter:

- the instant it **was supposed** to leave, imposed by the load profile;
- the instant it **actually** left;
- the instant the response came back.

Which gives three durations, of which only two are usually published:

```text
Service  = response received − actual departure      → what an HTTP client times
Response = response received − scheduled departure   → what the caller actually waited
Debt     = actual departure  − scheduled departure   → the injector's own lateness
```

(These three quantities are the `ServiceTicks`, `ResponseTicks` and `SchedulingDelayTicks`
properties of [`MetricResult`](https://github.com/coulibalyousmane/Tempest/blob/main/src/Tempest.Domain/Metrics/MetricResult.cs).)

Debt is not the target's latency. It is **your injector's** lateness. And a late injector has only
two options, which distort the report in two different ways:

1. **Drop** the request. It then vanishes from every percentile — and since dropping only happens
   once everything is already busy, the sacrificed requests are exactly the ones that would have
   been slowest.
2. **Send it late**. It gets measured, but if you time it from its *actual* departure rather than
   its *scheduled* one, the wait the caller suffered disappears from the number.

Both are defensible. What is not defensible is failing to say so.

## Why an ordinary load campaign shows none of this

This repository's [comparative benchmark](https://github.com/coulibalyousmane/Tempest/blob/main/benchmark/results/RESULTS.md)
replays the same scenario against the same saturated target with all four tools. Observed
scheduling debt: **19.1 ms** on a p99 of 337.9 ms. Five per cent. Nothing to write about.

The reason is not that the tools are good. It is that **the target was good**.

That target protects itself: beyond 8 concurrent checkouts it waits 50 ms for a slot, then returns
a `503`. It **sheds load**. Shedding frees the virtual user in 50 ms, the injector never falls
behind, and there genuinely is nothing to see.

But most real systems do not shed. They **queue**: a thread pool, a connection pool, a message
queue, a database with its 200 connections. They do not turn you away, they make you wait. That is
often even presented as a virtue.

Hence the experiment: **change exactly one thing.**

## The experiment

Same target, same scenario (login then checkout), same four tools, same orchestration as the
published benchmark. One variable changes: `QUEUE_WAIT_MS`, the target's maximum wait before it
refuses.

[!code-yaml[](../../benchmark/docker-compose.yml)]

Target capacity: 8 slots, 80 to 150 ms of simulated processing, so roughly **70 requests per
second**. We ask for a **constant 100 req/s for 60 seconds** — comfortably above capacity, so the
queue grows linearly and there is only one slope to explain.

The virtual-user ceiling is set to **50 everywhere one exists**:

[!code-javascript[](../../benchmark/k6/checkout.js)]

Everywhere one exists, because it does not exist everywhere — and that is a substantive point
rather than housekeeping. Gatling's `injectOpen` and NBomber's `Simulation.Inject` have **no**
concurrency ceiling: they create as many users as the rate demands. That is not a flaw, it is a
different design, and it produces different behaviour under saturation.

One command, both passes (queueing target, then the load-shedding control):

```bash
./benchmark/saturation.sh
```

## The measurements

[!include[](_mesures-en.md)]

## Reading these numbers

Start with the table nobody ever thinks to read: **only one tool delivered the load it was asked
for.** The other three fell short, and each of them says so — a drop counter in k6, network
exceptions in Gatling, a `failCount` in NBomber. Nobody is cheating.

But put the two tables side by side and the ordering jumps out:

- Tempest, which delivered **everything** it was asked for, reports the highest p99.
- NBomber, short by a few dozen requests, reports very nearly the same — and since it has no notion
  of scheduling debt at all, that agreement is a **cross-validation** of Tempest's *Response*
  figure. Two independent mechanisms, one answer.
- Gatling, missing a fifth of the load, reports roughly half.
- k6, missing close to a third, reports more than twenty times less.

**The p99 looks better the less work the tool actually did.** That is not a coincidence, and it is
not the same cause in all three cases.

### The waiting time is conserved; only its location changes

The total wait is a property of the **target**: 100 requests per second arriving at a system that
serves 70 makes a growing queue, whichever tool is pushing. What differs
between injectors is **where that queue accumulates** and **who counts it**:

- **Gatling and NBomber do not bound their concurrency.** They create as many virtual users as
  needed, requests leave on time, and the wait happens *inside the request itself*. It therefore
  shows up naturally in the reported latency — hence NBomber's agreement with Tempest. But the price
  is right there in the delivery table: at a thousand concurrent users, Gatling exhausted the
  machine's sockets (`NoRouteToHostException`), and a fifth of the load never reached the target. Its
  p99 therefore describes what was left — that is, the fastest requests. **This is the blind spot of
  the unbounded model: the injector becomes the bottleneck, and nothing in the latency says so.**
- **k6 and Tempest do bound their concurrency** — 50 virtual users here. The queue can no longer
  build up in the target: it builds up **in the injector**. That is precisely scheduling debt. And
  that is where the two tools diverge.

### k6 drops; Tempest delays and measures

Faced with a request it cannot launch on time, k6 **drops** it. That is a defensible choice: it
protects the injector and avoids a snowball effect. And k6 does not conceal it — twice over. During
the run:

```text
level=warning msg="Insufficient VUs, reached 50 active VUs and cannot initialize more"
```

and in the final summary, `dropped_iterations` counts the sacrificed iterations — close to a third
of the requested load.

But look at the currency that information is denominated in. A dropped iteration never happened: it
therefore has **no latency**, and weighs on **no percentile**. The failure rate stays at 0 %, since
no request failed. Yet your SLO, your CI threshold and your alert are written in latency and error
rate. In that currency, the run is green.

And there is something worse than "green": the iterations that get dropped are systematically **the
slowest ones**, since dropping happens precisely when every virtual user is busy waiting. The
published percentile therefore describes a sample with its tail removed. That is coordinated
omission climbing back in through the window after the open model showed it out through the door.

Tempest makes the other choice: it **sends the request late** and times it from the instant it
should have left. The same physical event becomes latency — something a latency threshold catches
on its own, without anyone having had to think about watching a counter they did not know existed.
And it publishes the gap separately, so you know whether you are looking at the target's slowness
or the injector's.

Neither behaviour is wrong. But only one of them survives a dashboard where all anyone watches is
p99 and error rate.

### Where the debt lands — and where it hides

The per-step table deserves a pause. The debt is carried by the **first step of the iteration**, and
only by it: that step inherits the theoretical departure instant, while later steps start from
their own actual instant (charging them the debt would count it twice).

A very concrete consequence: on the `checkout` step, Response and Service are **identical**. An
operator who opens the report and looks at the step they care about sees **nothing**. You have to
read `__iteration`, or the first step.

## What Tempest also misses

An article that only praised its own tool would be worthless. Three blind spots, found while
running this very experiment:

- **The "injector fell behind" flag did not fire.** Tempest raises `InjectorFellBehind` when
  scheduled tokens were never issued. Here **all** of them were issued — just very late. That flag
  detects an injector that gives up, not one that lags. Only the debt gauge catches the second case.
- **The report declares itself trustworthy.** The `isTrustworthy` flag only looks at measurements
  lost for lack of room in the channel. A report can therefore be perfectly trustworthy and
  describe a system nobody would ship.
- **The debt is only visible on the first step** (above). It is consistent, it is documented, and it
  remains a reading trap.

And the limits of the protocol itself, which are real: a single machine with no isolation,
sequential runs, saturation settings chosen to fit on a personal workstation. The socket exhaustion
that truncated Gatling's run is a direct consequence — on a more generous machine, or with a
distributed injector, that particular ceiling would move. What this experiment demonstrates is a
**mechanism**, not a performance ranking between tools: do not conclude "Gatling is twice as fast",
conclude "read the delivered load before you read the p99".

## What to take away, whichever tool you use

Four checks that need neither Tempest nor any change of tooling:

1. **Know whether your injector bounds its concurrency.** That question decides everything else. If
   it does not (Gatling, NBomber in open mode), the wait will show up in your latencies — but
   nothing will tell you whether the injector was the one that gave way. If it does (k6 via
   `maxVUs`, Tempest via `--max-vus`), the queue moves into the injector and becomes invisible to
   anyone who does not know where to look.
2. **Compare the run's actual duration to the profile's duration.** It is the cheapest signal there
   is, and every tool displays it. A 60-second profile that takes considerably longer is an injector
   that spent the difference catching up. No percentile will tell you.
3. **Compare iterations achieved to iterations requested.** A 30 % gap between the two is a run that
   did not happen, not a run that passed.
4. **Find your tool's drop counter, and alert on it.** In k6 it is `dropped_iterations` — and a
   threshold on that counter (`thresholds: { dropped_iterations: ['count<1'] }`) catches what a p99
   threshold never will. If you cap `maxVUs`, that threshold is not a precaution: it is what makes
   your p99 interpretable.

And one design remark, stepping outside the subject of tools: **a system that sheds load is easier
to test honestly than one that queues.** The first refuses you an answer and says so; the second
makes you wait and lets every layer it passes through believe all is well. The control table on
this page shows it inverted: the load-shedding target posts a third of its requests as failures and
looks to be in worse shape — when it is the one treating its callers properly.

## Reproducing the experiment

Everything is in the repository, and the protocol is code, not a description:

- [`benchmark/saturation.sh`](https://github.com/coulibalyousmane/Tempest/blob/main/benchmark/saturation.sh) — both passes.
- [`benchmark/docker-compose.yml`](https://github.com/coulibalyousmane/Tempest/blob/main/benchmark/docker-compose.yml) — the target and its single variable.
- [`benchmark/k6/checkout.js`](https://github.com/coulibalyousmane/Tempest/blob/main/benchmark/k6/checkout.js), [`benchmark/gatling/CheckoutSimulation.java`](https://github.com/coulibalyousmane/Tempest/blob/main/benchmark/gatling/CheckoutSimulation.java), [`benchmark/nbomber/Program.cs`](https://github.com/coulibalyousmane/Tempest/blob/main/benchmark/nbomber/Program.cs), [`benchmark/scenarios/tempest-checkout.yaml`](https://github.com/coulibalyousmane/Tempest/blob/main/benchmark/scenarios/tempest-checkout.yaml) — the same scenario in all four tools.
- [`benchmark/results-saturation/SATURATION.md`](https://github.com/coulibalyousmane/Tempest/blob/main/benchmark/results-saturation/SATURATION.md) — the report for the run discussed here. As with the published benchmark, the four tools' raw outputs are not versioned: one command regenerates them.

The figures on this page are not copied by hand: they are generated by
`benchmark/normalize --saturation` from those outputs, and the French version includes the **same**
fragment. There can be no figure that is right in one language and wrong in the other.
