// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json;

namespace SpaceTangent.Core.Addons;

public enum AddonStage { Catalog, Kernel, Trajectory, Mesh, Texture, Terrain, World, RenderResources }
public enum AddonIssue { MissingResource, AccessDenied, InvalidInput, UnsupportedFeature, IoFailure }

public sealed record AddonDiagnostic(AddonIssue Code, AddonStage Stage, string? File, long? Line,
    string? Item, string? Source, string Detail);

/// <summary>Adds structured context without changing the exception type observed by existing callers.</summary>
public static class AddonDiagnostics
{
    private static readonly object DiagnosticKey = new();

    public static AddonDiagnostic? Get(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
            if (current.Data[DiagnosticKey] is AddonDiagnostic diagnostic)
                return diagnostic;
        return null;
    }

    public static T Read<T>(AddonStage stage, string? file, Func<T> read)
    {
        try
        {
            return read();
        }
        catch (Exception error) when (Annotate(error, stage, file))
        {
            throw;
        }
    }

    /// <summary>Records the most specific input context and returns false for unexpected failures and cancellation.</summary>
    public static bool Annotate(Exception error, AddonStage stage, string? file = null, long? line = null,
        string? item = null, string? source = null)
    {
        AddonIssue? issue = error switch
        {
            FileNotFoundException or DirectoryNotFoundException => AddonIssue.MissingResource,
            UnauthorizedAccessException => AddonIssue.AccessDenied,
            NotSupportedException => AddonIssue.UnsupportedFeature,
            IOException => AddonIssue.IoFailure,
            InvalidDataException or FormatException or JsonException or KeyNotFoundException or InvalidOperationException
                or ArgumentException or OverflowException => AddonIssue.InvalidInput,
            _ => null
        };
        if (issue is null)
            return false;

        AddonDiagnostic? diagnostic = Get(error);
        if (error is FileNotFoundException { FileName: { Length: > 0 } missing })
            file = missing;
        if (error is JsonException json)
        {
            line ??= json.LineNumber + 1;
            item ??= json.Path;
        }
        error.Data[DiagnosticKey] = diagnostic is null
            ? new AddonDiagnostic(issue.Value, stage, file, line, item, source, error.Message)
            : diagnostic with { File = diagnostic.File ?? file, Line = diagnostic.Line ?? line,
                Item = diagnostic.Item ?? item, Source = diagnostic.Source ?? source, Detail = error.Message };
        return true;
    }
}
