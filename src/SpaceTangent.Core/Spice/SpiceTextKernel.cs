// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using SpaceTangent.Core.Addons;
using System.Text;
using System.Text.RegularExpressions;

namespace SpaceTangent.Core.Spice;

internal sealed class SpiceTextKernel
{
    private static readonly Regex DataSections = new(@"^[ \t]*\\begindata[ \t]*\r?\n(.*?)(?:^[ \t]*\\begintext\b|\z)",
        RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex Assignments = new(@"^[ \t]*([A-Za-z0-9_/-]+)[ \t]*(\+=|=)[ \t]*(\([^)]*\)|[^\r\n]+)",
        RegexOptions.Multiline | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex Values = new(@"'(?:[^']|'')*'|[^\s(),]+",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private readonly Dictionary<string, string[]> _values = new(StringComparer.OrdinalIgnoreCase);

    internal void Furnish(string path) => Apply(Read(path));

    internal static IReadOnlyList<Assignment> Read(string path)
    {
        try
        {
            using FileStream file = AddonDependencies.OpenRead(path, 2 * 1024 * 1024);
            if (file.Length > 2 * 1024 * 1024)
                throw new InvalidDataException("SPICE text kernel exceeds 2 MiB.");
            using var reader = new StreamReader(file, new UTF8Encoding(false, true));
            var assignments = new List<Assignment>();
            foreach (Match section in DataSections.Matches(reader.ReadToEnd()))
            foreach (Match assignment in Assignments.Matches(section.Groups[1].Value))
            {
                string name = assignment.Groups[1].Value;
                string[] values = Values.Matches(assignment.Groups[3].Value)
                    .Select(match => match.Value.StartsWith('\'') ? match.Value[1..^1].Replace("''", "'") : match.Value)
                    .ToArray();
                assignments.Add(new(name, values, assignment.Groups[2].Value == "+="));
            }
            return assignments;
        }
        catch (Exception error) when (AddonDiagnostics.Annotate(error, AddonStage.Kernel, path))
        {
            throw;
        }
    }

    internal sealed record Assignment(string Name, string[] Values, bool Append);

    internal void Apply(IReadOnlyList<Assignment> assignments)
    {
        foreach (var (name, values, append) in assignments)
            _values[name] = append && _values.TryGetValue(name, out var previous) ? [.. previous, .. values] : values;
    }

    internal bool Contains(string name) => _values.ContainsKey(name);

    internal string[] Get(string name) => _values.TryGetValue(name, out var values) && values.Length > 0
        ? values
        : throw new InvalidDataException($"Missing SPICE kernel variable {name}.");

    internal int GetInteger(string name)
    {
        string[] values = Get(name);
        if (values.Length != 1 || !int.TryParse(values[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
            throw new InvalidDataException($"Expected a SPICE integer in {name}.");
        return value;
    }

    internal double[] GetNumbers(string name) => Get(name).Select(value =>
    {
        if (!double.TryParse(value.Replace('D', 'E').Replace('d', 'e'), NumberStyles.Float,
                CultureInfo.InvariantCulture, out double number) || !double.IsFinite(number))
            throw new InvalidDataException($"Invalid SPICE number in {name}.");
        return number;
    }).ToArray();
}
