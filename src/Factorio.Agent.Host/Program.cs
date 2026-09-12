using System.Globalization;
using System.Text.Json;
using Factorio.Agent.Core;
using Factorio.Agent.Host;
using Factorio.Agent.Infrastructure;
using Factorio.Agent.Ollama;

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };
try
{
    if (args.Length == 0) throw new ArgumentException("""
        Commands:
          start [--fixture] [--seed N] [--root PATH] [--installation PATH]
          resume --session FILE [--root PATH]
          observe --session FILE
          factory --session FILE [--capacity-items item1,item2]
          spatial --session FILE [--items item1,item2]
          navigate --session FILE --x N --y N [--distance N]
          build --session FILE --item NAME --x N --y N
          produce --session FILE --item NAME --quantity N
          automate-smelting --session FILE --item NAME --quantity N
          run-goal --session FILE
          steam-power --session FILE [--plan FILE]
          research-plan --session FILE --technology NAME
          prepare-research --session FILE --technology NAME
          research --session FILE --technology NAME
          rpc --session FILE --action ACTION [--json-file FILE]
          connect --session FILE
          submit --session FILE --kind KIND --json-file FILE [--ticks N]
          defend --session FILE [--seconds N]
          verify-native|verify-defense|verify-factory|verify-spatial|verify-crafting --session FILE
          verify-pilot --session FILE --phase manual|ai|standalone
          stop --session FILE
        """);
    var options = Parse(args[1..]);
    string? Option(string name) => options.GetValueOrDefault(name);
    string Required(string name) => Option(name) ?? throw new ArgumentException($"Missing --{name}.");
    switch (args[0])
    {
        case "start":
        {
            var session = await FactorioRuntime.StartAsync(Option("root") ?? Environment.CurrentDirectory, Option("installation"),
                uint.Parse(Option("seed") ?? "424242", CultureInfo.InvariantCulture), options.ContainsKey("fixture"), shutdown.Token);
            Print(new { session.ManifestPath, session.ServerProcessId, session.GamePort, session.RconPort, session.Seed, session.IsFixture });
            break;
        }
        case "resume":
        {
            var prior = await RuntimeSession.ReadAsync(Required("session"), shutdown.Token);
            var session = await FactorioRuntime.ResumeAsync(prior, Option("root") ?? Environment.CurrentDirectory, shutdown.Token);
            Print(new { session.ManifestPath, session.ServerProcessId, session.GamePort, session.RconPort, session.Seed, session.IsFixture });
            break;
        }
        case "connect":
        {
            var session = await RuntimeSession.ReadAsync(Required("session"), shutdown.Token);
            Print(new { clientProcessId = await FactorioRuntime.ConnectClientAsync(session, shutdown.Token) });
            break;
        }
        case "research":
        {
            var session = await RuntimeSession.ReadAsync(Required("session"), shutdown.Token);
            using var lease = ActorControlLease.Acquire(session.Directory);
            string journalPath = Path.Combine(session.Directory, $"laboratory-{Guid.NewGuid():N}.jsonl");
            var controller = new LaboratoryController(session.CreateClient(lease), new ControllerJournal(journalPath));
            Print(new { result = await controller.RunAsync(Required("technology"), shutdown.Token), journalPath });
            break;
        }
        case "verify-crafting":
        {
            var session = await RuntimeSession.ReadAsync(Required("session"), shutdown.Token);
            Print(new { report = await new CraftingQualification(session).RunAsync(shutdown.Token) });
            break;
        }
        case "verify-native":
        {
            var session = await RuntimeSession.ReadAsync(Required("session"), shutdown.Token);
            Print(new { report = await new NativeQualification(session).RunAsync(shutdown.Token) });
            break;
        }
        case "verify-pilot":
        {
            var session = await RuntimeSession.ReadAsync(Required("session"), shutdown.Token);
            Print(new { report = await new PilotQualification(session).RunPhaseAsync(Required("phase"), shutdown.Token) });
            break;
        }
        case "defend":
        {
            var session = await RuntimeSession.ReadAsync(Required("session"), shutdown.Token);
            using var lease = ActorControlLease.Acquire(session.Directory);
            string journalPath = Path.Combine(session.Directory, $"defense-{Guid.NewGuid():N}.jsonl");
            var controller = new DefenseController(session.CreateClient(lease), new ControllerJournal(journalPath));
            await controller.RunAsync(TimeSpan.FromSeconds(int.Parse(Option("seconds") ?? "60", CultureInfo.InvariantCulture)), shutdown.Token);
            Print(new { journal = journalPath });
            break;
        }
        case "factory":
        {
            var session = await RuntimeSession.ReadAsync(Required("session"), shutdown.Token);
            string[]? capacityItems = Option("capacity-items")?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            FactorySnapshot snapshot = await new FactorySnapshotClient(session.CreateClient()).CaptureAsync(capacityItems,
                cancellationToken: shutdown.Token);
            string output = Path.Combine(session.Directory, $"factory-{Guid.NewGuid():N}.json");
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(snapshot, Protocol.Json), shutdown.Token);
            Print(new { snapshot.SnapshotId, snapshot.CollectedTick, recordCount = snapshot.Records.Count,
                coverage = snapshot.Coverage, stocks = snapshot.SummarizeStocks(), output });
            break;
        }
        case "verify-defense":
        {
            var session = await RuntimeSession.ReadAsync(Required("session"), shutdown.Token);
            Print(new { report = await new DefenseQualification(session).RunAsync(shutdown.Token) });
            break;
        }
        case "verify-factory":
        {
            var session = await RuntimeSession.ReadAsync(Required("session"), shutdown.Token);
            Print(new { report = await new FactoryQualification(session).RunAsync(shutdown.Token) });
            break;
        }
        case "prepare-research":
        {
            var session = await RuntimeSession.ReadAsync(Required("session"), shutdown.Token);
            using var lease = ActorControlLease.Acquire(session.Directory);
            string journalPath = Path.Combine(session.Directory, $"research-preparation-{Guid.NewGuid():N}.jsonl");
            var game = session.CreateClient(lease);
            var journal = new ControllerJournal(journalPath);
            var controller = new ResearchPrerequisiteController(game, new ProductionGoalExecutor(game, journal), journal);
            Print(new { result = await controller.RunAsync(Required("technology"), shutdown.Token), journalPath });
            break;
        }
        case "research-plan":
        {
            var session = await RuntimeSession.ReadAsync(Required("session"), shutdown.Token);
            string technology = Required("technology");
            TechnologyObservation observation = await new TechnologyClient(session.CreateClient())
                .ReadDependenciesAsync(technology, shutdown.Token);
            TechnologyStep next = new TechnologyPlanner().Next(technology, observation.Technologies);
            string reportPath = Path.Combine(session.Directory, $"research-plan-{Guid.NewGuid():N}.json");
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new { observation, next }, Protocol.Json), shutdown.Token);
            Print(new { next, observation.StartTick, observation.EndTick, reportPath });
            break;
        }
        case "steam-power":
        {
            var session = await RuntimeSession.ReadAsync(Required("session"), shutdown.Token);
            using var lease = ActorControlLease.Acquire(session.Directory);
            string journalPath = Path.Combine(session.Directory, $"steam-power-{Guid.NewGuid():N}.jsonl");
            var controller = new SteamPowerController(session.CreateClient(lease), new ControllerJournal(journalPath));
            SteamPowerPlan? resume = Option("plan") is { } planPath
                ? JsonSerializer.Deserialize<SteamPowerPlan>(await File.ReadAllTextAsync(planPath, shutdown.Token), Protocol.Json)
                    ?? throw new InvalidDataException("Missing power recovery plan.") : null;
            Print(new { result = await controller.RunAsync(shutdown.Token, resume), journalPath });
            break;
        }
        case "run-goal":
        {
            var session = await RuntimeSession.ReadAsync(Required("session"), shutdown.Token);
            using var lease = ActorControlLease.Acquire(session.Directory);
            string journalPath = Path.Combine(session.Directory, $"strategic-production-{Guid.NewGuid():N}.jsonl");
            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            var planner = new OllamaStrategicPlanner(http, new OllamaOptions { MaxAttempts = 1 });
            var controller = new StrategicProductionController(session.CreateClient(lease), planner, new ControllerJournal(journalPath));
            StockGoalResult result = await controller.RunOnceAsync(shutdown.Token);
            Print(new { result.Method, result.Item, result.TargetStock, result.InitialStock, result.FinalStock, result.StartTick, result.EndTick, journalPath });
            break;
        }
        case "automate-smelting":
        {
            var session = await RuntimeSession.ReadAsync(Required("session"), shutdown.Token);
            using var lease = ActorControlLease.Acquire(session.Directory);
            string journalPath = Path.Combine(session.Directory, $"automated-smelting-{Guid.NewGuid():N}.jsonl");
            var controller = new AutomatedSmeltingController(session.CreateClient(lease), new ControllerJournal(journalPath));
            var result = await controller.RunAsync(Required("item"), int.Parse(Required("quantity"), CultureInfo.InvariantCulture), shutdown.Token);
            Print(new { result, journalPath });
            break;
        }
        case "produce":
        {
            var session = await RuntimeSession.ReadAsync(Required("session"), shutdown.Token);
            using var lease = ActorControlLease.Acquire(session.Directory);
            string journalPath = Path.Combine(session.Directory, $"production-{Guid.NewGuid():N}.jsonl");
            var controller = new ProductionGoalExecutor(session.CreateClient(lease), new ControllerJournal(journalPath));
            StockGoalResult result = await controller.RunAsync(Required("item"),
                int.Parse(Required("quantity"), CultureInfo.InvariantCulture), shutdown.Token);
            Print(new { result.Method, result.Item, result.TargetStock, result.InitialStock, result.FinalStock,
                result.StartTick, result.EndTick, journalPath });
            break;
        }
        case "spatial":
        {
            var session = await RuntimeSession.ReadAsync(Required("session"), shutdown.Token);
            string[]? items = Option("items")?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            SpatialSnapshot map = await new SpatialClient(session.CreateClient()).CaptureAsync(items, cancellationToken: shutdown.Token);
            string output = Path.Combine(session.Directory, $"spatial-{Guid.NewGuid():N}.json");
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(map, Protocol.Json), shutdown.Token);
            Print(new { map.CollectedTick, map.Bounds, map.Actor, entityCount = map.Entities.Count, output });
            break;
        }
        case "navigate":
        case "build":
        {
            var session = await RuntimeSession.ReadAsync(Required("session"), shutdown.Token);
            using var lease = ActorControlLease.Acquire(session.Directory);
            string journalPath = Path.Combine(session.Directory, $"spatial-operations-{Guid.NewGuid():N}.jsonl");
            await using var controller = new SpatialController(session.CreateClient(lease), new ControllerJournal(journalPath));
            var target = new MapPosition(double.Parse(Required("x"), CultureInfo.InvariantCulture),
                double.Parse(Required("y"), CultureInfo.InvariantCulture));
            if (args[0] == "navigate")
            {
                NavigationResult result = await controller.NavigateAsync(target,
                    double.Parse(Option("distance") ?? "0.4", CultureInfo.InvariantCulture), shutdown.Token);
                Print(new { result.Position, result.Plans, operations = result.Receipts.Count, journalPath });
            }
            else Print(await controller.BuildAsync(Required("item"), target, shutdown.Token));
            break;
        }
        case "verify-spatial":
        {
            var session = await RuntimeSession.ReadAsync(Required("session"), shutdown.Token);
            Print(new { report = await new SpatialQualification(session).RunAsync(shutdown.Token) });
            break;
        }
        case "stop":
        {
            var session = await RuntimeSession.ReadAsync(Required("session"), shutdown.Token);
            Print(new { checkpoint = await FactorioRuntime.StopAsync(session, shutdown.Token) });
            break;
        }
        case "observe":
        case "rpc":
        {
            var session = await RuntimeSession.ReadAsync(Required("session"), shutdown.Token);
            string action = args[0] == "observe" ? "observe" : Required("action");
            using JsonDocument json = JsonDocument.Parse(await JsonInput(options, shutdown.Token));
            Print(await session.CreateClient().ExecuteAsync(GameRequest.Create(action, json.RootElement), shutdown.Token));
            break;
        }
        case "submit":
        {
            var session = await RuntimeSession.ReadAsync(Required("session"), shutdown.Token);
            var game = session.CreateClient();
            GameResponse hello = await session.HelloAsync(shutdown.Token);
            var scope = JsonSerializer.Deserialize<ActorScope>(hello.Data.GetProperty("scope"), Protocol.Json)!;
            using JsonDocument json = JsonDocument.Parse(await JsonInput(options, shutdown.Token));
            long ticks = long.Parse(Option("ticks") ?? "1800", CultureInfo.InvariantCulture);
            if (ticks is < 1 or > 216000) throw new ArgumentOutOfRangeException("ticks", "Operation deadline must be between 1 and 216000 ticks.");
            var submission = OperationSubmission.Create(scope, Required("kind"), json.RootElement, hello.Tick + ticks);
            // Persist intent before a command can reach the engine, including for an ambiguous outcome.
            string journal = Path.Combine(session.Directory, "host-operations.jsonl");
            await File.AppendAllTextAsync(journal, JsonSerializer.Serialize(new { type = "submission", submission }, Protocol.Json) + "\n", shutdown.Token);
            var client = new OperationClient(game);
            var receipt = await client.SubmitAsync(submission, shutdown.Token);
            Print(receipt);
            if (!receipt.IsTerminal) receipt = await client.WaitAsync(submission.OperationId, TimeSpan.FromSeconds(ticks / 60.0 + 15), shutdown.Token);
            await File.AppendAllTextAsync(journal, JsonSerializer.Serialize(new { type = "receipt", receipt }, Protocol.Json) + "\n", shutdown.Token);
            Print(receipt);
            if (receipt.Status != "completed") Environment.ExitCode = 2;
            break;
        }
        default:
            throw new ArgumentException($"Unknown command: {args[0]}");
    }
}
catch (Exception error) when (error is not OutOfMemoryException)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new { error = error.GetType().Name, message = error.Message }));
    Environment.ExitCode = 1;
}

static Dictionary<string, string> Parse(string[] values)
{
    var options = new Dictionary<string, string>(StringComparer.Ordinal);
    for (int index = 0; index < values.Length; index++)
    {
        string value = values[index];
        if (!value.StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"Expected option, got {value}.");
        string name = value[2..];
        string contents = name == "fixture" ? "true" : ++index < values.Length ? values[index] : throw new ArgumentException($"Missing value for {value}.");
        if (!options.TryAdd(name, contents)) throw new ArgumentException($"Duplicate option {value}.");
    }
    return options;
}

static void Print<T>(T value) => Console.WriteLine(JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));

static async Task<string> JsonInput(Dictionary<string, string> options, CancellationToken token)
{
    if (options.TryGetValue("json-file", out string? path))
    {
        if (options.ContainsKey("json")) throw new ArgumentException("Use either --json-file or --json, not both.");
        return await File.ReadAllTextAsync(path, token);
    }
    return options.GetValueOrDefault("json") ?? "{}";
}
