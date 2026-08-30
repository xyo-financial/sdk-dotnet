using System;
using System.Collections.Generic;
using Microsoft.Extensions.Options;

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

    public TOptions Get(string? name) => CurrentValue;

    public IDisposable OnChange(Action<TOptions, string?> listener)
    {
        _listeners.Add(listener);
        return new Unsubscriber(() => _listeners.Remove(listener));
    }

    /// <summary>
    /// Sets a new current value and synchronously invokes every registered <see cref="OnChange"/> listener,
    /// simulating the underlying configuration's change token firing.
    /// </summary>
    public void Change(TOptions newValue)
    {
        CurrentValue = newValue;

        // Copied so a listener that disposes its own subscription mid-notification (as XyoClient does not,
        // but a well-behaved consumer might) cannot mutate _listeners while this loop is iterating it.
        foreach (var listener in _listeners.ToArray())
        {
            listener(newValue, null);
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
