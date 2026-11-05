namespace ParticleLife.MonoGame.Rendering;

/// <summary>Restores native draw state exactly so MonoGame's cached state remains authoritative.</summary>
/// <remarks>The adapter changes only the state listed here. Its private VAO prevents changes to
/// MonoGame's vertex attributes or element binding. Texture-buffer binding on unit zero is separate
/// from its ordinary 2D texture binding. No framebuffer, SSBO, compute, depth range or colour mask
/// is changed. A value-type scope avoids per-draw managed allocation.</remarks>
internal readonly struct GlDrawStateScope : IDisposable
{
    private readonly OpenGlDrawApi gl;
    private readonly int program, vertexArray, arrayBuffer, activeTexture, textureBuffer;
    private readonly int sourceRgb, destinationRgb, sourceAlpha, destinationAlpha, equationRgb, equationAlpha;
    private readonly bool blend, depth, scissor, cull, pointSize;
    internal GlRectangle Viewport { get; }
    private GlRectangle Scissor { get; }

    internal GlDrawStateScope(OpenGlDrawApi gl)
    {
        this.gl = gl;
        gl.EnsureOwner();
        program = gl.Integer(OpenGlDrawApi.CurrentProgram);
        vertexArray = gl.Integer(OpenGlDrawApi.VertexArrayBinding);
        arrayBuffer = gl.Integer(OpenGlDrawApi.ArrayBufferBinding);
        activeTexture = gl.Integer(OpenGlDrawApi.ActiveTextureName);
        gl.ActiveTexture(OpenGlDrawApi.TextureZero);
        textureBuffer = gl.Integer(OpenGlDrawApi.TextureBindingBuffer);
        gl.ActiveTexture((uint)activeTexture);
        Viewport = gl.Rectangle(OpenGlDrawApi.ViewportName);
        Scissor = gl.Rectangle(OpenGlDrawApi.ScissorBox);
        sourceRgb = gl.Integer(0x80C9);
        destinationRgb = gl.Integer(0x80C8);
        sourceAlpha = gl.Integer(0x80CB);
        destinationAlpha = gl.Integer(0x80CA);
        equationRgb = gl.Integer(0x8009);
        equationAlpha = gl.Integer(0x883D);
        blend = gl.IsEnabled(OpenGlDrawApi.Blend) != 0;
        depth = gl.IsEnabled(OpenGlDrawApi.DepthTest) != 0;
        scissor = gl.IsEnabled(OpenGlDrawApi.ScissorTest) != 0;
        cull = gl.IsEnabled(OpenGlDrawApi.CullFace) != 0;
        pointSize = gl.IsEnabled(OpenGlDrawApi.ProgramPointSize) != 0;
    }

    public void Dispose()
    {
        gl.UseProgram((uint)program);
        gl.BindVertexArray((uint)vertexArray);
        gl.BindBuffer(OpenGlDrawApi.ArrayBuffer, (uint)arrayBuffer);
        gl.ActiveTexture(OpenGlDrawApi.TextureZero);
        gl.BindTexture(OpenGlDrawApi.TextureBuffer, (uint)textureBuffer);
        gl.ActiveTexture((uint)activeTexture);
        gl.Viewport(Viewport.X, Viewport.Y, Viewport.Width, Viewport.Height);
        gl.Scissor(Scissor.X, Scissor.Y, Scissor.Width, Scissor.Height);
        gl.BlendFuncSeparate((uint)sourceRgb, (uint)destinationRgb, (uint)sourceAlpha, (uint)destinationAlpha);
        gl.BlendEquationSeparate((uint)equationRgb, (uint)equationAlpha);
        Restore(OpenGlDrawApi.Blend, blend);
        Restore(OpenGlDrawApi.DepthTest, depth);
        Restore(OpenGlDrawApi.ScissorTest, scissor);
        Restore(OpenGlDrawApi.CullFace, cull);
        Restore(OpenGlDrawApi.ProgramPointSize, pointSize);
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
