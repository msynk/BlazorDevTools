using BlazorDevTools.Events;
using BlazorDevTools.Internal;

namespace BlazorDevTools.Tests;

public class ConcurrencyTests
{
    [Fact]
    public void Ring_buffer_survives_parallel_writers_and_readers()
    {
        var buffer = new RingBuffer<int>(1000);
        var errors = 0;
        Parallel.For(0, 200_000, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
        {
            try
            {
                buffer.Add(i);
                if (i % 997 == 0)
                {
                    _ = buffer.ToArray();
                    _ = buffer.Last;
                    _ = buffer.FindLast(x => x == i);
                }
            }
            catch
            {
                Interlocked.Increment(ref errors);
            }
        });

        Assert.Equal(0, errors);
        Assert.Equal(1000, buffer.Count);
        Assert.Equal(1000, buffer.ToArray().Distinct().Count());
    }

    [Fact]
    public void Timeline_and_error_center_accept_parallel_recording()
    {
        var (session, scope) = TestHelpers.CreateSession(o => o.MaxEvents = 2000);
        using (scope)
        {
            var exceptions = 0;
            Parallel.For(0, 20_000, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
            {
                try
                {
                    var id = session.Timeline.Record(new DevToolsEvent { Kind = DevToolsEventKind.Custom, Category = "t", Title = "e" + i });
                    if (i % 3 == 0)
                    {
                        session.Timeline.Complete(id, i);
                    }

                    if (i % 500 == 0)
                    {
                        session.Errors.Record(new InvalidOperationException("err" + (i % 7)), "test");
                        session.Diagnostics.Evaluate(force: true);
                    }
                }
                catch
                {
                    Interlocked.Increment(ref exceptions);
                }
            });

            Assert.Equal(0, exceptions);
            Assert.Equal(2000, session.Timeline.Count);
            var ids = session.Timeline.Snapshot().Select(e => e.Id).ToList();
            Assert.Equal(ids.OrderBy(x => x), ids);
            Assert.True(session.Errors.TotalOccurrences == 40);
        }
    }

    [Fact]
    public void Sessions_can_be_created_and_disposed_concurrently()
    {
        var exceptions = 0;
        Parallel.For(0, 64, _ =>
        {
            try
            {
                var (session, scope) = TestHelpers.CreateSession();
                session.Timeline.Record(new DevToolsEvent { Title = "x" });
                scope.Dispose();
                Assert.True(session.IsDisposed);
            }
            catch
            {
                Interlocked.Increment(ref exceptions);
            }
        });

        Assert.Equal(0, exceptions);
    }
}
