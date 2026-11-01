using Godot;
using Planetfall.Sim.Core;

namespace Planetfall;

public partial class Main : Node
{
    public override void _Ready()
    {
        GD.Print($"Planetfall sim {SimInfo.Version}");
    }
}
