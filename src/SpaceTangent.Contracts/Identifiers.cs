// SPDX-License-Identifier: GPL-2.0-or-later
namespace SpaceTangent.Contracts;

public readonly record struct ObjectId
{
    public ObjectId(string value) => Value = CanonicalId.Validate(value);
    public string Value { get; }
}

public readonly record struct FrameId
{
    public FrameId(string value) => Value = CanonicalId.Validate(value);
    public string Value { get; }
}

internal static class CanonicalId
{
    internal static string Validate(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        foreach (char character in value)
        {
            if (!(char.IsAsciiLetterOrDigit(character) || character is '/' or '-' or '_'))
                throw new ArgumentException("IDs contain ASCII letters, digits, slashes, hyphens and underscores.", nameof(value));
        }
        return value;
    }
}
