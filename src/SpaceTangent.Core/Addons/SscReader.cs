// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using System.Text;

namespace SpaceTangent.Core.Addons;

internal enum SscDisposition { Add, Modify, Replace }

internal abstract record SscValue
{
    internal sealed record Atom(string Text) : SscValue;
    internal sealed record List(SscValue[] Items) : SscValue;
    internal sealed record Map(Dictionary<string, SscValue> Fields) : SscValue
    {
        internal SscValue Get(string key) => Fields.TryGetValue(key, out var value)
            ? value
            : throw new InvalidDataException($"Missing SSC field {key}.");
        internal string Text(string key) => Get(key).AsText();
        internal Map Object(string key) => Get(key) as Map ?? throw new InvalidDataException($"Expected SSC object {key}.");
    }

    internal string AsText() => this is Atom value ? value.Text : throw new InvalidDataException("Expected SSC text.");
}

internal sealed class SscReader(string input, string path)
{
    private int _offset;
    private int _line = 1;

    internal IEnumerable<(SscDisposition Disposition, string Kind, string Name, string Parent, SscValue.Map Properties)> ReadDefinitions()
    {
        while (SkipSpace())
        {
            var disposition = SscDisposition.Add;
            string kind = "Body";
            if (input[_offset] != '"')
            {
                string prefix = ReadText();
                if (prefix is "Add" or "Modify" or "Replace")
                {
                    disposition = Enum.Parse<SscDisposition>(prefix);
                    if (SkipSpace() && input[_offset] != '"')
                        kind = ReadText();
                }
                else
                    kind = prefix;
            }
            if (kind is not ("Body" or "ReferencePoint" or "SurfaceObject" or "AltSurface" or "Location"))
                throw Error($"Unknown SSC object kind {kind}.");
            string name = ReadText();
            string parent = ReadText();
            if (!SkipSpace() || input[_offset] != '{')
                throw Error("Expected object properties.");
            yield return (disposition, kind, name, parent, ReadObject(0));
        }
    }

    internal IEnumerable<(string Name, SscValue.Map Properties)> ReadNamedRecords()
    {
        while (SkipSpace())
        {
            if (input[_offset] != '"')
                throw Error("Expected a quoted record name.");
            string name = ReadText();
            if (!SkipSpace() || input[_offset] != '{')
                throw Error("Expected record properties.");
            yield return (name, ReadObject(0));
        }
    }

    internal IEnumerable<(string Kind, string Name, SscValue.Map Properties)> ReadTypedRecords(string defaultKind)
    {
        foreach (var (disposition, kind, number, name, properties) in ReadCatalogueRecords(defaultKind))
        {
            if (disposition != SscDisposition.Add || number is not null || name is null)
                throw Error("Expected a named catalogue addition.");
            yield return (kind, name, properties);
        }
    }

    internal IEnumerable<(SscDisposition Disposition, string Kind, uint? Number, string? Name,
        SscValue.Map Properties)> ReadCatalogueRecords(string defaultKind)
    {
        while (SkipSpace())
        {
            SscDisposition disposition = SscDisposition.Add;
            string kind = defaultKind;
            uint? number = null;
            string? name = null;
            if (input[_offset] != '"' && !char.IsAsciiDigit(input[_offset]))
            {
                string prefix = ReadText();
                if (prefix is "Add" or "Modify" or "Replace")
                {
                    disposition = Enum.Parse<SscDisposition>(prefix);
                    if (SkipSpace() && input[_offset] != '"' && !char.IsAsciiDigit(input[_offset]))
                        kind = ReadText();
                }
                else
                {
                    kind = prefix;
                }
            }
            if (SkipSpace() && char.IsAsciiDigit(input[_offset]))
                number = uint.TryParse(ReadText(), NumberStyles.None, CultureInfo.InvariantCulture, out uint parsed)
                    ? parsed : throw Error("Invalid catalogue number.");
            if (SkipSpace() && input[_offset] == '"')
                name = ReadText();
            if (!SkipSpace() || input[_offset] != '{')
                throw Error("Expected catalogue object properties.");
            yield return (disposition, kind, number, name, ReadObject(0));
        }
    }

    private SscValue ReadValue(int depth)
    {
        if (depth >= 16 || !SkipSpace())
            throw Error("SSC value is incomplete or nested too deeply.");
        char next = input[_offset];
        if (next == '{')
            return ReadObject(depth);
        if (next == '[')
            return ReadArray(depth);
        return new SscValue.Atom(ReadText());
    }

    private SscValue.Map ReadObject(int depth)
    {
        _offset++;
        var fields = new Dictionary<string, SscValue>(StringComparer.Ordinal);
        while (SkipSpace() && input[_offset] != '}')
        {
            string key = ReadText();
            if (!fields.TryAdd(key, ReadValue(depth + 1)))
                throw Error($"Duplicate property {key}.");
        }
        Consume('}');
        return new(fields);
    }

    private SscValue.List ReadArray(int depth)
    {
        _offset++;
        var values = new List<SscValue>();
        while (SkipSpace() && input[_offset] != ']')
            values.Add(ReadValue(depth + 1));
        Consume(']');
        return new([.. values]);
    }

    private string ReadText()
    {
        if (!SkipSpace() || input[_offset] is '{' or '}' or '[' or ']')
            throw Error("Expected text.");
        if (input[_offset] == '"')
        {
            _offset++;
            var text = new StringBuilder();
            while (_offset < input.Length)
            {
                char character = input[_offset++];
                if (character == '"')
                    return text.ToString();
                if (character is '\r' or '\n')
                    break;
                if (character == '\\' && _offset < input.Length && input[_offset] is '"' or '\\')
                    character = input[_offset++];
                text.Append(character);
            }
            throw Error("Unterminated quoted text.");
        }

        int start = _offset;
        while (_offset < input.Length && !char.IsWhiteSpace(input[_offset]) &&
               input[_offset] is not ('{' or '}' or '[' or ']' or '#' or '"'))
            _offset++;
        if (_offset == start)
            throw Error("Invalid text token.");
        return input[start.._offset];
    }

    private bool SkipSpace()
    {
        while (_offset < input.Length)
        {
            char value = input[_offset];
            if (value == '#')
            {
                while (_offset < input.Length && input[_offset] != '\n')
                    _offset++;
                continue;
            }
            if (!char.IsWhiteSpace(value))
                return true;
            if (value == '\n')
                _line++;
            _offset++;
        }
        return false;
    }

    private void Consume(char expected)
    {
        if (_offset >= input.Length || input[_offset++] != expected)
            throw Error($"Expected {expected}.");
    }

    private InvalidDataException Error(string message)
    {
        var error = new InvalidDataException($"SSC line {_line}. {message}");
        AddonDiagnostics.Annotate(error, AddonStage.Catalog, path, _line);
        return error;
    }
}
