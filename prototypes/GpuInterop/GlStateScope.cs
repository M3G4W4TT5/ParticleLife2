namespace ParticleLife.GpuInterop;

/// <summary>Restores precisely the GL state touched by the adapter, leaving MonoGame's cached state valid.</summary>
/// <remarks>Scope covers program, VAO, array/SSBO bindings, SSBO slots zero/one, and five enable flags.
/// The adapter never changes framebuffer, viewport, textures, blend functions, colour masks, element
/// bindings in MonoGame's VAO, or attribute definitions in MonoGame's VAO. Queries are intentional
/// correctness scaffolding for this prototype; a production adapter may narrow the boundary after profiling.</remarks>
internal readonly struct GlStateScope : IDisposable
{
    private readonly NativeGl gl;
    private readonly int program, vertexArray, arrayBuffer, storageBuffer, storageZero, storageOne;
    private readonly bool blend, depth, scissor, cull, pointSize;

    internal GlStateScope(NativeGl gl)
    {
        this.gl = gl;
        gl.GetInteger(NativeGl.CurrentProgram, out program);
        gl.GetInteger(NativeGl.VertexArrayBinding, out vertexArray);
        gl.GetInteger(NativeGl.ArrayBufferBinding, out arrayBuffer);
        gl.GetInteger(NativeGl.ShaderStorageBinding, out storageBuffer);
        gl.GetIntegerIndexed(NativeGl.ShaderStorageBinding, 0, out storageZero);
        gl.GetIntegerIndexed(NativeGl.ShaderStorageBinding, 1, out storageOne);
        blend = gl.IsEnabled(NativeGl.Blend) != 0;
        depth = gl.IsEnabled(NativeGl.DepthTest) != 0;
        scissor = gl.IsEnabled(NativeGl.ScissorTest) != 0;
        cull = gl.IsEnabled(NativeGl.CullFace) != 0;
        pointSize = gl.IsEnabled(NativeGl.ProgramPointSize) != 0;
    }

    public void Dispose()
    {
        gl.UseProgram((uint)program);
        gl.BindVertexArray((uint)vertexArray);
        gl.BindBuffer(NativeGl.ArrayBuffer, (uint)arrayBuffer);
        gl.BindBufferBase(NativeGl.ShaderStorageBuffer, 0, (uint)storageZero);
        gl.BindBufferBase(NativeGl.ShaderStorageBuffer, 1, (uint)storageOne);
        gl.BindBuffer(NativeGl.ShaderStorageBuffer, (uint)storageBuffer);
        Restore(NativeGl.Blend, blend);
        Restore(NativeGl.DepthTest, depth);
        Restore(NativeGl.ScissorTest, scissor);
        Restore(NativeGl.CullFace, cull);
        Restore(NativeGl.ProgramPointSize, pointSize);
    }

    private void Restore(uint capability, bool enabled)
    {
        if (enabled)
        {
            gl.Enable(capability);
        }
        else
        {
            gl.Disable(capability);
        }
    }
}
