using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Portmark.Core.Reporting;

/// <summary>
/// The values on this machine that identify a person or the machine itself, as distinct from its
/// make and model. Passed in rather than read inside the redactor so the rules can be tested with
/// names that are not the tester's own.
/// </summary>
public sealed record IdentityHints(string? MachineName, string? UserName, string? UserProfilePath, string? UserDomainName)
{
    public static IdentityHints FromEnvironment() => new(
        Environment.MachineName,
        Environment.UserName,
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) is { Length: > 0 } profile ? profile : null,
        Environment.UserDomainName);
}

/// <summary>One redacted value: its placeholder, what kind of thing it was, and where it appeared. Never the value.</summary>
public sealed class RedactionEntry
{
    public string Placeholder { get; init; } = "";
    public string Kind { get; init; } = "";
    public List<string> Locations { get; init; } = [];
}

/// <summary>
/// Removes identifying values from a hardware report before it is written, so that a file meant for
/// a public issue does not carry serial numbers, device paths, this PC's name or the user's name.
///
/// It works on the JSON tree rather than on the report types, so a field added later is covered
/// by the same rules: anything named <c>serialNumber</c>, anything ending <c>InstanceId</c> or
/// <c>Path</c>, and this machine's own names wherever they turn up in any string. Every value gets
/// one placeholder for the whole report, so two devices with different serials stay two devices,
/// and the same serial in the JSON and the <c>--human</c> text stays visibly the same one.
///
/// Raw bytes and CCI values are hex, and names are only matched as whole words, so a name that
/// happens to spell out in hex can never corrupt a payload.
/// </summary>
public sealed partial class ReportRedactor
{
    /// <summary>
    /// Field values shorter than this are replaced in their own field but not searched for
    /// elsewhere: a serial of "1" would otherwise take out every "Port 1" in the human text.
    /// </summary>
    private const int MinimumSearchableLength = 4;

    private readonly IdentityHints _hints;
    private readonly List<RedactionEntry> _entries = [];
    private readonly Dictionary<string, RedactionEntry> _byKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _counters = new(StringComparer.Ordinal);
    private readonly List<(string Value, string Key, string Kind, string Prefix)> _fieldValues = [];

    public ReportRedactor(IdentityHints hints) => _hints = hints;

    /// <summary>What was replaced, in the order it was first met.</summary>
    public IReadOnlyList<RedactionEntry> Entries => _entries;

    /// <summary>What the redactor looks for, stated in the report so an empty list means something.</summary>
    public static IReadOnlyList<string> LooksFor { get; } =
    [
        "USB serial numbers",
        "device instance IDs, apart from short ACPI unique IDs such as the 0 in ACPI\\USBC000\\0",
        "device paths",
        "this PC's name and domain",
        "your user name",
        "user profile paths",
    ];

    /// <summary>
    /// Redacts in place. Fields are done first, over the whole tree, so that a serial recorded in
    /// the JSON is already known by the time the human text that repeats it is searched.
    /// </summary>
    public void Redact(JsonNode root)
    {
        RedactFields(root, "");
        ScrubStrings(root, "");
    }

    private void RedactFields(JsonNode? node, string path)
    {
        if (node is JsonObject obj)
        {
            foreach ((string name, JsonNode? child) in obj.ToList())
            {
                string childPath = path.Length == 0 ? name : $"{path}.{name}";
                if (AsString(child) is { Length: > 0 } value)
                {
                    if (RedactField(name, value, childPath) is { } replaced) obj[name] = replaced;
                }
                else
                {
                    RedactFields(child, childPath);
                }
            }
        }
        else if (node is JsonArray array)
        {
            for (int i = 0; i < array.Count; i++) RedactFields(array[i], $"{path}[{i}]");
        }
    }

    private string? RedactField(string name, string value, string path)
    {
        if (name.Equals("serialNumber", StringComparison.OrdinalIgnoreCase))
            return RecordFieldValue(value, "serial", "USB serial number", path);

        if (name.EndsWith("InstanceId", StringComparison.OrdinalIgnoreCase))
        {
            // Enumerator and hardware ID say what the device is; only the last segment can carry a
            // serial. A short decimal there is an ACPI unique ID, the same on every such machine.
            int cut = value.LastIndexOf('\\');
            string instance = value[(cut + 1)..];
            if (instance.Length == 0 || AcpiUniqueId().IsMatch(instance)) return null;
            return value[..(cut + 1)] + RecordFieldValue(instance, "instance", "device instance ID", path);
        }

        if (name.EndsWith("Path", StringComparison.OrdinalIgnoreCase))
            return RecordFieldValue(value, "path", "device path", path);

        return null;
    }

    private string RecordFieldValue(string value, string prefix, string kind, string path)
    {
        string key = $"{prefix}:{value}";
        if (!_byKey.ContainsKey(key) && value.Length >= MinimumSearchableLength)
            _fieldValues.Add((value, key, kind, prefix));
        return Record(key, kind, prefix, numbered: true, path);
    }

