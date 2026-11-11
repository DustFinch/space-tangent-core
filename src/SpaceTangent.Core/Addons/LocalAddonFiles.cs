// SPDX-License-Identifier: GPL-2.0-or-later
namespace SpaceTangent.Core.Addons;

internal static class LocalAddonFiles
{
    internal static string ResolveRelative(string root, string directory, string path)
    {
        if (Path.IsPathRooted(path))
            throw new InvalidDataException("Add-on resources must use relative paths.");
        return Resolve(root, directory, path);
    }

    internal static string Resolve(string root, string directory, string path)
    {
        string candidate = Path.GetFullPath(path, directory);
        string relative = Path.GetRelativePath(root, candidate);
        if (Path.IsPathRooted(relative) || relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("Catalogue asset is outside the declared add-on root.");
        if (!AddonDependencies.FileExists(candidate))
            throw new FileNotFoundException("Catalogue asset is missing.", candidate);

        for (string cursor = candidate; ; cursor = Path.GetDirectoryName(cursor)!)
        {
            if ((AddonDependencies.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Catalogue assets must not use symbolic links.");
            if (Path.GetRelativePath(root, cursor) == ".")
                return candidate;
        }
    }
}
