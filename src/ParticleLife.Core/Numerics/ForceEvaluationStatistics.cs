namespace ParticleLife.Core.Numerics;

/// <summary>Exact geometric counts from one force evaluation, requiring no extra diagnostic pass.</summary>
/// <param name="CandidatePairs">Number of directed non-self candidates inspected; N(N-1) for direct enumeration.</param>
/// <param name="AcceptedPairs">Number of directed, noncoincident pairs strictly inside the cutoff, including zero coefficients.</param>
/// <param name="OverlapPairs">Number of unordered exactly coincident pairs; each contributes zero pair force.</param>
/// <remarks>
/// These counts describe the input positions, before integration. Walls are not pairs.
/// If a grid evaluation retries direct enumeration, counts describe the successful direct evaluation;
/// candidates visited in the discarded partial grid attempt are not included.
/// </remarks>
public readonly record struct ForceEvaluationStatistics(long CandidatePairs, long AcceptedPairs, long OverlapPairs);
