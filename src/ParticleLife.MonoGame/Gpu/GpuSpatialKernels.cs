namespace ParticleLife.MonoGame.Gpu;

/// <summary>Defines optional observation kernels over immutable completed float32 particle records.</summary>
/// <remarks>Observation geometry and speed reduction use binary64 arithmetic; simulation remains
/// float32. Every particle contributes exactly once to an inside bin or the outside counter, and once
/// to the global speed/count reduction. Right/bottom rectangle edges are included in the final bin,
/// matching SpatialSampler. These buffers are independent of the simulation grid and summary scratch.</remarks>
internal static class GpuSpatialKernels
{
    internal const string Clear = """
        #version 430
        layout(local_size_x=256) in;
        layout(std430,binding=1) writeonly buffer Histogram { uint bins[]; };
        uniform int count;
        void main() { uint index=gl_GlobalInvocationID.x; if(index<uint(count)) bins[index]=0u; }
        """;

    private const string Header = """
        #version 430
        layout(local_size_x=256) in;
        struct Summary { double meanContribution; double maximumSpeed; uint count; uint flags; };
        uniform int count;
        shared double means[256];
        shared double maximums[256];
        shared uint counts[256];
        shared uint flags[256];
        void reduce(uint thread) {
            barrier();
            for(uint width=128u;width>0u;width/=2u) {
                if(thread<width) {
                    means[thread]+=means[thread+width];
                    maximums[thread]=max(maximums[thread],maximums[thread+width]);
                    counts[thread]+=counts[thread+width];
                    flags[thread]|=flags[thread+width];
                }
                barrier();
            }
        }
        """;

    internal static readonly string Accumulate = Header + """

        struct Particle { vec2 position; vec2 velocity; uvec2 identity; uint species; uint padding; };
        layout(std430,binding=0) readonly buffer InputState { Particle particles[]; };
        layout(std430,binding=1) buffer Histogram { uint bins[]; };
        layout(std430,binding=2) writeonly buffer OutputSummary { Summary summaries[]; };
        uniform dvec4 rectangle; // origin x/y, positive width/height
        uniform ivec4 dimensions; // columns, rows
        void main() {
            uint index=gl_GlobalInvocationID.x,thread=gl_LocalInvocationID.x;
            double speed=0.0;
            uint invalid=0u;
            if(index<uint(count)) {
                dvec2 point=(dvec2(particles[index].position)-rectangle.xy)/rectangle.zw;
                uint target=uint(dimensions.x*dimensions.y);
                if(all(greaterThanEqual(point,dvec2(0.0))) && all(lessThanEqual(point,dvec2(1.0)))) {
                    ivec2 cell=min(dimensions.xy-ivec2(1),ivec2(point*dvec2(dimensions.xy)));
                    target=uint(cell.y*dimensions.x+cell.x);
                }
                atomicAdd(bins[target],1u);
                dvec2 velocity=dvec2(particles[index].velocity);
                double largest=max(abs(velocity.x),abs(velocity.y));
                speed=largest==0.0?0.0:largest*length(velocity/largest);
                if(isnan(speed)||isinf(speed)) { speed=0.0; invalid=1u; }
            }
            means[thread]=count==0?0.0:speed/double(count);
            maximums[thread]=speed;
            counts[thread]=index<uint(count)?1u:0u;
            flags[thread]=invalid;
            reduce(thread);
            if(thread==0u) summaries[gl_WorkGroupID.x]=Summary(means[0],maximums[0],counts[0],flags[0]);
        }
        """;

    internal static readonly string Reduce = Header + """

        layout(std430,binding=0) readonly buffer InputSummary { Summary inputValues[]; };
        layout(std430,binding=1) writeonly buffer OutputSummary { Summary outputValues[]; };
        void main() {
            uint index=gl_GlobalInvocationID.x,thread=gl_LocalInvocationID.x;
            Summary value=index<uint(count)?inputValues[index]:Summary(0.0,0.0,0u,0u);
            means[thread]=value.meanContribution;
            maximums[thread]=value.maximumSpeed;
            counts[thread]=value.count;
            flags[thread]=value.flags;
            reduce(thread);
            if(thread==0u) outputValues[gl_WorkGroupID.x]=Summary(means[0],maximums[0],counts[0],flags[0]);
        }
        """;
}
