namespace ParticleLife.Core.Experiments;

/// <summary>Contains one explicitly sampled set of geometric and speed measurements.</summary>
/// <param name="Time">Completed simulation time in reduced time units.</param>
/// <param name="Step">Completed integration step number.</param>
/// <param name="Count">Total particle count.</param>
/// <param name="FiniteCount">Particles whose positions and velocities are finite.</param>
/// <param name="OverlapPairs">Unordered pairs at exactly the same position.</param>
/// <param name="MeanSpeed">Arithmetic mean speed in reduced length per time; zero for no particles.</param>
/// <param name="MaxSpeed">Largest speed in reduced length per time; zero for no particles.</param>
/// <param name="ClusterDistance">Inclusive distance threshold in reduced length units.</param>
/// <param name="ClusterCount">Number of connected components, including single particles.</param>
/// <param name="LargestCluster">Population of the largest connected component; zero for no particles.</param>
/// <remarks>Clusters use undirected geometric connectivity; species and force direction do not affect this statistic.</remarks>
public sealed record DiagnosticSample(double Time, long Step, int Count, int FiniteCount, long OverlapPairs,
    double MeanSpeed, double MaxSpeed, double ClusterDistance, int ClusterCount, int LargestCluster);
