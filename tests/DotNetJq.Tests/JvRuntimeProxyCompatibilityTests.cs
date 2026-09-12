using System.Reflection;
using DotNetJq.Port;

namespace DotNetJq.Tests;

public sealed class JvRuntimeProxyCompatibilityTests
{
    [Fact]
    public void ManagedAllocationHelpersPreserveSizesZeroFillAndReallocationPrefixes()
    {
        Assert.Empty(libjq.jv_mem_alloc(0));
        Assert.Equal(new byte[6], libjq.jv_mem_calloc(2, 3));
        Assert.Null(libjq.jv_mem_calloc_unguarded(int.MaxValue, 2));

        var original = new byte[] { 1, 2, 3 };
        Assert.Equal(new byte[] { 1, 2, 3, 0, 0 }, libjq.jv_mem_realloc(original, 5));
        Assert.Equal(new byte[] { 1, 2 }, libjq.jv_mem_realloc(original, 2));
        Assert.Empty(libjq.jv_mem_realloc(original, 0));

        var source = new string("managed-copy".AsSpan());
        var duplicate = libjq.jv_mem_strdup(source);
        Assert.Equal(source, duplicate);
        Assert.NotSame(source, duplicate);
        libjq.jv_mem_free(duplicate);

        object marker = new();
        libjq.jv_nomem_handler(_ => { }, marker);
        Assert.Equal(new byte[1], libjq.jv_mem_alloc(1));
        libjq.jv_nomem_handler(null, null);
    }

    [Fact]
    public void PthreadShapedMutexAndOncePreserveOwnershipAndRunOnceBehavior()
    {
        using var mutex = new pthread_mutex_t();
        Assert.Equal(0, libjq.pthread_mutex_init(mutex));
        Assert.Equal(0, libjq.pthread_mutex_lock(mutex));
        Assert.Equal(libjq.EBUSY, libjq.pthread_mutex_destroy(mutex));

        var nonOwnerResult = 0;
        var nonOwner = new Thread(() => nonOwnerResult = libjq.pthread_mutex_unlock(mutex));
        nonOwner.Start();
        nonOwner.Join();
        Assert.Equal(libjq.EPERM, nonOwnerResult);
        Assert.Equal(0, libjq.pthread_mutex_unlock(mutex));
        Assert.Equal(0, libjq.pthread_mutex_destroy(mutex));
        Assert.Equal(libjq.EINVAL, libjq.pthread_mutex_lock(mutex));

        var once = new pthread_once_t();
        var calls = 0;
        Parallel.For(0, 64, _ =>
        {
            Assert.Equal(0, libjq.pthread_once(once, () => Interlocked.Increment(ref calls)));
        });
        Assert.Equal(1, calls);

        var retryOnce = new pthread_once_t();
        var attempts = 0;
        var failure = new InvalidOperationException("initializer failed");
        var thrown = Assert.Throws<InvalidOperationException>(
            () => libjq.pthread_once(
                retryOnce,
                () =>
                {
                    attempts++;
                    throw failure;
                }));
        Assert.Same(failure, thrown);

        Assert.Equal(0, libjq.pthread_once(retryOnce, () => attempts++));
        Assert.Equal(0, libjq.pthread_once(retryOnce, () => attempts++));
        Assert.Equal(2, attempts);
    }

    [Fact]
    public void ThreadSpecificValuesAndDtoaContextsAreIsolatedPerThread()
    {
        var destroyed = new List<object>();
        var destroyGate = new object();
        Assert.Equal(
            0,
            libjq.pthread_key_create(
                out var key,
                value =>
                {
                    lock (destroyGate)
                    {
                        destroyed.Add(Assert.IsType<object>(value));
                    }
                }));

        object mainValue = new();
        object workerValue = new();
        Assert.Equal(0, libjq.pthread_setspecific(key, mainValue));
        Assert.Same(mainValue, libjq.pthread_getspecific(key));

        var mainContext = libjq.tsd_dtoa_context_get();
        Assert.Same(mainContext, libjq.tsd_dtoa_context_get());
        dtoa_context? workerContext = null;
        var worker = new Thread(() =>
        {
            Assert.Null(libjq.pthread_getspecific(key));
            Assert.Equal(0, libjq.pthread_setspecific(key, workerValue));
            Assert.Same(workerValue, libjq.pthread_getspecific(key));
            workerContext = libjq.tsd_dtoa_context_get();
            Assert.Same(workerContext, libjq.tsd_dtoa_context_get());
        });
        worker.Start();
        worker.Join();

        Assert.NotNull(workerContext);
        Assert.NotSame(mainContext, workerContext);
        key.Dispose();
        Assert.Equal(2, destroyed.Count);
        Assert.Contains(mainValue, destroyed);
        Assert.Contains(workerValue, destroyed);
    }

    [Fact]
    public void NoMemoryHandlerUsesDeclaredThreadStaticIsolation()
    {
        var field = typeof(libjq).GetField(
            "current_nomem_handler",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        Assert.True(field.IsDefined(typeof(ThreadStaticAttribute), inherit: false));

        object mainMarker = new();
        libjq.jv_nomem_handler(_ => { }, mainMarker);
        var mainState = field.GetValue(null);
        Assert.NotNull(mainState);

        object? workerStateBeforeRegistration = new();
        object? workerStateAfterRegistration = null;
        var worker = new Thread(() =>
        {
            workerStateBeforeRegistration = field.GetValue(null);
            libjq.jv_nomem_handler(_ => { }, new object());
            workerStateAfterRegistration = field.GetValue(null);
            libjq.jv_nomem_handler(null, null);
        });
        worker.Start();
        worker.Join();

        Assert.Null(workerStateBeforeRegistration);
        Assert.NotNull(workerStateAfterRegistration);
        Assert.NotSame(mainState, workerStateAfterRegistration);
        Assert.Same(mainState, field.GetValue(null));
        libjq.jv_nomem_handler(null, null);
    }
}
