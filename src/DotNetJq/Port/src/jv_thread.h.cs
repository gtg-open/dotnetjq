// DOTNETJQ PORT MAP
// Upstream repository: https://github.com/jqlang/jq
// Upstream revision: 34f7186b86743a083a589741b6cea95293524108 (jq-1.8.2)
// Upstream file: src/jv_thread.h
// Upstream URL: https://github.com/jqlang/jq/blob/jq-1.8.2/src/jv_thread.h
// Strategy: PROXY
// Target file: src/DotNetJq/Port/src/jv_thread.h.cs
//
// Rules:
// - Preserve upstream function names wherever possible.
// - Behavioral compatibility is defined by upstream tests/differential tests.
// - Do not refactor cross-file architecture during compatibility port.
//
// UPSTREAM COMPONENT: pthread-shaped mutex, once, and thread-specific-data primitives.
// REPLACEMENT: SemaphoreSlim, monitor-protected once state, and ThreadLocal<object?>.
// WHY: retain the mapped upstream symbol surface used by jq's thread regression coverage without
// introducing native pthread/Win32 ABI dependencies into the managed test assembly.
// PRODUCTION REACHABILITY: none. Production allocation, dtoa, and hash paths use their separately
// mapped CLR implementations and do not call these compatibility names. Ordinary framework builds
// retain this internal IL/metadata; NativeAOT trims the unreferenced proxy types.
// BEHAVIORAL CONTRACT: preserve acquire/release, run-once, and per-thread lookup behavior behind
// pthread-shaped compatibility names for direct proxy tests. These atomics and locks are auxiliary
// synchronization mechanics; they do not make jq_state, the VM, or jq value refcounts thread-safe.
// KNOWN DIFFERENCES: TSD destructors run when the managed key is disposed, not automatically
// at each native thread exit; POSIX cancellation and robust-mutex behavior are not modeled.
// TESTS COVERING THE SUBSTITUTION: JvRuntimeProxyCompatibilityTests directly covers mutex ownership,
// initializer retry and concurrent once-only initialization, thread-specific values, disposal, and
// destructors.

using System.Threading;

namespace DotNetJq.Port;

internal sealed class pthread_mutex_t : IDisposable
{
    private readonly SemaphoreSlim semaphore = new(1, 1);
    private int ownerThreadId;
    private int disposed;

    internal int Lock()
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            return libjq.EINVAL;
        }

        semaphore.Wait();
        Volatile.Write(ref ownerThreadId, Environment.CurrentManagedThreadId);
        return 0;
    }

    internal int Unlock()
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            return libjq.EINVAL;
        }

        if (Volatile.Read(ref ownerThreadId) != Environment.CurrentManagedThreadId)
        {
            return libjq.EPERM;
        }

        Volatile.Write(ref ownerThreadId, 0);
        semaphore.Release();
        return 0;
    }

    internal int Destroy()
    {
        if (Volatile.Read(ref ownerThreadId) != 0 || semaphore.CurrentCount == 0)
        {
            return libjq.EBUSY;
        }

        Dispose();
        return 0;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            semaphore.Dispose();
        }
    }
}

internal sealed class pthread_once_t
{
    private readonly object gate = new();
    private bool completed;

    internal void Run(Action initializer)
    {
        if (Volatile.Read(ref completed))
        {
            return;
        }

        lock (gate)
        {
            if (completed)
            {
                return;
            }

            initializer();
            Volatile.Write(ref completed, true);
        }
    }
}

internal sealed class pthread_key_t : IDisposable
{
    private readonly Action<object?>? destructor;
    private readonly ThreadLocal<object?> values = new(trackAllValues: true);
    private int disposed;

    internal pthread_key_t(Action<object?>? destructor)
    {
        this.destructor = destructor;
    }

    internal object? Value
    {
        get
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            return values.Value;
        }
        set
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            values.Value = value;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        if (destructor is not null)
        {
            foreach (var value in values.Values)
            {
                if (value is not null)
                {
                    destructor(value);
                }
            }
        }

        values.Dispose();
    }
}

internal static partial class libjq
{
    internal const int EPERM = 1;
    internal const int EBUSY = 16;
    internal const int EINVAL = 22;

    internal static int pthread_mutex_init(pthread_mutex_t mutex)
    {
        ArgumentNullException.ThrowIfNull(mutex);
        return 0;
    }

    internal static int pthread_mutex_lock(pthread_mutex_t mutex)
    {
        ArgumentNullException.ThrowIfNull(mutex);
        return mutex.Lock();
    }

    internal static int pthread_mutex_unlock(pthread_mutex_t mutex)
    {
        ArgumentNullException.ThrowIfNull(mutex);
        return mutex.Unlock();
    }

    internal static int pthread_mutex_destroy(pthread_mutex_t mutex)
    {
        ArgumentNullException.ThrowIfNull(mutex);
        return mutex.Destroy();
    }

    internal static int pthread_once(pthread_once_t once, Action initializer)
    {
        ArgumentNullException.ThrowIfNull(once);
        ArgumentNullException.ThrowIfNull(initializer);
        once.Run(initializer);
        return 0;
    }

    internal static int pthread_key_create(out pthread_key_t key, Action<object?>? destructor)
    {
        key = new pthread_key_t(destructor);
        return 0;
    }

    internal static int pthread_setspecific(pthread_key_t key, object? value)
    {
        ArgumentNullException.ThrowIfNull(key);
        key.Value = value;
        return 0;
    }

    internal static object? pthread_getspecific(pthread_key_t key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return key.Value;
    }
}
