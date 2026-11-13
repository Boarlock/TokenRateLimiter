# TokenRateLimiter

A lightweight, thread-safe token bucket rate limiter for C#/.NET, with no locks.

## What it is

`TokenRateLimiter` holds a pool of tokens that refills over time at a fixed rate, up to a
capacity. Each unit of work costs one or more tokens. Callers ask for tokens;
the bucket either grants them or reports why it can't, with a wait time.

This is the standard token bucket algorithm, used for rate limiting (APIs,
logins), traffic shaping, and anything that needs to allow short bursts while
enforcing a long-term average rate.

## Requirements

.NET 8 or later, C# 12 or later.

## Features

- Asynchronous Acquisition
- Thread-Safe
- Adjustable Configuration
- TimeProvider Integration
- State Snapshots

## Quick start

```csharp
using TokenRateLimiter;

// Creating a bucket: 10 token capacity, 2 tokens/second regeneration
var bucket = new Bucket(cap: 10, regen: 2m);

// To start empty instead of full, pass startingTokens explicitly:
// var bucket = new Bucket(cap: 10, regen: 2m, startingTokens: 0);

// Requesting tokens
// Use TryRequest() to attempt to consume tokens immediately.
if (bucket.TryRequest(3))
{
    // Returns true when succeeded
}

// You can also retrieve the estimated refill time when a request cannot be fulfilled.
if (!bucket.TryRequest(3, out TimeSpan retryTime))
{
    Console.WriteLine($"Retry in ~ {retryTime.TotalSeconds:F2} seconds.");
}

// Asynchronous requests
// Use RequestAsync() when you want to wait for tokens rather than immediately reject a request.
bool authorized = await bucket.RequestAsync(3);

if (authorized)
{
    Console.WriteLine("Request authorized!");
}

// Result
// TokenRateLimiter exposes a public enum Result with 5 types, each of the methods shown have overloads that return Result.
Result result = await bucket.RequestResultAsync(3);

switch (result)
{
    case Result.Authorized:
        Console.WriteLine("Authorized");
        break;

    case Result.Disabled:
        Console.WriteLine("Bucket is disabled");
        break;

    case Result.Insufficient:
        Console.WriteLine("Requests exceeds current tokens");
        break;

    case Result.ExceedsCapacity:
        Console.WriteLine("Request exceeds bucket capacity");
        break;

    case Result.NeverRefills:
        Console.WriteLine("Bucket cannot replenish tokens");
        break;
}
```

## API Reference

### Constructor

