// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.World;

namespace SpaceTangent.Core.Addons;

/// <summary>Built-in models available in the host. Orbits return ICRF states relative to their native center.</summary>
public sealed record SscCustomModels(
    Func<string, Func<JulianInstant, CartesianState?>?> Orbit,
    Func<string, Func<JulianInstant, FrameKinematics?>?> Rotation);
