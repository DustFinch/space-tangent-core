// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;

namespace SpaceTangent.Core.Addons;

public enum MissionAxesKind { Geometry, BodyVisualizer, FrameVisualizer }

public sealed record MissionAxesStyle(MissionAxesKind Kind, double ScaleKilometers, bool NaturalAxes, bool Visible)
{
    public ObjectId? ReferenceOwner { get; init; }
    public float Opacity => Kind == MissionAxesKind.FrameVisualizer ? .3f : 1;
}

/// <summary>The twenty-section arrow profile, in source coordinates with its tip on +Z.</summary>
public sealed class MissionAxesMesh
{
    private static readonly MissionAxesMesh Geometry = Create(false), Visualizer = Create(true);

    private MissionAxesMesh(float[] vertices, int[] triangles, float boundingRadius)
    {
        Vertices = Array.AsReadOnly(vertices);
        Triangles = Array.AsReadOnly(triangles);
        BoundingRadius = boundingRadius;
    }

    public IReadOnlyList<float> Vertices { get; }
    public IReadOnlyList<int> Triangles { get; }
    public float BoundingRadius { get; }

    public static MissionAxesMesh Build(MissionAxesKind kind) => kind == MissionAxesKind.Geometry ? Geometry : Visualizer;

    private static MissionAxesMesh Create(bool visualizer)
    {
        const int sections = 20, ringSize = sections + 1, cap = 3 * ringSize, tip = cap + 1;
        float shaft = visualizer ? .9f : 1, head = visualizer ? .1f : .05f;
        float shaftRadius = visualizer ? .01f : .005f, headRadius = visualizer ? .02f : .01f;
        var vertices = new float[(tip + 1) * 3];
        var triangles = new List<int>(sections * 18);
        float bound = 0;
        for (int ring = 0; ring < 3; ring++)
        {
            float radius = ring == 2 ? headRadius : shaftRadius;
            for (int index = 0; index <= sections; index++)
            {
                float angle = index / (float)sections * 2 * MathF.PI;
                Vertex(ring * ringSize + index, MathF.Cos(angle) * radius, MathF.Sin(angle) * radius, ring == 0 ? 0 : shaft);
            }
        }
        Vertex(tip, 0, 0, shaft + head);
        for (int index = 0; index < sections; index++)
            Triangle(cap, sections - index, sections - index - 1);
        Strip(ringSize, 0);
        Strip(2 * ringSize, ringSize);
        for (int index = 0; index < sections; index++)
            Triangle(tip, 2 * ringSize + index, 2 * ringSize + index + 1);
        return new(vertices, triangles.ToArray(), bound);

        void Vertex(int index, float x, float y, float z)
        {
            int offset = index * 3;
            vertices[offset] = x;
            vertices[offset + 1] = y;
            vertices[offset + 2] = z;
            bound = MathF.Max(bound, MathF.Sqrt(x * x + y * y + z * z));
        }

        void Strip(int outer, int inner)
        {
            for (int index = 0; index < sections; index++)
            {
                Triangle(outer + index, inner + index, outer + index + 1);
                Triangle(outer + index + 1, inner + index, inner + index + 1);
            }
        }

        void Triangle(int first, int second, int third)
        {
            triangles.Add(first);
            triangles.Add(second);
            triangles.Add(third);
        }
    }
}
