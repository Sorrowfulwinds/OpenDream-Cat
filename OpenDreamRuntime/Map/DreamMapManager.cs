using System.Diagnostics.CodeAnalysis;
using System.Linq;
using DMCompiler.Json;
using OpenDreamRuntime.Objects;
using OpenDreamRuntime.Objects.Types;
using OpenDreamRuntime.Procs;
using OpenDreamRuntime.Rendering;
using OpenDreamShared.Dream;
using Robust.Server.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Utility;
using Level = OpenDreamRuntime.Map.IDreamMapManager.Level;
using Cell = OpenDreamRuntime.Map.IDreamMapManager.Cell;

namespace OpenDreamRuntime.Map;

public sealed partial class DreamMapManager : IDreamMapManager {
    private readonly Dictionary<MapObjectJson, DreamObjectArea> _areas = new();
    private readonly Dictionary<DreamObjectDefinition, ImmutableAppearance> _defaultTurfAppearanceCache = new();
    private readonly HashSet<EntityUid> _entityLookupSet = new();

    private readonly List<Level> _levels = new();

    /// <summary>
    ///     Caches the turf/area appearance pair instead of recreating and re-registering it for every turf in the game.
    ///     This is cleared out when an area appearance changes
    /// </summary>
    private readonly Dictionary<ValueTuple<ImmutableAppearance, uint>, ImmutableAppearance> _turfAreaLookup = new();

    // Set in Initialize
    private ServerAppearanceSystem _appearanceSystem = default!;
    [Dependency] private AtomManager _atomManager = default!;

    // Set in Initialize
    private MapObjectJson _defaultArea = default!;
    private MapObjectJson _defaultTurf = default!;
    [Dependency] private DreamManager _dreamManager = default!;
    [Dependency] private IEntitySystemManager _entitySystemManager = default!;

    private List<DreamMapJson>? _jsonMaps = new();
    private EntityLookupSystem _lookupSystem = default!;
    private SharedMapSystem _mapSystem = default!;
    [Dependency] private DreamObjectTree _objectTree = default!;

    public Vector2i Size { get; private set; }
    public int Levels => _levels.Count;

    public DreamObjectArea DefaultArea => GetOrCreateArea(_defaultArea);

    public void Initialize() {
        _appearanceSystem = _entitySystemManager.GetEntitySystem<ServerAppearanceSystem>();
        _mapSystem = _entitySystemManager.GetEntitySystem<MapSystem>();
        _lookupSystem = _entitySystemManager.GetEntitySystem<EntityLookupSystem>();

        DreamObjectDefinition worldDefinition = _objectTree.World.ObjectDefinition;

        // Default area
        DreamValue defaultArea = worldDefinition.Variables["area"];
        if (!defaultArea.TryGetValueAsType(out TreeEntry? defaultAreaValue) &&
            defaultArea.TryGetValueAsFloatCoerceNull(out float areaInt) &&
            areaInt == 0) //TODO: Properly handle disabling default area
            defaultAreaValue = _objectTree.Area;
        if (defaultAreaValue?.ObjectDefinition.IsSubtypeOf(_objectTree.Area) is not true)
            throw new Exception("bad area");

        //Default turf
        DreamValue defaultTurf = worldDefinition.Variables["turf"];
        if (!defaultTurf.TryGetValueAsType(out TreeEntry? defaultTurfValue) &&
            defaultTurf.TryGetValueAsFloatCoerceNull(out float turfInt) &&
            turfInt == 0) //TODO: Properly handle disabling default turf
            defaultTurfValue = _objectTree.Turf;
        if (defaultTurfValue?.ObjectDefinition.IsSubtypeOf(_objectTree.Turf) is not true)
            throw new Exception("bad turf");

        _defaultArea = new MapObjectJson(defaultAreaValue.Id);
        _defaultTurf = new MapObjectJson(defaultTurfValue.Id);
    }

    public void UpdateTiles() {
        foreach (Level level in _levels) {
            if (level.QueuedTileUpdates.Count == 0)
                continue;

            List<(Vector2i, Tile)> tiles = new(level.QueuedTileUpdates.Count);
            foreach ((Vector2i, Tile) tileUpdate in level.QueuedTileUpdates) tiles.Add(tileUpdate);

            _mapSystem.SetTiles(level.Grid, tiles);
            level.QueuedTileUpdates.Clear();
        }
    }

