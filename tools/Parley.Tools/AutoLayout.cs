// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using Parley.Engine;

namespace Parley.Tools;

/// <summary>
/// Gives every province a position when a map file has no layout (imported maps).
/// A simple spring layout: connected provinces pull together, all provinces push
/// apart. The result is a readable node-and-link diagram, not a geographic map.
/// Deterministic: the same map always gets the same picture.
/// </summary>
public static class AutoLayout
{
    public static (int[] x, int[] y) Compute(MapData map, int size = 1000)
    {
        int n = map.provCount;
        var px = new double[n]; var py = new double[n];
        var rng = new Random(12345);
        for (int i = 0; i < n; i++)
        {
            double a = 2 * Math.PI * i / n;
            px[i] = size / 2.0 + Math.Cos(a) * size * 0.35 + rng.NextDouble() * 20;
            py[i] = size / 2.0 + Math.Sin(a) * size * 0.35 + rng.NextDouble() * 20;
        }
        double k = size * 0.9 / Math.Sqrt(n);
        var dx = new double[n]; var dy = new double[n];
        int iterations = 400;
        for (int it = 0; it < iterations; it++)
        {
            double temp = size * 0.08 * (1.0 - (double)it / iterations) + 1;
            Array.Clear(dx); Array.Clear(dy);
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                {
                    double ex = px[i] - px[j], ey = py[i] - py[j];
                    double d2 = ex * ex + ey * ey + 0.01; double d = Math.Sqrt(d2);
                    double f = k * k / d;
                    dx[i] += ex / d * f; dy[i] += ey / d * f; dx[j] -= ex / d * f; dy[j] -= ey / d * f;
                }
            for (int i = 0; i < n; i++)
                for (int a = 0; a < map.anyCount[i]; a++)
                {
                    int j = map.anyAdj[map.anyStart[i] + a];
                    if (j <= i) continue;
                    double ex = px[i] - px[j], ey = py[i] - py[j];
                    double d = Math.Sqrt(ex * ex + ey * ey) + 0.01;
                    double f = d * d / k;
                    dx[i] -= ex / d * f; dy[i] -= ey / d * f; dx[j] += ex / d * f; dy[j] += ey / d * f;
                }
            for (int i = 0; i < n; i++)
            {
                double d = Math.Sqrt(dx[i] * dx[i] + dy[i] * dy[i]) + 0.01;
                double step = Math.Min(d, temp);
                px[i] += dx[i] / d * step; py[i] += dy[i] / d * step;
            }
        }
        // fit into the square with a margin
        double minX = px.Min(), maxX = px.Max(), minY = py.Min(), maxY = py.Max();
        var rx = new int[n]; var ry = new int[n];
        for (int i = 0; i < n; i++)
        {
            rx[i] = (int)(40 + (px[i] - minX) / Math.Max(1, maxX - minX) * (size - 80));
            ry[i] = (int)(40 + (py[i] - minY) / Math.Max(1, maxY - minY) * (size - 80));
        }
        return (rx, ry);
    }
}
