using ParticleLife.Core.State;

namespace ParticleLife.Core.Experiments;

/// <summary>Provides the independent direct-pair reference for completed-state diagnostic measurements.</summary>
/// <remarks>Use <see cref="DiagnosticSampler"/> for repeated host sampling with reusable spatial/component
/// storage. This straightforward enumeration remains available for parity checks and independent evidence.</remarks>
public static class Diagnostics
{
    /// <summary>Samples speeds, exact overlaps and geometric connected components.</summary>
    /// <param name="state">Completed read-only state; do not advance its owner while sampling.</param>
    /// <param name="clusterDistance">Finite positive inclusive connection radius in reduced length units.</param>
    /// <returns>One measurement; connected paths may join particles farther apart than the radius.</returns>
    /// <remarks>
    /// This O(N squared) measurement allocates O(N) temporary storage and is deliberately independent
    /// of force evaluation and integration. The caller chooses the sampling cadence. It changes no state.
    /// No potential energy or conservation claim is inferred for arbitrary directed interactions.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The connection radius is not finite and positive.</exception>
    /// <exception cref="InvalidOperationException">State or resulting speed measurements are non-finite.</exception>
    public static DiagnosticSample Sample(ParticleStateView state, double clusterDistance)
    {
        if (!double.IsFinite(clusterDistance) || clusterDistance <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(clusterDistance), "Cluster distance must be finite and positive.");
        }

        int[] parents = new int[state.Count];
        int[] sizes = new int[state.Count];
        double meanSpeed = 0;
        double maxSpeed = 0;
        int finiteCount = 0;
        for (int i = 0; i < state.Count; i++)
        {
            parents[i] = i;
            sizes[i] = 1;
            Vector2d p = state.Positions[i];
            Vector2d v = state.Velocities[i];
            if (!p.IsFinite || !v.IsFinite)
            {
                throw new InvalidOperationException($"Non-finite state at particle {state.Ids[i]}.");
            }

            finiteCount++;
            double speed = double.Hypot(v.X, v.Y);
            if (!double.IsFinite(speed))
            {
                throw new InvalidOperationException($"Speed overflow at particle {state.Ids[i]}.");
            }

            meanSpeed += (speed - meanSpeed) / (i + 1);
            maxSpeed = Math.Max(maxSpeed, speed);
        }

        long overlaps = 0;
        int clusters = state.Count;
        int largest = state.Count == 0 ? 0 : 1;
        for (int i = 0; i < state.Count; i++)
        {
            for (int j = i + 1; j < state.Count; j++)
            {
                Vector2d first = state.Positions[i];
                Vector2d second = state.Positions[j];
                double distance = double.Hypot(second.X - first.X, second.Y - first.Y);
                if (first == second)
                {
                    overlaps++;
                }

                if (distance <= clusterDistance)
                {
                    int a = FindRoot(parents, i);
                    int b = FindRoot(parents, j);
                    if (a != b)
                    {
                        if (sizes[a] < sizes[b])
                        {
                            (a, b) = (b, a);
                        }

                        parents[b] = a;
                        sizes[a] += sizes[b];
                        clusters--;
                        largest = Math.Max(largest, sizes[a]);
                    }
                }
            }
        }

        return new DiagnosticSample(state.Time, state.Step, state.Count, finiteCount, overlaps,
            meanSpeed, maxSpeed, clusterDistance, clusters, largest);
    }

    /// <summary>Finds a component root, compressing paths only within this diagnostic's private scratch array.</summary>
    private static int FindRoot(int[] parents, int node)
    {
        while (parents[node] != node)
        {
            parents[node] = parents[parents[node]];
            node = parents[node];
        }

        return node;
    }
}