    public void LoadMaps(List<DreamMapJson>? maps) {
        DreamObjectWorld world = _dreamManager.WorldInstance;
        var maxX = (int)world.ObjectDefinition.Variables["maxx"].UnsafeGetValueAsFloat();
        var maxY = (int)world.ObjectDefinition.Variables["maxy"].UnsafeGetValueAsFloat();
        var maxZ = (int)world.ObjectDefinition.Variables["maxz"].UnsafeGetValueAsFloat();

        if (maps != null)
            foreach (DreamMapJson map in maps) {
                maxX = Math.Max(maxX, map.MaxX);
                maxY = Math.Max(maxY, map.MaxY);
                maxZ = Math.Max(maxZ, map.MaxZ);
            }

        Size = new Vector2i(maxX, maxY);
        SetZLevels(maxZ);

        if (maps != null) {
            _jsonMaps = maps;

            // Load turfs and areas of compiled-in maps, recursively calling <init>, but suppressing all New
            foreach (DreamMapJson map in maps)
            foreach (MapBlockJson block in map.Blocks)
                LoadMapAreasAndTurfs(block, map.CellDefinitions);
        }
    }

    public void InitializeAtoms() {
        // Call New() on all /area in this particular order, each with waitfor=FALSE
        var seenAreas = new HashSet<DreamObject>();
        for (var z = 1; z <= Levels; ++z)
        for (var y = 1; y <= Size.Y; ++y)
        for (var x = 1; x <= Size.X; ++x) {
            DreamObjectArea area = _levels[z - 1].Cells[x - 1, y - 1].Area;
            if (seenAreas.Add(area)) area.SpawnProc("New").Dispose();
        }

        // Also call New() on all /area not in the grid.
        // This may call New() a SECOND TIME. This is intentional.
        foreach (DreamObjectAtom thing in _atomManager.EnumerateAtoms(_objectTree.Area))
            if (seenAreas.Add(thing))
                thing.SpawnProc("New").Dispose();

        // Call New() on all /turf in the grid, each with waitfor=FALSE
        for (var z = 1; z <= Levels; ++z)
        for (int y = Size.Y; y >= 1; --y)
        for (int x = Size.X; x >= 1; --x)
            _levels[z - 1].Cells[x - 1, y - 1].Turf.SpawnProc("New").Dispose();

        if (_jsonMaps != null) {
            // new() up /objs and /mobs from compiled-in maps
            foreach (DreamMapJson map in _jsonMaps)
            foreach (MapBlockJson block in map.Blocks)
                LoadMapObjectsAndMobs(block, map.CellDefinitions);

            // No longer needed
            _jsonMaps = null;
        }
    }

    public void SetTurf(DreamObjectTurf turf, DreamObjectDefinition type, DreamProcArguments creationArguments) {
        SetTurf((turf.X, turf.Y), turf.Z, type, creationArguments);
    }

    public void SetTurfAppearance(DreamObjectTurf turf, ImmutableAppearance appearance) {
        appearance.EnabledMouseEvents = _atomManager.GetEnabledMouseEvents(turf);

        if (turf.Cell.Area.Appearance != _appearanceSystem.DefaultAppearance)
            if (!appearance.Overlays.Contains(turf.Cell.Area.Appearance)) {
                if (!_turfAreaLookup.TryGetValue((appearance, turf.Cell.Area.Appearance.MustGetId()),
                        out ImmutableAppearance? newAppearance)) {
                    MutableAppearance mutable = appearance.ToMutable();

                    mutable.Overlays.Add(turf.Cell.Area.Appearance);
                    newAppearance = _appearanceSystem.AddAppearance(mutable);
                    _turfAreaLookup.Add((appearance, turf.Cell.Area.Appearance.MustGetId()), newAppearance);
                }

                appearance = newAppearance;
            }

        Level level = _levels[turf.Z - 1];
        uint turfId = appearance.MustGetId();
        var turfPos = new Vector2i(turf.X, turf.Y);
        level.QueuedTileUpdates.Add((turfPos, new Tile((int)turfId)));
        turf.Appearance = appearance;
    }

    public void SetTurfAppearance(DreamObjectTurf turf, MutableAppearance appearance) {
        ImmutableAppearance immutable = _appearanceSystem.AddAppearance(appearance);

        SetTurfAppearance(turf, immutable);
    }

