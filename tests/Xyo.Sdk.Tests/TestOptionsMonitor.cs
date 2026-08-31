using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Xyo.Sdk.Tests;

/// <summary>
/// Minimal, synchronous, in-process test double for <see cref="IOptionsMonitor{TOptions}"/>. The real
/// implementation reacts to an underlying <c>IConfiguration</c> change token, which in a unit test would
/// mean touching a file on disk and waiting on a FileSystemWatcher -- slow and flaky. <see cref="Change"/>
/// lets a test fire a reload deterministically, on the calling thread, which is all
/// <see cref="Client.XyoClient"/>'s reload handling needs to be exercised.
/// </summary>
internal sealed class TestOptionsMonitor<TOptions> : IOptionsMonitor<TOptions>
    where TOptions : class
{
    private readonly List<Action<TOptions, string?>> _listeners = new();

    public TestOptionsMonitor(TOptions initialValue)
    {
        CurrentValue = initialValue;
    }

    public TOptions CurrentValue { get; private set; }

    /// <summary>
    /// Number of listeners currently subscribed via <see cref="OnChange"/>. Used to assert that
    /// <see cref="Client.XyoClient.Dispose"/> actually unsubscribes rather than merely appearing to.
    /// </summary>
    public int ListenerCount => _listeners.Count;

    public TOptions Get(string? name) => CurrentValue;

    public IDisposable OnChange(Action<TOptions, string?> listener)
    {
        _listeners.Add(listener);
        return new Unsubscriber(() => _listeners.Remove(listener));
    }

    /// <summary>
    /// Sets a new current value and synchronously invokes every registered <see cref="OnChange"/> listener
    /// with <paramref name="name"/>, simulating the underlying configuration's change token firing for that
    /// named <typeparamref name="TOptions"/> instance (<c>null</c>, the default, simulates the default-named
    /// instance -- matching how a real <c>IOptionsMonitor</c> normalises a null name).
    /// </summary>
    /// <remarks>
    /// The real <see cref="Microsoft.Extensions.Options.OptionsMonitor{TOptions}"/> invokes its listeners for
    /// <b>every</b> named change-token source registered in the container, not only the default one, and
    /// passes that source's name through unaltered. Always passing <c>null</c> here (as this double did
    /// before <paramref name="name"/> existed) cannot exercise that behaviour, which is precisely why a
    /// subscriber that fails to filter on the name -- see <see cref="Client.XyoClient"/>'s
    /// <c>OnOptionsChanged</c> -- was invisible to the test suite (US-DOTNET-004).
    /// </remarks>
    public void Change(TOptions newValue, string? name = null)
    {
        CurrentValue = newValue;

        // Copied so a listener that disposes its own subscription mid-notification (as XyoClient does not,
        // but a well-behaved consumer might) cannot mutate _listeners while this loop is iterating it.
        foreach (var listener in _listeners.ToArray())
        {
            listener(newValue, name);
        }
    }

    private sealed class Unsubscriber : IDisposable
    {
        private readonly Action _onDispose;

        public Unsubscriber(Action onDispose)
        {
            _onDispose = onDispose;
        }

        public void Dispose() => _onDispose();
    }
}

/// <summary>
/// A real <see cref="IOptionsChangeTokenSource{TOptions}"/> a test can fire on demand, for driving the real
/// <see cref="Microsoft.Extensions.Options.OptionsMonitor{TOptions}"/> (as opposed to
/// <see cref="TestOptionsMonitor{TOptions}"/>) rather than a double, when the behaviour under test depends on
/// genuine <c>IOptionsMonitor</c> semantics -- specifically, that <c>OnChange</c> notifies for every named
/// change-token source registered in the container, not only the default one (US-DOTNET-004 / C1).
/// </summary>
internal sealed class TriggerableChangeTokenSource<TOptions> : IOptionsChangeTokenSource<TOptions>
{
    private CancellationTokenSource _cts = new();

    public TriggerableChangeTokenSource(string? name)
    {
        Name = name;
    }

    public string? Name { get; }

    /// <summary>
    /// A fresh token is produced on every call, because <c>OptionsMonitor</c> re-invokes this to re-register
    /// after each firing (a <see cref="CancellationChangeToken"/> only ever fires once); reusing a single
    /// already-cancelled token would silently stop this source from notifying again after the first
    /// <see cref="Trigger"/>.
    /// </summary>
    public IChangeToken GetChangeToken()
    {
        _cts = new CancellationTokenSource();
        return new CancellationChangeToken(_cts.Token);
    }

    /// <summary>
    /// Fires the change token, simulating this named <typeparamref name="TOptions"/> instance's underlying
    /// configuration source reloading.
    /// </summary>
    public void Trigger() => _cts.Cancel();
}
