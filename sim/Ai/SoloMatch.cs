using System.Text.Json;
using System.Text.Json.Serialization;
using Planetfall.Sim.Core;
using Planetfall.Sim.Data;
using Planetfall.Sim.MapGen;
using Planetfall.Sim.Systems;
using Planetfall.Sim.World;

namespace Planetfall.Sim.Ai;

/// <summary>One logged human command and the tick it was applied on.</summary>
public sealed record LoggedCommand(int Tick, Command Command);

/// <summary>
/// Everything needed to rebuild a single-player match exactly: the seed and map size, the human's commands, and
/// the state hash at the moment of saving (checked on load, so a save from a different sim version is refused
/// instead of silently diverging).
/// </summary>
public sealed record SaveGame(int Version, int GeneratorVersion, ulong Seed, int Cols, int Rows, int Tick, ulong Hash, List<LoggedCommand> Commands);

public sealed class SaveException(string message) : Exception(message);

/// <summary>
/// A human (player 0) against one RivalAi (player 1) on a Standard map, with player 0's fog tracked. The AI is a pure
/// function of the state, so the human's command log is the whole save: replaying it reproduces the match bit for bit.
/// </summary>
public sealed class SoloMatch
{
    public const int SaveVersion = 1;
    public const int Human = 0;

    private static readonly Leader Commander = Leader.Parse("""{ "id": "commander", "name": "Field Commander" }""");

    private readonly List<LoggedCommand> _log = new();

    public GameState State { get; }
    public RivalAi Ai { get; }
    public ulong Seed { get; }
    public int Cols { get; }
    public int Rows { get; }
    public Fog Fog => State.Fogs[Human];
    public IReadOnlyList<LoggedCommand> Log => _log;

    public SoloMatch(ulong seed, int cols = AiMatch.StandardCols, int rows = AiMatch.StandardRows)
    {
        Seed = seed; Cols = cols; Rows = rows;
        var map = MapGenerator.Generate(seed, cols, rows);
        State = GameState.NewWithMinors(map, Rules.MinorStrength, Commander, Commander);
        State.Players[0].AutoRecruit = false;
        State.Players[1].AutoRecruit = false;
        Ai = new RivalAi(1, Personality.ForSeed(seed, 1));
        State.Players[1].PersonalityId = Ai.Personality.Id;
        State.TrackFog(Human);
    }

    /// <summary>Advances one tick with the human's commands plus whatever the rival decides.</summary>
    public List<SimEvent> Step(IReadOnlyList<Command> human)
    {
        foreach (var c in human) _log.Add(new LoggedCommand(State.Tick, c));
        var all = new List<Command>(human);
        all.AddRange(Ai.Think(State));
        return Simulation.Step(State, all);
    }

    public SaveGame ToSave() =>
        new(SaveVersion, MapData.GeneratorVersion, Seed, Cols, Rows, State.Tick, State.Hash(), new List<LoggedCommand>(_log));

    /// <summary>Rebuilds the match by replaying the log. Throws <see cref="SaveException"/> if the save does not fit this build.</summary>
    public static SoloMatch Load(SaveGame save)
    {
        if (save.Version != SaveVersion) throw new SaveException($"Save version {save.Version} is not supported (this build reads {SaveVersion}).");
        if (save.GeneratorVersion != MapData.GeneratorVersion) throw new SaveException("This save was made with a different map generator.");
        var m = new SoloMatch(save.Seed, save.Cols, save.Rows);
        int next = 0;
        var none = new List<Command>();
        while (m.State.Tick < save.Tick)
        {
            var now = new List<Command>();
            while (next < save.Commands.Count && save.Commands[next].Tick == m.State.Tick) now.Add(save.Commands[next++].Command);
            m.Step(now);
        }
        if (m.State.Hash() != save.Hash) throw new SaveException("The save no longer replays to the same state (the game was updated since it was made).");
        return m;
    }

    // ---- file format ----------------------------------------------------------------------

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
        IncludeFields = false,
    };

    public static string ToJson(SaveGame save) => JsonSerializer.Serialize(save, Json);

    public static SaveGame FromJson(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<SaveGame>(json, Json) ?? throw new SaveException("The save file is empty.");
        }
        catch (JsonException e)
        {
            throw new SaveException("The save file is damaged: " + e.Message);
        }
    }

    /// <summary>Writes to a temporary file first, then swaps it in, so a crash mid-write never destroys the old save.</summary>
    public static void WriteFile(string path, SaveGame save)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, ToJson(save));
        if (File.Exists(path)) File.Replace(tmp, path, null);
        else File.Move(tmp, path);
    }

    public static SaveGame ReadFile(string path) => FromJson(File.ReadAllText(path));
}