    public void SetAreaAppearance(DreamObjectArea area, MutableAppearance appearance) {
        //if an area changes appearance, invalidate the lookup
        _turfAreaLookup.Clear();
        ImmutableAppearance oldAppearance = area.Appearance;
        appearance.AppearanceFlags |=
            AppearanceFlags.ResetColor | AppearanceFlags.ResetAlpha | AppearanceFlags.ResetTransform;
        area.Appearance = _appearanceSystem.AddAppearance(appearance);

        //get all unique turf appearances
        //create the new version of each of those appearances
        //for each turf, update the appropriate ID

        Dictionary<ImmutableAppearance, ImmutableAppearance> oldToNewAppearance = new();
        foreach (DreamObjectTurf turf in area.Turfs) {
            if (oldToNewAppearance.TryGetValue(turf.Appearance, out ImmutableAppearance? newAppearance)) {
                turf.Appearance = newAppearance;
            } else {
                MutableAppearance turfAppearance = _atomManager.MustGetAppearance(turf).ToMutable();

                turfAppearance.Overlays.Remove(oldAppearance);
                turfAppearance.Overlays.Add(area.Appearance);
                newAppearance = _appearanceSystem.AddAppearance(turfAppearance);
                oldToNewAppearance.Add(turf.Appearance, newAppearance);
                turf.Appearance = newAppearance;
            }

            Level level = _levels[turf.Z - 1];
            uint turfId = newAppearance.MustGetId();
            var turfPos = new Vector2i(turf.X, turf.Y);
            level.QueuedTileUpdates.Add((turfPos, new Tile((int)turfId)));
        }
    }

    public bool TryGetCellAt(Vector2i pos, int z, [NotNullWhen(true)] out Cell? cell) {
        if (IsInvalidCoordinate(pos, z) || !_levels.TryGetValue(z - 1, out Level level)) {
            cell = null;
            return false;
        }

        cell = level.Cells[pos.X - 1, pos.Y - 1];
        return true;
    }

    public bool TryGetTurfAt(Vector2i pos, int z, [NotNullWhen(true)] out DreamObjectTurf? turf) {
        if (TryGetCellAt(pos, z, out Cell? cell)) {
            turf = cell.Turf;
            return true;
        }

        turf = null;
        return false;
    }

    public void SetWorldSize(Vector2i size) {
        Vector2i oldSize = Size;

        int newX = Math.Max(oldSize.X, size.X);
        int newY = Math.Max(oldSize.Y, size.Y);

        Size = (newX, newY);

        if (Size.X > oldSize.X || Size.Y > oldSize.Y)
            foreach (Level existingLevel in _levels) {
                Cell[,] oldCells = existingLevel.Cells;

                existingLevel.Cells = new Cell[Size.X, Size.Y];
                for (var x = 1; x <= Size.X; x++)
                for (var y = 1; y <= Size.Y; y++) {
                    if (x <= oldSize.X && y <= oldSize.Y) {
                        existingLevel.Cells[x - 1, y - 1] = oldCells[x - 1, y - 1];
                        continue;
                    }

                    DreamObjectDefinition defaultTurfDef = _objectTree.GetTreeEntry(_defaultTurf.Type).ObjectDefinition;
                    var defaultTurf = new DreamObjectTurf(defaultTurfDef, x, y, existingLevel.Z);
                    var cell = new Cell(DefaultArea, defaultTurf);
                    defaultTurf.Cell = cell;
                    existingLevel.Cells[x - 1, y - 1] = cell;
                    SetTurf(new Vector2i(x, y), existingLevel.Z, defaultTurfDef, new DreamProcArguments());
                    defaultTurf.DecRef();
                }
            }

        if (Size.X > size.X || Size.Y > size.Y) {
            Size = size;

            foreach (Level existingLevel in _levels) {
                Cell[,] oldCells = existingLevel.Cells;

                existingLevel.Cells = new Cell[size.X, size.Y];
                for (var x = 1; x <= oldSize.X; x++)
                for (var y = 1; y <= oldSize.Y; y++)
                    if (x > size.X || y > size.Y) {
                        Cell deleteCell = oldCells[x - 1, y - 1];
                        deleteCell.Turf.DecRef();
                        deleteCell.Turf.Delete();
                        _mapSystem.SetTile(existingLevel.Grid, new Vector2i(x, y), Tile.Empty);
                        foreach (DreamObjectMovable movableToDelete in deleteCell.Movables) {
                            movableToDelete.DecRef();
                            movableToDelete.Delete();
                        }
                    } else {
                        existingLevel.Cells[x - 1, y - 1] = oldCells[x - 1, y - 1];
                    }
            }
        }
    }

