# Planetfall

Real-time grand strategy on a procedurally generated planet. See [docs/PLAN.md](docs/PLAN.md).

## Layout
- `sim/` — pure C# rules library (no Godot references), .NET 8
- `sim.tests/` — xUnit tests (`dotnet test sim.tests`)
- `godot/` — Godot 4.7.2 .NET project (presentation only)
- `docs/PLAN.md` — the implementation plan

## Toolchain (pinned)
- Godot **4.7.2 Mono** (`winget install GodotEngine.GodotEngine.Mono`)
- .NET SDK **8.0.x** (see `global.json`)

## Run
```
dotnet test sim.tests
godot --path godot          # or open godot/project.godot in the editor
```

macOS builds are unsigned until M9; open with right-click → Open.
