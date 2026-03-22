using DcKookBot;

var config = RelayConfig.FromEnvironment();
using var cts = new CancellationTokenSource();

Console.CancelKeyPress += (_, e) =>
{
	e.Cancel = true;
	cts.Cancel();
};

var relay = new RelayService(config);
await relay.StartAsync(cts.Token);
await relay.WaitForShutdownAsync(cts.Token);