    public void SetZLevels(int levels) {
        if (levels > Levels) {
            DreamObjectDefinition defaultTurfDef = _objectTree.GetTreeEntry(_defaultTurf.Type).ObjectDefinition;

            for (int z = Levels + 1; z <= levels; z++) {
                MapId mapId = new(z);
                _mapSystem.CreateMap(mapId);

                Entity<MapGridComponent> grid = _mapSystem.CreateGridEntity(mapId);
                var level = new Level(z, grid, defaultTurfDef, DefaultArea, Size);
                _levels.Add(level);

                for (var x = 1; x <= Size.X; x++)
                for (var y = 1; y <= Size.Y; y++) {
                    Vector2i pos = (x, y);

                    SetTurf(pos, z, defaultTurfDef, new DreamProcArguments());
                }
            }

            UpdateTiles();
        } else if (levels < Levels) {
            _levels.RemoveRange(levels, Levels - levels);
            for (int z = Levels; z > levels; z--) _mapSystem.DeleteMap(new MapId(z));
        }
    }

    public EntityUid GetZLevelEntity(int z) {
        return _levels[z - 1].Grid.Owner;
    }

    public IEnumerable<DreamObjectMob> GetMobsInRange((int X, int Y, int Z) loc, int distance) {
        _entityLookupSet.Clear();
        _lookupSystem.GetEntitiesInRange(new MapId(loc.Z), new Vector2(loc.X, loc.Y), distance, _entityLookupSet);

        foreach (EntityUid entity in _entityLookupSet) {
            if (!_atomManager.TryGetMovableFromEntity(entity, out DreamObjectMovable? movable))
                continue;
            if (movable is not DreamObjectMob mob)
                continue;

            yield return mob;
        }
    }

    private void SetTurf(Vector2i pos, int z, DreamObjectDefinition type, DreamProcArguments creationArguments) {
        if (IsInvalidCoordinate(pos, z))
            throw new ArgumentException("Invalid coordinates");

        Cell cell = _levels[z - 1].Cells[pos.X - 1, pos.Y - 1];

        cell.Turf.SetTurfType(type);

        if (!_defaultTurfAppearanceCache.TryGetValue(cell.Turf.ObjectDefinition, out ImmutableAppearance? appearance)) {
            MutableAppearance mutable = _atomManager.GetAppearanceFromDefinition(cell.Turf.ObjectDefinition);

            appearance = _appearanceSystem.AddAppearance(mutable);
            _defaultTurfAppearanceCache.Add(cell.Turf.ObjectDefinition, appearance);
        }

        SetTurfAppearance(cell.Turf, appearance);

        cell.Turf.InitSpawn(creationArguments);
    }

    //Returns an area loaded by a DMM
    //Does not include areas created by DM code
    private DreamObjectArea GetOrCreateArea(MapObjectJson prototype) {
        if (!_areas.TryGetValue(prototype, out DreamObjectArea? area)) {
            DreamObjectDefinition definition = CreateMapObjectDefinition(prototype);
            area = new DreamObjectArea(definition);
            area.InitSpawn(new DreamProcArguments());
            _areas.Add(prototype, area);
        }

        return area;
    }

    private bool IsInvalidCoordinate(Vector2i pos, int z) {
        return pos.X < 1 || pos.X > Size.X ||
               pos.Y < 1 || pos.Y > Size.Y ||
               z < 1 || z > Levels;
    }

    private void LoadMapAreasAndTurfs(MapBlockJson block, Dictionary<string, CellDefinitionJson> cellDefinitions) {
        var blockX = 1;
        var blockY = 1;

        // Order here doesn't really matter because it's not observable.
        foreach (string cell in block.Cells) {
            CellDefinitionJson cellDefinition = cellDefinitions[cell];
            DreamObjectArea area = GetOrCreateArea(cellDefinition.Area ?? _defaultArea);

            Vector2i pos = (block.X + blockX - 1, block.Y + block.Height - blockY);

            _levels[block.Z - 1].Cells[pos.X - 1, pos.Y - 1].Area = area;
            SetTurf(pos, block.Z, CreateMapObjectDefinition(cellDefinition.Turf ?? _defaultTurf),
                new DreamProcArguments());

            blockX++;
            if (blockX > block.Width) {
                blockX = 1;
                blockY++;
            }
        }
    }

