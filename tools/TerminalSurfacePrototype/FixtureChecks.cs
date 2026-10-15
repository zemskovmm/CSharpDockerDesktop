using System.Text;
using CSharpDockerDesktop.Domain;

internal static class FixtureChecks
{
    public static async Task RunAsync()
    {
        var fixture = new ControlledTerminal();
        var first = fixture.OpenContainerTerminal("controlled", [], 80, 24);
        var producer = fixture.ProduceAsync(false);
        var bytes = new List<byte>();
        while (bytes.Count < 189)
        {
            var output = await fixture.ReadContainerTerminalOutputAsync(first.SessionId).WaitAsync(TimeSpan.FromSeconds(2));
            Require(!output.Revoked && output.Bytes.Length <= 4096);
            bytes.AddRange(output.Bytes);
        }
        await producer.WaitAsync(TimeSpan.FromSeconds(2));
        Require(Encoding.UTF8.GetString(bytes.ToArray()).StartsWith("UTF-8: café λ 世界 🐳\r\n\x1b[31mRED\x1b[0m"));
        var input = await fixture.WriteContainerTerminalAsync(first.SessionId, new byte[] { 3 });
        Require(input.Disposition == ContainerExecWriteDisposition.Written && fixture.ControlCCount == 1);
        var resize = await fixture.ResizeContainerTerminalAsync(first.SessionId, 100, 30);
        Require(resize.Disposition == ContainerExecResizeDisposition.Acknowledged && fixture.LastColumns == 100 && fixture.LastRows == 30);
        await fixture.ReadContainerTerminalOutputAsync(first.SessionId); // Consume echoed control byte.
        var pending = fixture.ReadContainerTerminalOutputAsync(first.SessionId);
        await fixture.CloseContainerTerminalAsync();
        Require((await pending.WaitAsync(TimeSpan.FromSeconds(2))).Revoked);
        await fixture.CloseContainerTerminalAsync();
        var second = fixture.OpenContainerTerminal("controlled", [], 80, 24);
        Require(second.Accepted && second.SessionId > first.SessionId);
        producer = fixture.ProduceAsync(true);
        await Task.Delay(50);
        Require(fixture.PendingChunks == 4 && !producer.IsCompleted);
        await fixture.CloseContainerTerminalAsync();
        await producer.WaitAsync(TimeSpan.FromSeconds(2));
        Require(!fixture.IsContainerTerminalCurrent(first.SessionId, "controlled", 1));
        Console.WriteLine("Controlled fixture checks passed: raw bytes, input 0x03, resize, pending-read revocation, repeated close/reopen, bounded backpressure and joined producer.");
    }
    private static void Require(bool value)
    {
        if (!value) throw new InvalidOperationException("Controlled terminal fixture check failed (payload omitted).");
    }
}