    private void ScrubStrings(JsonNode? node, string path)
    {
        if (node is JsonObject obj)
        {
            foreach ((string name, JsonNode? child) in obj.ToList())
            {
                string childPath = path.Length == 0 ? name : $"{path}.{name}";
                if (AsString(child) is { } value)
                {
                    string scrubbed = Scrub(value, childPath);
                    if (scrubbed != value) obj[name] = scrubbed;
                }
                else
                {
                    ScrubStrings(child, childPath);
                }
            }
        }
        else if (node is JsonArray array)
        {
            for (int i = 0; i < array.Count; i++)
            {
                string childPath = $"{path}[{i}]";
                if (AsString(array[i]) is { } value)
                {
                    string scrubbed = Scrub(value, childPath);
                    if (scrubbed != value) array[i] = scrubbed;
                }
                else
                {
                    ScrubStrings(array[i], childPath);
                }
            }
        }
    }

    private string Scrub(string text, string path)
    {
        // Longest first, so a value that contains another is replaced whole.
        foreach ((string value, string key, string kind, string prefix) in _fieldValues.OrderByDescending(v => v.Value.Length))
            text = ReplaceWholeWord(text, value, RegexOptions.None, () => Record(key, kind, prefix, numbered: true, path));

        // The profile path before the bare user name, so it reads as a path rather than a name.
        if (_hints.UserProfilePath is { Length: > 0 } profile)
            text = ReplaceWholeWord(text, profile, RegexOptions.IgnoreCase,
                                    () => Record("hint:profile", "user profile path", "user-profile", numbered: false, path));

        // Anyone's profile, not only this user's: a path from another account names that person.
        text = ReplaceOutsidePlaceholders(text, OtherUserInPath(), m =>
            Record($"user-in-path:{m.Value.ToLowerInvariant()}", "user name in a path", "user-name-in-path", numbered: true, path));

        if (_hints.MachineName is { Length: > 0 } machine)
            text = ReplaceWholeWord(text, machine, RegexOptions.IgnoreCase,
                                    () => Record("hint:machine", "this PC's name", "machine-name", numbered: false, path));

        if (_hints.UserDomainName is { Length: > 0 } domain
            && !domain.Equals(_hints.MachineName, StringComparison.OrdinalIgnoreCase))
            text = ReplaceWholeWord(text, domain, RegexOptions.IgnoreCase,
                                    () => Record("hint:domain", "domain name", "domain-name", numbered: false, path));

        if (_hints.UserName is { Length: > 0 } user)
            text = ReplaceWholeWord(text, user, RegexOptions.IgnoreCase,
                                    () => Record("hint:user", "user name", "user-name", numbered: false, path));

        return text;
    }

    private static string ReplaceWholeWord(string text, string value, RegexOptions options, Func<string> placeholder)
    {
        if (!text.Contains(value, (options & RegexOptions.IgnoreCase) != 0
                                       ? StringComparison.OrdinalIgnoreCase
                                       : StringComparison.Ordinal))
            return text;

        var pattern = new Regex($"(?<![A-Za-z0-9]){Regex.Escape(value)}(?![A-Za-z0-9])", options | RegexOptions.CultureInvariant);
        return ReplaceOutsidePlaceholders(text, pattern, _ => placeholder());
    }

    /// <summary>
    /// Replaces only outside placeholders already inserted. Without this, a user called "user"
    /// would be found again inside "[user-name]".
    /// </summary>
    private static string ReplaceOutsidePlaceholders(string text, Regex pattern, MatchEvaluator replacement)
    {
        string[] parts = Placeholder().Split(text);
        for (int i = 0; i < parts.Length; i += 2)   // the captured placeholders sit at odd indices
            parts[i] = pattern.Replace(parts[i], replacement);
        return string.Concat(parts);
    }

    private string Record(string key, string kind, string prefix, bool numbered, string path)
    {
        if (!_byKey.TryGetValue(key, out RedactionEntry? entry))
        {
            string placeholder = numbered
                ? $"[{prefix}-{_counters[prefix] = _counters.GetValueOrDefault(prefix) + 1}]"
                : $"[{prefix}]";
            entry = new RedactionEntry { Placeholder = placeholder, Kind = kind };
            _byKey[key] = entry;
            _entries.Add(entry);
        }

        if (!entry.Locations.Contains(path)) entry.Locations.Add(path);
        return entry.Placeholder;
    }

    private static string? AsString(JsonNode? node)
        => node is JsonValue value && value.TryGetValue(out string? s) ? s : null;

    [GeneratedRegex(@"^\d{1,4}$")]
    private static partial Regex AcpiUniqueId();

    [GeneratedRegex(@"(\[[a-z]+(?:-[a-z]+)*(?:-\d+)?\])")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"(?<=[A-Za-z]:\\Users\\)(?!(?:Public|Default|All Users)(?:\\|$))[^\\/:*?""<>|\s\[\]]+", RegexOptions.IgnoreCase)]
    private static partial Regex OtherUserInPath();
}
