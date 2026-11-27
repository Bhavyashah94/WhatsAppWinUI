using WhatsApp.Cli;

Console.InputEncoding = System.Text.Encoding.UTF8;
Console.OutputEncoding = System.Text.Encoding.UTF8;

CliOptions options;
try
{
    options = CliOptions.Parse(args);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Argument error: {ex.Message}");
    return 1;
}

using var shutdownCts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    shutdownCts.Cancel();
};

await using CliRuntime runtime = await CliRuntime.CreateAndInitializeAsync(options, ct: shutdownCts.Token);
var dispatcher = new CommandDispatcher(runtime);

if (!string.IsNullOrWhiteSpace(options.SingleCommand))
{
    CommandResult result = await dispatcher.ExecuteAsync(options.SingleCommand, shutdownCts.Token);
    return result.ExitCode;
}

Console.WriteLine($"WhatsApp.Cli ready (profile='{options.Profile}', db='{options.ResolveDatabasePath()}'). Type 'help' for commands.");
while (!shutdownCts.IsCancellationRequested)
{
    Console.Write("wa> ");
    string? line = Console.ReadLine();
    if (line is null)
    {
        break;
    }
    CommandResult res = await dispatcher.ExecuteAsync(line, shutdownCts.Token);
    if (res.ShouldExit)
    {
        break;
    }
}

return 0;
