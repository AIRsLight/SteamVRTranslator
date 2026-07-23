using SteamVRTranslator.App.Configuration;

namespace SteamVRTranslator.App.Translation;

internal sealed class ProviderConcurrencyLimiter
{
    private readonly object _sync = new();
    private readonly Dictionary<string, ProviderGate> _gates =
        new(StringComparer.OrdinalIgnoreCase);

    public Task<IDisposable> AcquireAsync(
        string providerId,
        int maximumConcurrency,
        CancellationToken cancellationToken)
    {
        ProviderGate gate;
        lock (_sync)
        {
            if (!_gates.TryGetValue(providerId, out gate!))
            {
                gate = new ProviderGate(Normalize(maximumConcurrency));
                _gates.Add(providerId, gate);
            }
        }

        return gate.AcquireAsync(Normalize(maximumConcurrency), cancellationToken);
    }

    public void UpdateLimits(IEnumerable<TranslationProviderConfiguration> providers)
    {
        foreach (var provider in providers)
        {
            ProviderGate? gate;
            lock (_sync)
            {
                _gates.TryGetValue(provider.Id, out gate);
            }
            gate?.UpdateLimit(Normalize(provider.MaxConcurrency));
        }
    }

    private static int Normalize(int value) => Math.Clamp(
        value,
        1,
        TranslationProviderConfiguration.MaximumMaxConcurrency);

    private sealed class ProviderGate
    {
        private readonly object _sync = new();
        private readonly LinkedList<Waiter> _waiters = [];
        private int _limit;
        private int _active;

        public ProviderGate(int limit)
        {
            _limit = limit;
        }

        public Task<IDisposable> AcquireAsync(int limit, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var waiter = new Waiter(this, cancellationToken);
            lock (_sync)
            {
                _limit = limit;
                waiter.Node = _waiters.AddLast(waiter);
                DispatchLocked();
            }
            waiter.RegisterCancellation();
            return waiter.Completion.Task;
        }

        public void UpdateLimit(int limit)
        {
            lock (_sync)
            {
                _limit = limit;
                DispatchLocked();
            }
        }

        public void Release()
        {
            lock (_sync)
            {
                if (_active <= 0)
                {
                    return;
                }
                _active--;
                DispatchLocked();
            }
        }

        private void Cancel(Waiter waiter)
        {
            var removed = false;
            lock (_sync)
            {
                if (waiter.Node?.List is not null)
                {
                    _waiters.Remove(waiter.Node);
                    waiter.Node = null;
                    removed = true;
                }
            }
            if (removed)
            {
                waiter.Completion.TrySetCanceled(waiter.CancellationToken);
            }
        }

        private void DispatchLocked()
        {
            while (_active < _limit && _waiters.First is { } node)
            {
                var waiter = node.Value;
                _waiters.RemoveFirst();
                waiter.Node = null;
                if (waiter.Completion.TrySetResult(new Lease(this)))
                {
                    _active++;
                }
            }
        }

        private sealed class Waiter
        {
            private readonly ProviderGate _gate;
            private CancellationTokenRegistration _registration;

            public Waiter(ProviderGate gate, CancellationToken cancellationToken)
            {
                _gate = gate;
                CancellationToken = cancellationToken;
            }

            public CancellationToken CancellationToken { get; }

            public TaskCompletionSource<IDisposable> Completion { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            public LinkedListNode<Waiter>? Node { get; set; }

            public void RegisterCancellation()
            {
                if (!CancellationToken.CanBeCanceled || Completion.Task.IsCompleted)
                {
                    return;
                }
                _registration = CancellationToken.Register(
                    static state => ((Waiter)state!).Cancel(),
                    this);
                _ = Completion.Task.ContinueWith(
                    static (_, state) => ((Waiter)state!)._registration.Dispose(),
                    this,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }

            private void Cancel() => _gate.Cancel(this);
        }

        private sealed class Lease(ProviderGate gate) : IDisposable
        {
            private ProviderGate? _gate = gate;

            public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
        }
    }
}
