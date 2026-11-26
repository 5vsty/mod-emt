using FSDEV.Common.FDF.IlpParser.IlpParser.Wrapper;
using FSDEV.Common.Logger;
using Newtonsoft.Json.Linq;
using System.Diagnostics.CodeAnalysis;
using IlpStructureParser = FSDEV.Common.FDF.IlpParser.IlpParser.IlpParser;

namespace FSDEV.MOD.Emt.Emt_Server.Locations;

/// <summary>A selectable place of the facility: the id of the FDF area and its path ("Hall 1 / Wareneingang").</summary>
public readonly record struct LocationOption(string LocationId, string Path, string Type)
{
    public JObject ToJson() => new() { ["LocationId"] = LocationId, ["Path"] = Path, ["Type"] = Type };
}

public interface ILocationSource
{
    bool TryGetLocations(out List<LocationOption> locations);
}

/// <summary>
/// Reads the area tree of the facility structure (FDF version 1, D-04) with the IlpParser, like Notifications and LocationSearch do.
/// </summary>
public class IlpParserLocationSource : ILocationSource
{
    private const string LOG_PREFIX = $"[{nameof(IlpParserLocationSource)}]";
    private const string PATH_SEPARATOR = " / ";

    private readonly string ProjectPath;
    private readonly int FdfVersion;

    public IlpParserLocationSource(string projectPath, int fdfVersion)
    {
        ProjectPath = projectPath;
        FdfVersion = fdfVersion;
    }

    public bool TryGetLocations(out List<LocationOption> locations)
    {
        locations = [];

        if (FdfVersion != 1)
        {
            Logging.Log(LogLevel.Error, $"{LOG_PREFIX} FDF version {FdfVersion} is not supported, only version 1");
            return false;
        }

        var parser = new IlpStructureParser();
        if (!parser.TryParseFromIlp(ProjectPath, out var failureReason))
        {
            Logging.Log(LogLevel.Error, $"{LOG_PREFIX} Unable to parse project information from {ProjectPath}. Reason: {failureReason}");
            return false;
        }

        if (!parser.TryGetStructure(out var structure) || !structure.TryGetAllLocationsById(out var rootLocations))
        {
            Logging.Log(LogLevel.Error, $"{LOG_PREFIX} Unable to retrieve the locations of the structure");
            return false;
        }

        foreach (var (locationId, location) in rootLocations)
        {
            Collect(locationId, location, string.Empty, locations);
        }

        return true;
    }

    private static void Collect(Guid areaId, Area area, string parentPath, List<LocationOption> result)
    {
        _ = area.TryGetName(out var name);
        _ = area.TryGetTypeOfArea(out var type);

        var path = parentPath.Length == 0 ? name ?? areaId.ToString() : $"{parentPath}{PATH_SEPARATOR}{name ?? areaId.ToString()}";
        result.Add(new LocationOption(areaId.ToString(), path, type ?? string.Empty));

        if (area.TryGetAllChildAreasById(out var children))
        {
            foreach (var (childId, child) in children)
            {
                Collect(childId, child, path, result);
            }
        }
    }
}

/// <summary>
/// Caches the selectable locations. The IlpParser opens its own ILP connection for every parse, so the structure is only re-read
/// when the cache is older than <paramref name="maxAge"/>; if a re-read fails the old data stays in use.
/// </summary>
public class LocationProvider
{
    private const string LOG_PREFIX = $"[{nameof(LocationProvider)}]";

    private readonly object Lock = new();
    private readonly ILocationSource Source;
    private readonly HashSet<string> SelectableTypes;
    private readonly TimeSpan MaxAge;
    private readonly Func<DateTime> UtcNow;

    private List<LocationOption>? Cache;
    private DateTime LoadedAt = DateTime.MinValue;

    public LocationProvider(ILocationSource source, IEnumerable<string> selectableAreaTypes, TimeSpan maxAge, Func<DateTime>? utcNow = null)
    {
        Source = source;
        SelectableTypes = selectableAreaTypes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        MaxAge = maxAge;
        UtcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public bool TryGetAll([NotNullWhen(true)] out IReadOnlyList<LocationOption>? locations)
    {
        lock (Lock)
        {
            if (Cache is null || UtcNow() - LoadedAt > MaxAge)
            {
                if (Source.TryGetLocations(out var loaded))
                {
                    Cache = loaded.Where(location => SelectableTypes.Count == 0 || SelectableTypes.Contains(location.Type)).OrderBy(location => location.Path, StringComparer.CurrentCultureIgnoreCase).ToList();
                    LoadedAt = UtcNow();
                }
                else if (Cache is null)
                {
                    Logging.Log(LogLevel.Error, $"{LOG_PREFIX} No locations available");
                    locations = null;
                    return false;
                }
                else
                {
                    Logging.Log(LogLevel.Warning, $"{LOG_PREFIX} Refreshing the locations failed, using the previous data");
                }
            }

            locations = Cache!;
            return true;
        }
    }

    public bool TryGet(string locationId, out LocationOption location)
    {
        location = default;
        if (!TryGetAll(out var all))
        {
            return false;
        }

        foreach (var candidate in all)
        {
            if (string.Equals(candidate.LocationId, locationId, StringComparison.OrdinalIgnoreCase))
            {
                location = candidate;
                return true;
            }
        }

        return false;
    }
}
