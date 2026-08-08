using Yaap.Cli;

using CancellationTokenSource cancellation = new();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

return await CliApplication.RunAsync(args, Console.Out, Console.Error, cancellation.Token);
