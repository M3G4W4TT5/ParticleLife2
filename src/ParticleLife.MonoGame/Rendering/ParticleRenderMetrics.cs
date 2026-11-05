namespace ParticleLife.MonoGame.Rendering;

/// <summary>Describes CPU work performed for the latest particle draw, excluding present and GPU completion.</summary>
/// <param name="PreparationMilliseconds">CPU coordinate/vertex preparation time; zero when reusable data is unchanged.</param>
/// <param name="UploadMilliseconds">Time spent submitting buffer uploads, including any driver stalls.</param>
/// <param name="SubmissionMilliseconds">Draw submission and state-management time, not GPU execution time.</param>
/// <param name="UploadedBytes">Particle and palette payload submitted this draw, excluding capacity reservations.</param>
/// <param name="InputCount">Particle membership in the completed frame, including offscreen particles.</param>
/// <param name="SubmittedParticles">Particle primitives submitted; GPU clipping may remove their pixels.</param>
/// <param name="Backend">Actual particle rendering path, independent of simulation hardware.</param>
/// <param name="FallbackReason">Reason the compatible quad path was selected, when applicable.</param>
internal readonly record struct ParticleRenderMetrics(double PreparationMilliseconds, double UploadMilliseconds,
    double SubmissionMilliseconds, long UploadedBytes, int InputCount, int SubmittedParticles,
    string Backend, string? FallbackReason);
