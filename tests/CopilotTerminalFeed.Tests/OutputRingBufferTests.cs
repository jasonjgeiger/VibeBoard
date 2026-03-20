using CopilotTerminalFeed.Server;
using Xunit;

namespace CopilotTerminalFeed.Tests;

public class OutputRingBufferTests
{
    [Fact]
    public void Empty_Buffer_Returns_Empty_Array()
    {
        var buffer = new OutputRingBuffer(1024);
        Assert.Empty(buffer.ToArray());
    }

    [Fact]
    public void Write_And_Read_Within_Capacity()
    {
        var buffer = new OutputRingBuffer(1024);
        var data = new byte[] { 1, 2, 3, 4, 5 };
        buffer.Write(data);

        var result = buffer.ToArray();
        Assert.Equal(data, result);
    }

    [Fact]
    public void Write_Wraps_Around_On_Overflow()
    {
        var buffer = new OutputRingBuffer(4);
        buffer.Write(new byte[] { 1, 2, 3 });
        buffer.Write(new byte[] { 4, 5 });

        // Buffer capacity is 4, so we should see the last 4 bytes: [2, 3, 4, 5]
        var result = buffer.ToArray();
        Assert.Equal(new byte[] { 2, 3, 4, 5 }, result);
    }

    [Fact]
    public void Multiple_Writes_Accumulate()
    {
        var buffer = new OutputRingBuffer(1024);
        buffer.Write(new byte[] { 10, 20 });
        buffer.Write(new byte[] { 30, 40 });

        Assert.Equal(new byte[] { 10, 20, 30, 40 }, buffer.ToArray());
    }

    [Fact]
    public void Exact_Capacity_Fill()
    {
        var buffer = new OutputRingBuffer(3);
        buffer.Write(new byte[] { 1, 2, 3 });

        Assert.Equal(new byte[] { 1, 2, 3 }, buffer.ToArray());
    }

    [Fact]
    public void Large_Overflow_Keeps_Last_N_Bytes()
    {
        var buffer = new OutputRingBuffer(4);
        buffer.Write(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 });

        Assert.Equal(new byte[] { 7, 8, 9, 10 }, buffer.ToArray());
    }

    [Fact]
    public void Thread_Safety_Concurrent_Writes()
    {
        var buffer = new OutputRingBuffer(8192);
        var tasks = new Task[10];

        for (int i = 0; i < 10; i++)
        {
            var data = new byte[100];
            Array.Fill(data, (byte)i);
            tasks[i] = Task.Run(() => buffer.Write(data));
        }

        Task.WaitAll(tasks);

        var result = buffer.ToArray();
        Assert.Equal(1000, result.Length);
    }
}
