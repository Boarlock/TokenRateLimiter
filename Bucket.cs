using System.Numerics;

namespace TokenRateLimiter {

    public readonly record struct BucketSnapshot(
        int Tokens, 
        int Capacity, 
        decimal Regen, 
        bool Enabled);

    public enum Result {
        Authorized,
        Insufficient,
        ExceedsCapacity,
        Disabled,
        NeverRefills
    }

    public class Bucket {

        private sealed record State(
            decimal Tokens, 
            decimal Capacity,
            decimal Regen, 
            bool Enabled, 
            long LastUpdate);

        private State _state;

        private readonly TimeProvider _time;
        public Bucket(
            int cap,
            decimal regen,
            int? startingTokens = null,
            TimeProvider? timeProvider = null) {

            _time = (timeProvider is null) ? TimeProvider.System : timeProvider;

            ArgumentOutOfRangeException.ThrowIfNegative(cap);
            ArgumentOutOfRangeException.ThrowIfNegative(regen);

            decimal tokens = startingTokens ?? cap;

            ArgumentOutOfRangeException.ThrowIfNegative(tokens);

            _state = new(
                Math.Min(tokens, cap),
                cap,
                regen,
                true,
                _time.GetTimestamp());
        }

        public BucketSnapshot GetSnapshot() {

            var s = AdvanceTokens(Volatile.Read(ref _state));
            return new BucketSnapshot((int)Math.Floor(s.Tokens), (int)s.Capacity, s.Regen, s.Enabled);
        }

        public bool Enabled {

            get => AdvanceTokens(Volatile.Read(ref _state)).Enabled;
            set => Update(
                s => s with { Enabled = value },
                () => null);
        }

        public int Tokens {

            get => (int)Math.Floor(AdvanceTokens(Volatile.Read(ref _state)).Tokens);
            set => Update(
                s => s with { Tokens = Math.Min(value, s.Capacity) },
                () => NonNegative(value, "Tokens"));

        }

        public int Capacity {

            get => (int)Math.Floor(AdvanceTokens(Volatile.Read(ref _state)).Capacity);
            set => Update(
                s => s with { Capacity = value, Tokens = Math.Min(s.Tokens, value) },
                () => NonNegative(value, "Capacity"));
        }

        public decimal Regen {

            get => AdvanceTokens(Volatile.Read(ref _state)).Regen;
            set => Update(
                s => s with { Regen = value },
                () => NonNegative(value, "Regen"));
        }

        private static ArgumentOutOfRangeException? NonNegative<TValue>(
            TValue value, 
            string paramName)
            where TValue : INumber<TValue> {

            if (value < TValue.Zero)
                return new ArgumentOutOfRangeException(paramName, value, "cannot be negative");

            return null;
        }

        private void Update(
            Func<State, State> change, 
            Func<ArgumentOutOfRangeException?> exceptionCond) {

            if (exceptionCond() is var except && except is not null)
                throw except;

            while (true) {

                var old = Volatile.Read(ref _state);
                var next = change(AdvanceTokens(old));

                if (ReferenceEquals(Interlocked.CompareExchange(ref _state, next, old), old))
                    break;
            }
        }

        private State AdvanceTokens(State oldState) {

            long now = _time.GetTimestamp();
            if (now <= oldState.LastUpdate)
                return oldState;

            TimeSpan span = _time.GetElapsedTime(oldState.LastUpdate, now);

            decimal elapsedSeconds = (decimal)span.Ticks / TimeSpan.TicksPerSecond;
            decimal tokens = Math.Min(oldState.Capacity, oldState.Tokens + (oldState.Regen * elapsedSeconds));

            return new State(tokens, oldState.Capacity, oldState.Regen, oldState.Enabled, now);
        }

        // Sets a floor which caps at capacity
        public void EnsureTokens(int value) => Update(
            s => s with { Tokens = Math.Min(s.Capacity, Math.Max(s.Tokens, value)) },
            () => NonNegative(value, "EnsureTokens"));

        // Tokens represent 1 unit of work therefore they can only be consumed 1 at a time or none
        // Internally we use decimal for regen / math operations to have the least rounding issues.

        public bool TryRequest(
            int tokensToConsume) 
            => RequestResult(tokensToConsume, out _) == Result.Authorized;

        public bool TryRequest(
            int tokensToConsume,
            out TimeSpan retryTime)
            => RequestResult(tokensToConsume, out retryTime) == Result.Authorized;

        public async Task<bool> RequestAsync(
            int tokensToConsume,
            CancellationToken cancellationToken = default)
            => await RequestResultAsync(tokensToConsume, cancellationToken) == Result.Authorized;

        public Result RequestResult(int tokensToConsume) => RequestResult(tokensToConsume, out _);
        public Result RequestResult(
            int tokensToConsume,
            out TimeSpan retryTime) {

            ArgumentOutOfRangeException.ThrowIfNegative(tokensToConsume);

            while (true) {

                var old = Volatile.Read(ref _state);
                var advanced = AdvanceTokens(old);

                if (!advanced.Enabled) {

                    retryTime = TimeSpan.MaxValue;
                    return Result.Disabled;
                }
                else if (tokensToConsume > advanced.Capacity) {

                    retryTime = TimeSpan.MaxValue;
                    return Result.ExceedsCapacity;
                }
                else if (advanced.Regen == 0 && advanced.Tokens < tokensToConsume) {

                    retryTime = TimeSpan.MaxValue;
                    return Result.NeverRefills;
                }
                else if (advanced.Tokens < tokensToConsume) {

                    retryTime = TimeToRefill(advanced, tokensToConsume);
                    return Result.Insufficient;
                }

                var next = advanced with { Tokens = advanced.Tokens - tokensToConsume };

                if (ReferenceEquals(Interlocked.CompareExchange(ref _state, next, old), old)) {

                    retryTime = TimeSpan.Zero;
                    return Result.Authorized;
                }
            }
        }

        public async Task<Result> RequestResultAsync(
            int tokensToConsume,
            CancellationToken cancellationToken = default) {

            ArgumentOutOfRangeException.ThrowIfNegative(tokensToConsume);

            while (true) {

                cancellationToken.ThrowIfCancellationRequested();

                switch (RequestResult(tokensToConsume, out TimeSpan retryTime)) {

                    case Result.Authorized:
                        return Result.Authorized;
                    case Result.Disabled:
                        return Result.Disabled;
                    case Result.ExceedsCapacity:
                        return Result.ExceedsCapacity;
                    case Result.NeverRefills:
                        return Result.NeverRefills;
                    case Result.Insufficient:
                        break;
                }

                var delay = retryTime < TimeSpan.FromMilliseconds(1)
                    ? TimeSpan.FromMilliseconds(1)
                    : retryTime;

                await Task.Delay(delay, _time, cancellationToken);
            }
        }

        public int ConsumeAll()
        {
            while (true) {

                var old = Volatile.Read(ref _state);
                var advanced = AdvanceTokens(old);

                if (!advanced.Enabled)
                    return 0;

                int take = (int)Math.Floor(advanced.Tokens);
                if (take == 0)
                    return 0;

                var next = advanced with { Tokens = advanced.Tokens - take };

                if (ReferenceEquals(Interlocked.CompareExchange(ref _state, next, old), old))
                    return take;
            }
        }

        private static TimeSpan TimeToRefill(State advanced, int tokensToConsume) {

            decimal deficit = tokensToConsume - advanced.Tokens;

            if (deficit <= 0)
                return TimeSpan.Zero;

            return TimeSpan.FromSeconds((double)(deficit / advanced.Regen));
        }
    }
}