| Parameter | Description |
|---|---|
| `cap` | Maximum tokens the bucket can hold. Must be non-negative. |
| `regen` | Tokens added per second of elapsed time. Must be non-negative. `0` means the bucket never refills on its own. |
| `startingTokens` | Initial balance. Defaults to `cap` (starts full). Clamped to `cap` if higher. |
| `timeProvider` | Clock to use. Defaults to `TimeProvider.System`. Pass your own to control how the bucket measures elapsed time — see [TimeProvider](#timeprovider) below. |

Tokens are computed lazily: every access recalculates the balance from the
regen rate and how much time has passed since the last access, so there's no
background timer.

---

### Properties

#### `Tokens` — `int`, get/set

The whole tokens currently available. Fractional regeneration is tracked
internally but never exposed; `Tokens` is always the floor of the real balance.

**Contract:** a request for `n` tokens is honored only if the full `n` is
present. If the bucket holds 2.9 tokens, `Tokens` reports `2`, a request for
2 succeeds, and a request for 3 is denied.

Setting `Tokens` replaces the balance, clamped to `[0, Capacity]`.

`Tokens` is a snapshot, not a reservation. Reading it and then requesting
that many tokens is not atomic.

#### `Capacity` — `int`, get/set

The maximum the bucket can hold. Setting a smaller value clamps the current
balance down to the new capacity. Setting a larger value does not add tokens.

#### `Regen` — `decimal`, get/set

Tokens added per second. Unlike `Tokens` and `Capacity`, this is `decimal`
rather than `int`, so sub-1 rates (e.g. one token every 10 seconds) are valid.

#### `Enabled` — `bool`, get/set

When `false`, every request is denied with `Result.Disabled`, regardless of
token count. Tokens still accumulate while disabled. Re-enable to resume
granting requests.

---

### Requesting tokens

#### `TryRequest(int)` / `TryRequest(int, out TimeSpan)` → `bool`

The simple form. Returns whether the request was granted. The `out` overload
also gives a suggested wait time.

#### `RequestResult(int)` / `RequestResult(int, out TimeSpan)` → `Result`

The detailed form. Tells you *why* a request failed, not just that it did.

| `Result` | Meaning | `retryTime` |
|---|---|---|
| `Authorized` | Granted. Tokens were deducted. | `TimeSpan.Zero` |
| `Insufficient` | Not enough tokens right now, but the bucket will refill. | Time until enough tokens accumulate. |
| `ExceedsCapacity` | The request is larger than the bucket could ever hold. Waiting never helps. | `TimeSpan.MaxValue` |
| `Disabled` | The bucket is disabled. Waiting never helps until re-enabled. | `TimeSpan.MaxValue` |
| `NeverRefills` | `Regen` is `0` and there aren't enough tokens. Waiting never helps until `Regen` is raised or tokens are added. | `TimeSpan.MaxValue` |

#### `RequestAsync(int, CancellationToken)` → `Task<bool>`, `RequestResultAsync(...)` → `Task<Result>`

Waits for tokens to become available rather than failing immediately.
Internally retries `RequestResult` and delays for the reported `retryTime`
between attempts, using the bucket's own `TimeProvider`. Returns immediately
(no waiting) for any result other than `Insufficient` — a disabled,
never-refilling, or over-capacity request won't be retried.

---

### Other methods

#### `EnsureTokens(int value)`

Raises the balance to at least `value`, clamped to `Capacity`. Never lowers
the balance — if there are already more tokens than `value`, nothing changes.

#### `ConsumeAll()` → `int`

Atomically takes every whole token currently available and returns how many
it took. Returns `0` if the bucket is disabled or empty. This is the
one-step alternative to reading `Tokens` and then requesting that amount,
which is **not** guaranteed to succeed under concurrent access.

#### `GetSnapshot()` → `BucketSnapshot`

```csharp
public readonly record struct BucketSnapshot(int Tokens, int Capacity, decimal Regen, bool Enabled);
```

Returns all four values from one instant, useful for logging or display.
Reading the four properties individually can mix values from different
moments in time under concurrent access; `GetSnapshot` can't.

---

## Thread safety

`Bucket` is thread-safe **without locks**.

All mutable state (tokens, capacity, regen, enabled, last-update time) lives
in one private, immutable snapshot object. Every operation:

1. Reads the current snapshot with `Volatile.Read`.
2. Computes a new snapshot from it. The original is never mutated.
3. Publishes the new snapshot with `Interlocked.CompareExchange`, which
   succeeds only if no other thread has already published a different
   snapshot in the meantime.
4. If the publish fails, the operation retries from the latest snapshot.

This gives every individual call (`TryRequest`, `Tokens = x`, `ConsumeAll`,
etc.) the same correctness guarantee a lock would: no two threads can "split"
the same tokens.

**What this does not make atomic:** a *sequence* of calls. Reading `Tokens`
and then calling `TryRequest` with that value is two separate operations;
another thread can act on the bucket in between. If you need both as one
step, use `ConsumeAll` or `RequestResult`, not a read followed by a request.

## Precision

Internally, the balance is tracked as `decimal` so that slow regen rates
(e.g. `0.1` tokens/second) accumulate correctly between calls instead of
rounding to zero. The public `Tokens`/`Capacity` surface is `int`, since a
token represents one indivisible unit of work, but the fractional part isn't
lost, it's just not shown — so regeneration below a fraction of a token
still works, and a request for `n` integer tokens is only granted once the
full `n` has accumulated.

## TimeProvider

Every `Bucket` is driven by a `TimeProvider` (from `System`, .NET 8+), which
is just an object that knows how to answer "what time is it" and "how much
time passed between these two readings." Whenever the bucket needs to know
how many tokens have regenerated, it asks its `TimeProvider` for the current
timestamp and uses that to work out the elapsed time since the last check.

By default, a `Bucket` uses `TimeProvider.System`, which reads the real
system clock:

```csharp
var bucket = new Bucket(cap: 10, regen: 1m);
// equivalent to:
var bucket = new Bucket(cap: 10, regen: 1m, timeProvider: TimeProvider.System);
```

You only need to pass a different `TimeProvider` if you want the bucket to
measure time some other way — most commonly in tests, where you don't want
to actually `Thread.Sleep` for real seconds to check that regeneration
works. To do that, you implement your own `TimeProvider` subclass that
returns a timestamp you control instead of reading the real clock:

```csharp
public sealed class ManualTimeProvider : TimeProvider
{
    private long _now;

    // The bucket calls this every time it needs the current time.
    public override long GetTimestamp() => _now;

    // Nothing moves the clock forward except this call.
    public void Advance(TimeSpan by) =>
        _now += (long)(by.TotalSeconds * Stopwatch.Frequency);
}

var time = new ManualTimeProvider();
var bucket = new Bucket(cap: 10, regen: 1m, startingTokens: 0, timeProvider: time);

bucket.TryRequest(1);          // false, no tokens yet
time.Advance(TimeSpan.FromSeconds(5));
bucket.TryRequest(1);          // true, 5 seconds have "passed" and tokens regenerated
```

Nothing about the bucket changes when you do this. It's still asking the
same question ("how much time passed?") — the only difference is who's
answering it. `RequestAsync` and `RequestResultAsync` use the same provider
for their internal waits too, so a custom `TimeProvider` controls the whole
bucket consistently, not just the token math.