    private void LoadMapObjectsAndMobs(MapBlockJson block, Dictionary<string, CellDefinitionJson> cellDefinitions) {
        // The order we call New() here should be (1,1), (2,1), (1,2), (2,2)
        int blockY = block.Y;
        foreach (string[] row in block.Cells.Chunk(block.Width).Reverse()) {
            int blockX = block.X;
            foreach (string cell in row) {
                CellDefinitionJson cellDefinition = cellDefinitions[cell];

                if (TryGetTurfAt((blockX, blockY), block.Z, out DreamObjectTurf? turf))
                    foreach (MapObjectJson mapObject in cellDefinition.Objects) {
                        DreamObjectDefinition objDef = CreateMapObjectDefinition(mapObject);

                        // TODO: Use modified types during compile so this hack isn't necessary
                        DreamObject obj;
                        if (objDef.IsSubtypeOf(_objectTree.Mob))
                            obj = new DreamObjectMob(objDef);
                        else if (objDef.IsSubtypeOf(_objectTree.Movable))
                            obj = new DreamObjectMovable(objDef);
                        else if (objDef.IsSubtypeOf(_objectTree.Atom))
                            obj = new DreamObjectAtom(objDef);
                        else
                            obj = new DreamObject(objDef);

                        obj.InitSpawn(new DreamProcArguments(new DreamValue(turf)));
                        obj.DecRef();
                    }

                ++blockX;
            }

            ++blockY;
        }
    }

    private DreamObjectDefinition CreateMapObjectDefinition(MapObjectJson mapObject) {
        DreamObjectDefinition definition = _objectTree.GetObjectDefinition(mapObject.Type);
        if (mapObject.VarOverrides?.Count > 0) {
            definition = new DreamObjectDefinition(definition);

            foreach (KeyValuePair<string, object> varOverride in mapObject.VarOverrides)
                if (definition.HasVariable(varOverride.Key)) {
                    using DreamValue overrideValue = _objectTree.GetDreamValueFromJsonElement(varOverride.Value);

                    definition.Variables[varOverride.Key] = overrideValue;
                    overrideValue.IncRef();
                }
        }

        return definition;
    }
}

public interface IDreamMapManager {
    Vector2i Size { get; }
    int Levels { get; }
    DreamObjectArea DefaultArea { get; }

    void Initialize();
    void LoadMaps(List<DreamMapJson>? maps);
    void InitializeAtoms();
    void UpdateTiles();

    void SetTurf(DreamObjectTurf turf, DreamObjectDefinition type, DreamProcArguments creationArguments);
    void SetTurfAppearance(DreamObjectTurf turf, ImmutableAppearance appearance);
    void SetTurfAppearance(DreamObjectTurf turf, MutableAppearance appearance);
    void SetAreaAppearance(DreamObjectArea area, MutableAppearance appearance);
    bool TryGetCellAt(Vector2i pos, int z, [NotNullWhen(true)] out Cell? cell);
    bool TryGetTurfAt(Vector2i pos, int z, [NotNullWhen(true)] out DreamObjectTurf? turf);
    void SetZLevels(int levels);
    void SetWorldSize(Vector2i size);
    EntityUid GetZLevelEntity(int z);

    IEnumerable<DreamObjectMob> GetMobsInRange((int X, int Y, int Z) loc, int distance);

    IEnumerable<AtomDirection> CalculateSteps((int X, int Y, int Z) loc, (int X, int Y, int Z) dest, int targetDistance,
        int maxSteps);

    sealed class Level {
        public readonly Entity<MapGridComponent> Grid;
        public readonly List<(Vector2i, Tile)> QueuedTileUpdates = new();
        public readonly int Z;
        public Cell[,] Cells;

        public Level(int z, Entity<MapGridComponent> grid, DreamObjectDefinition turfType, DreamObjectArea area,
            Vector2i size) {
            Z = z;
            Grid = grid;

            Cells = new Cell[size.X, size.Y];
            for (var x = 0; x < size.X; x++)
            for (var y = 0; y < size.Y; y++) {
                var turf = new DreamObjectTurf(turfType, x + 1, y + 1, z);
                var cell = new Cell(area, turf);

                turf.Cell = cell;
                Cells[x, y] = cell;
                turf.DecRef();
            }
        }
    }

    sealed class Cell {
        public readonly List<DreamObjectMovable> Movables = new();

        public readonly DreamObjectTurf Turf;

        private DreamObjectArea _area;

        public Cell(DreamObjectArea area, DreamObjectTurf turf) {
            Turf = turf;
            Turf.IncRef();
            _area = area;
            Area = area;
        }

        public DreamObjectArea Area {
            get => _area;
            set {
                _area.Turfs.Remove(Turf);
                _area.ResetCoordinateCache();

                DreamObjectArea oldArea = _area;
                _area = value;
                _area.Turfs.Add(Turf);
                _area.ResetCoordinateCache();

                Turf.OnAreaChange(oldArea);
            }
        }
    }
}
