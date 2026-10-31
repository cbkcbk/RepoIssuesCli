using System.Globalization;

namespace RepoInfraCli.Features.Infra.Postgres;

public static class CronSchedule
{
    static readonly (string Name, int Lowest, int Highest)[] Fields =
    [
        ("minute", 0, 59),
        ("hour", 0, 23),
        ("day of month", 1, 31),
        ("month", 1, 12),
        ("day of week", 0, 7),
    ];

    public static string? Problem(string schedule)
    {
        var fields = schedule.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != Fields.Length)
            return $"schedule \"{schedule}\" needs five fields (minute hour day-of-month month day-of-week, in UTC), as in \"0 3 * * *\"";

        for (var index = 0; index < fields.Length; index++)
        {
            var (name, lowest, highest) = Fields[index];
            if (!fields[index].Split(',').All(part => ValidPart(part, lowest, highest)))
                return $"schedule \"{schedule}\": the {name} field \"{fields[index]}\" must be *, numbers {lowest}-{highest}, ranges a-b, lists a,b or steps */n";
        }
        return null;
    }

    static bool ValidPart(string part, int lowest, int highest)
    {
        var slash = part.IndexOf('/');
        var range = slash < 0 ? part : part[..slash];
        if (slash >= 0 && !(Number(part[(slash + 1)..], out var step) && step > 0))
            return false;
        if (range == "*")
            return true;

        var dash = range.IndexOf('-');
        if (dash < 0)
            return Number(range, out var single) && single >= lowest && single <= highest;
        return Number(range[..dash], out var from) && Number(range[(dash + 1)..], out var to) && from >= lowest && from <= to && to <= highest;
    }

    static bool Number(string text, out int value) => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
}
