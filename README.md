# TokenRateLimiter

A lightweight, thread-safe token bucket rate limiter for C#/.NET, with no locks.

## What it is

`TokenRateLimiter` holds a pool of tokens that refills over time at a fixed rate, up to a
capacity. Each unit of work costs one or more tokens. Callers ask for tokens;
the bucket either grants them or reports why it can't, with a wait time.

This is the standard token bucket algorithm, used for rate limiting (APIs,
logins), traffic shaping, and anything that needs to allow short bursts while
enforcing a long term average rate.

## Requirments

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

// Creating a bucket
// This create a bucket with 10 token capacity and 2 tokens/second regeneration rate
var bucket = new Bucket(
    cap: 10,
    regen: 2m
);

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

### API Reference

#### Constructor

| Parameter | Description |
|---|---|
| `cap` | Maximum tokens the bucket can hold. Must be non-negative. |
| `regen` | Tokens added per second of elapsed time. Must be non-negative. `0` means the bucket never refills on its own |
| `startingTokens` | Initial balance. Defaults to `cap` (starts full). Clamped to `cap` if higher. |
| `timeProvider` | Clock to use. Defaults to `TimeProvider.System`. Pass your own for testing |

#### Regeneration

`Tokens` are computed lazily so every access causes the token balance to update based off the regen rate and how much time has passed.

#### `Tokens` (`int`, get/set)

The whole tokens currently available. Fractional regeneration is tracked
internally but never exposed. `Tokens` are always the floor of the real balance.

**Contract:** a request for `n` tokens is honored only if the full `n` is
present. If the bucket holds 2.9 tokens, `Tokens` reports `2`, a request for
2 succeeds, and a request for 3 is denied.

Setting `Tokens` replaces the balance, clamped to `[0, Capacity]`.

`Tokens` are a snapshot, not a reservation. Reading it and then requesting that many tokens are not atomic.

#### `Capacity` (`int`, get/set)

The maximum the bucket can hold. Setting a smaller value clamps the current
balance down to the new capacity; setting a larger value does not add
tokens.

#### `Regen` (`decimal`, get/set)

Tokens added per second. Unlike `Tokens` and `Capacity`, this is `decimal`
rather than `int`, so sub-1 rates (e.g. one token every 10 seconds) are
valid.

#### `Enabled` (`bool`, get/set)

When `false`, every request is denied with `Result.Disabled`, regardless of
token count. Tokens still accumulate while disabled. Re-enable to resume
granting requests.

#### `TryRequest(int) → bool`, `TryRequest(int, out TimeSpan) → bool`

The simple form. Returns whether the request was granted. The `out` overload also gives a suggested wait time.

#### `RequestResult(int) → Result`, `RequestResult(int, out TimeSpan) → Result`

The detailed form. Tells you *why* a request failed, not just that it did.

| `Result` | Meaning | `retryTime` |
|---|---|---|
| `Authorized` | Granted. Tokens were deducted. | `TimeSpan.Zero` |
| `Insufficient` | Not enough tokens right now, but the bucket will refill. | Time until enough tokens accumulate. |
| `ExceedsCapacity` | The request is larger than the bucket could ever hold. Waiting never helps. | `TimeSpan.MaxValue` |
| `Disabled` | The bucket is disabled. Waiting never helps until re-enabled. | `TimeSpan.MaxValue` |
| `NeverRefills` | `Regen` is `0` and there aren't enough tokens. Waiting never helps until `Regen` is raised or tokens are added. | `TimeSpan.MaxValue` |

#### `RequestAsync(int, CancellationToken) → Task<bool>`, `RequestResultAsync(...) → Task<Result>`

Waits for tokens to become available rather than failing immediately.
Internally retries `RequestResult` and delays for the reported `retryTime`
between attempts, using the bucket's own `TimeProvider` so it composes with
fake clocks in tests. Returns immediately (no waiting) for any result other
than `Insufficient`, a disabled, never-refilling, or over-capacity request
won't be retried.

#### `EnsureTokens(int value)`

Raises the balance to at least `value`, clamped to `Capacity`. Never lowers
the balance, if there are already more tokens than `value`, nothing
changes.

#### `ConsumeAll() → int`

Atomically takes every whole token currently available and returns how many
it took. Returns `0` if the bucket is disabled or empty. This is the
one-step alternative to reading `Tokens` and then requesting that amount,
which is **not** guaranteed to succeed under concurrent access.

#### `GetSnapshot() → BucketSnapshot`

`public readonly record struct BucketSnapshot(int Tokens, int Capacity, decimal Regen, bool Enabled);`

Returns all four values from one instant, useful for logging or
display. Reading the four properties individually can mix values from
different moments in time under concurrent access, `GetSnapshot` can't.

### Thread safety

`Bucket` is thread-safe **without locks**.

All mutable state (tokens, capacity, regen, enabled, last-update time) lives
in one private, immutable snapshot object. Every operation:

- Reads the current snapshot with `Volatile.Read`.
- Computes a new snapshot from it, the original is never mutated.
- Publishes the new snapshot with `Interlocked.CompareExchange`, which
   succeeds only if no other thread has already published a different
   snapshot in the meantime.
- If the publish fails, the operation retries from the latest snapshot.

This gives every individual call (`TryRequest`, `Tokens = x`, `ConsumeAll`,
etc.) the same correctness guarantee a lock would, no two threads can
"split" the same tokens.

**What this does not make atomic:** a *sequence* of calls. Reading `Tokens`
and then calling `TryRequest` with that value is two separate operations;
another thread can act on the bucket in between. If you need both as one
step, use `ConsumeAll` or `RequestResult`, not a read followed by a request.

### Precision

Internally, the balance is tracked as `decimal` so that slow regen rates
(e.g. `0.1` tokens/second) accumulate correctly between calls instead of
rounding to zero. The public `Tokens`/`Capacity` surface is `int`, since a
token represents one indivisible unit of work, but the fractional part
isn't lost, it's just not shown, so regeneration below a fraction of a
token still works, and a request for `n` integer tokens is only granted
once the full `n` has accumulated.

### TimeProvider and Testing

The bucket accepts an optional TimeProvider:

```csharp
var bucket = new Bucket(
    cap: 10,
    regen: 1m,
    timeProvider: TimeProvider.System
);
```

When no provider is supplied, TimeProvider.System is used. Injecting a custom 
provider allows tests to control elapsed time without relying on actual waiting.
The same provider is used for token regeneration and asynchronous delays, making 
deterministic time based testing possible.