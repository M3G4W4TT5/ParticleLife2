namespace ParticleLife.MonoGame.Gpu;

/// <summary>Owns one linked compute program and caches uniform locations before repeated dispatch.</summary>
internal sealed class GpuProgram : IDisposable
{
    private readonly GpuComputeApi api;
    private uint handle;
    internal readonly int Count, ParticleCount, BucketCount, BucketMask, ClearStatus, Grid, Domain, ReceiverRange, Dynamics, Wall;

    internal GpuProgram(GpuComputeApi api, string source)
    {
        this.api = api;
        handle = api.BuildComputeProgram(source);
        Count = Location("count");
        ParticleCount = Location("particleCount");
        BucketCount = Location("bucketCount");
        BucketMask = Location("bucketMask");
        ClearStatus = Location("clearStatus");
        Grid = Location("grid");
        Domain = Location("domain");
        ReceiverRange = Location("receiverRange");
        Dynamics = Location("dynamics");
        Wall = Location("wall");
    }

    internal void Use() => api.Gl.UseProgram(handle);

    internal void Configure(GpuModelData model, int particles, int buckets)
    {
        Use();
        api.Gl.Uniform1i(ParticleCount, particles);
        api.Gl.Uniform1i(BucketMask, buckets - 1);
        api.Uniform4i(Grid, model.CellsX, model.CellsY, (int)model.Configuration.Parameters.Boundary, model.Configuration.Matrix.SpeciesCount);
        api.Gl.Uniform4f(Domain, model.Width, model.Height, model.CellWidth, model.CellHeight);
        api.Gl.Uniform4f(Dynamics, model.TimeStep, model.Damping, model.ForceScale, model.WallStrength);
        api.Gl.Uniform4f(Wall, model.WallWidth, 0, 0, 0);
    }

    private int Location(string name) => api.Gl.GetUniformLocation(handle, name);

    public void Dispose()
    {
        if (handle != 0 && api.Gl.CanUseOwner)
        {
            api.Gl.DeleteProgram(handle);
        }
        handle = 0;
    }
}
