namespace ParticleLife.MonoGame.Gpu;

/// <summary>Defines resident grid, scan, integration and reduction stages with explicit storage roles.</summary>
/// <remarks>Every force reads one immutable logical-slot state; scatter reorders indices only. Hash
/// collisions are filtered by full signed cell coordinates, and no bucket has an occupancy limit.
/// Periodic cell visits are deduplicated before traversal. The pair curve is directed: r/core-1 below
/// a positive core and a triangular coefficient between core and cutoff; coincidence contributes zero.
/// Integration uses v'=damping*v+dt*force then x'=x+dt*v', followed by the selected boundary mapping.
/// Any non-finite/range failure marks the unpublished candidate; the host retains completed input.
/// Separate dispatches and shader-storage barriers are required between every dependent stage.
/// Periodic stencil coordinates need at most one addition/subtraction of the cell count. Do not use
/// signed integer remainder on negative cells: GLSL 4.60 section 5.9 leaves that operation undefined
/// (https://registry.khronos.org/OpenGL/specs/gl/GLSLangSpec.4.60.html).</remarks>
internal static class GpuKernels
{
    private const string Header = """
        #version 430
        layout(local_size_x = 256) in;
        struct Particle { vec2 position; vec2 velocity; uvec2 identity; uint species; uint padding; };
        uniform int particleCount;
        uniform int bucketMask;
        uniform ivec4 grid; // periodic cells x/y, boundary (soft/open/reflect/periodic), species count
        uniform vec4 domain; // width, height, cell width, cell height
        bool finiteValue(float value) { return !isnan(value) && !isinf(value); }
        bool finiteVector(vec2 value) { return finiteValue(value.x) && finiteValue(value.y); }
        float wrapValue(float value, float extent) {
            if (value >= 0.0 && value < extent) return value;
            float result = mod(value, extent);
            if (result < 0.0) result += extent;
            return result >= extent ? 0.0 : result;
        }
        vec2 wrapped(vec2 position) {
            return vec2(wrapValue(position.x, domain.x), wrapValue(position.y, domain.y));
        }
        ivec2 cellFor(vec2 position, out bool valid) {
            if (grid.z == 3) {
                vec2 scaled = wrapped(position) / domain.zw;
                valid = finiteVector(scaled);
                return ivec2(clamp(floor(scaled), vec2(0), vec2(grid.xy - ivec2(1))));
            }
            vec2 scaled = position / domain.zw;
            valid = finiteVector(scaled) && all(lessThanEqual(abs(scaled), vec2(1048576.0)));
            vec2 floored = floor(clamp(scaled, vec2(-1048576.0), vec2(1048576.0)));
            if (scaled.x == 0.0 && position.x < 0.0) floored.x = -1.0;
            if (scaled.y == 0.0 && position.y < 0.0) floored.y = -1.0;
            return ivec2(floored);
        }
        uint bucketFor(ivec2 cell) {
            uint hash = uint(cell.x) * 0x9E3779B1u ^ uint(cell.y) * 0x85EBCA6Bu;
            return (hash ^ (hash >> 16)) & uint(bucketMask);
        }
        """;

    internal const string Clear = """
        #version 430
        layout(local_size_x = 256) in;
        layout(std430, binding = 2) buffer Counts { uint counts[]; };
        layout(std430, binding = 7) buffer Status { uint flags; uint maximumOccupancy; uint failedSlot; uint reserved; };
        uniform int bucketCount;
        uniform int clearStatus;
        void main() {
            uint index = gl_GlobalInvocationID.x;
            if (index < uint(bucketCount)) counts[index] = 0u;
            if (index == 0u && clearStatus != 0) { flags=0u; maximumOccupancy=0u; failedSlot=0xFFFFFFFFu; reserved=0u; }
        }
        """;

    internal static readonly string Count = Header + """

        layout(std430, binding = 0) readonly buffer Input { Particle particles[]; };
        layout(std430, binding = 2) buffer Counts { uint counts[]; };
        layout(std430, binding = 5) writeonly buffer Cells { ivec2 cells[]; };
        layout(std430, binding = 7) buffer Status { uint flags; uint maximumOccupancy; uint failedSlot; uint reserved; };
        void main() {
            uint index=gl_GlobalInvocationID.x;
            if(index>=uint(particleCount)) return;
            bool valid;
            ivec2 cell=cellFor(particles[index].position,valid);
            if(!valid) { atomicOr(flags,1u); atomicMin(failedSlot,index); }
            cells[index]=cell;
            uint occupancy=atomicAdd(counts[bucketFor(cell)],1u)+1u;
            atomicMax(maximumOccupancy,occupancy);
        }
        """;

    /// <summary>Performs an exclusive 512-element Blelloch block scan and emits one sum per block.</summary>
    /// <remarks>The owner recursively scans block sums and adds their offsets back down the hierarchy.
    /// Work and storage are linear in the bucket count; no single workgroup scans the entire table.</remarks>
    internal const string Scan = """
        #version 430
        layout(local_size_x = 256) in;
        layout(std430,binding=0) readonly buffer Input { uint inputValues[]; };
        layout(std430,binding=1) writeonly buffer Output { uint outputValues[]; };
        layout(std430,binding=2) writeonly buffer Sums { uint sums[]; };
        uniform int count;
        shared uint values[512];
        void main() {
            uint thread=gl_LocalInvocationID.x;
            uint first=gl_WorkGroupID.x*512u+thread;
            uint second=first+256u;
            values[thread]=first<uint(count)?inputValues[first]:0u;
            values[thread+256u]=second<uint(count)?inputValues[second]:0u;
            barrier();
            for(uint width=1u;width<512u;width*=2u) {
                uint index=(thread+1u)*width*2u-1u;
                if(index<512u) values[index]+=values[index-width];
                barrier();
            }
            if(thread==0u) { sums[gl_WorkGroupID.x]=values[511]; values[511]=0u; }
            barrier();
            for(uint width=256u;width>0u;width/=2u) {
                uint index=(thread+1u)*width*2u-1u;
                if(index<512u) { uint left=values[index-width]; values[index-width]=values[index]; values[index]+=left; }
                barrier();
            }
            if(first<uint(count)) outputValues[first]=values[thread];
            if(second<uint(count)) outputValues[second]=values[thread+256u];
        }
        """;

    internal const string AddScanOffsets = """
        #version 430
        layout(local_size_x = 256) in;
        layout(std430,binding=0) buffer Values { uint values[]; };
        layout(std430,binding=1) readonly buffer Offsets { uint offsets[]; };
        uniform int count;
        void main() {
            uint index=gl_GlobalInvocationID.x;
            if(index<uint(count)) values[index]+=offsets[index/512u];
        }
        """;

    internal static readonly string Scatter = Header + """

        layout(std430,binding=2) buffer Cursors { uint cursors[]; };
        layout(std430,binding=3) readonly buffer Offsets { uint offsets[]; };
        layout(std430,binding=4) writeonly buffer SortedIndices { uint indices[]; };
        layout(std430,binding=5) readonly buffer Cells { ivec2 cells[]; };
        void main() {
            uint index=gl_GlobalInvocationID.x;
            if(index>=uint(particleCount)) return;
            uint bucket=bucketFor(cells[index]);
            indices[offsets[bucket]+atomicAdd(cursors[bucket],1u)]=index;
        }
        """;

    internal static readonly string Integrate = Header + """

        layout(std430,binding=0) readonly buffer Input { Particle inputState[]; };
        layout(std430,binding=1) writeonly buffer Output { Particle outputState[]; };
        layout(std430,binding=2) readonly buffer Counts { uint counts[]; };
        layout(std430,binding=3) readonly buffer Offsets { uint offsets[]; };
        layout(std430,binding=4) readonly buffer SortedIndices { uint indices[]; };
        layout(std430,binding=5) readonly buffer Cells { ivec2 cells[]; };
        layout(std430,binding=6) readonly buffer Rules { vec4 rules[]; }; // coefficient, core, cutoff, reserved
        layout(std430,binding=7) buffer Status { uint flags; uint maximumOccupancy; uint failedSlot; uint reserved; };
        uniform ivec4 receiverRange; // first, count, reserved, reserved
        uniform vec4 dynamics; // timestep, damping, force scale, wall strength
        uniform vec4 wall; // layer width, reserved...
        vec2 displacement(vec2 receiver,vec2 source) {
            vec2 delta;
            if(grid.z==3) {
                delta=wrapped(source)-wrapped(receiver);
                vec2 halfExtent=domain.xy*0.5;
                if(delta.x>halfExtent.x) delta.x-=domain.x;
                else if(delta.x< -halfExtent.x) delta.x+=domain.x;
                if(delta.y>halfExtent.y) delta.y-=domain.y;
                else if(delta.y< -halfExtent.y) delta.y+=domain.y;
            } else delta=source-receiver;
            return delta;
        }
        vec2 pairForce(Particle receiver,Particle source) {
            vec2 delta=displacement(receiver.position,source.position);
            if(all(equal(delta,vec2(0)))) return vec2(0);
            vec4 rule=rules[receiver.species*uint(grid.w)+source.species];
            float core=rule.y,cutoff=rule.z;
            if(any(greaterThanEqual(abs(delta),vec2(cutoff)))) return vec2(0);
            float squared=dot(delta,delta);
            if(squared>=cutoff*cutoff) return vec2(0);
            float distance;
            vec2 direction;
            if(squared<1.17549435e-38) {
                float largest=max(abs(delta.x),abs(delta.y));
                vec2 scaled=delta/largest;
                float scaledLength=length(scaled);
                distance=largest*scaledLength;
                direction=scaled/scaledLength;
            } else { distance=sqrt(squared); direction=delta/distance; }
            if(distance>=cutoff) return vec2(0);
            float coefficient=core>0.0 && distance<core ? distance/core-1.0
                : rule.x*(1.0-abs(2.0*(distance-core)/(cutoff-core)-1.0));
            return direction*(dynamics.z*coefficient);
        }
        float wallForce(float position,float extent) {
            if(position<wall.x) return dynamics.w*(1.0-position/wall.x);
            if(extent-position<wall.x) return -dynamics.w*(1.0-(extent-position)/wall.x);
            return 0.0;
        }
        void reflectCoordinate(inout float position,inout float velocity,float extent) {
            float folded=wrapValue(position,2.0*extent);
            if(folded==0.0) { position=0.0; velocity=abs(velocity); }
            else if(folded==extent) { position=extent; velocity=-abs(velocity); }
            else if(folded<extent) position=folded;
            else { position=2.0*extent-folded; velocity=-velocity; }
        }
        void main() {
            uint localIndex=gl_GlobalInvocationID.x;
            if(localIndex>=uint(receiverRange.y)) return;
            uint index=uint(receiverRange.x)+localIndex;
            if(index>=uint(particleCount)) return;
            Particle receiver=inputState[index];
            vec2 force=vec2(0);
            if(dynamics.z!=0.0) {
                ivec2 visited[9]; int visitedCount=0;
                for(int y=-1;y<=1;y++) for(int x=-1;x<=1;x++) {
                    ivec2 cell=cells[index]+ivec2(x,y);
                    if(grid.z==3) {
                        // A stencil coordinate lies in [-1, cells]. GLSL integer remainder with a
                        // negative operand is undefined; one conditional correction is exact here.
                        if(cell.x<0) cell.x+=grid.x;
                        else if(cell.x>=grid.x) cell.x-=grid.x;
                        if(cell.y<0) cell.y+=grid.y;
                        else if(cell.y>=grid.y) cell.y-=grid.y;
                        bool duplicate=false;
                        for(int prior=0;prior<visitedCount;prior++) if(all(equal(visited[prior],cell))) duplicate=true;
                        if(duplicate) continue;
                    }
                    visited[visitedCount++]=cell;
                    uint bucket=bucketFor(cell);
                    uint end=offsets[bucket]+counts[bucket];
                    for(uint cursor=offsets[bucket];cursor<end;cursor++) {
                        uint source=indices[cursor];
                        if(source!=index && all(equal(cells[source],cell))) force+=pairForce(receiver,inputState[source]);
                    }
                }
            }
            if(grid.z==0 && dynamics.w!=0.0) force+=vec2(wallForce(receiver.position.x,domain.x),wallForce(receiver.position.y,domain.y));
            Particle next=receiver;
            next.velocity=dynamics.y*receiver.velocity+dynamics.x*force;
            next.position=receiver.position+dynamics.x*next.velocity;
            bool valid=finiteVector(force)&&finiteVector(next.velocity)&&finiteVector(next.position);
            if(valid && grid.z==3) next.position=wrapped(next.position);
            else if(valid && grid.z==2) {
                reflectCoordinate(next.position.x,next.velocity.x,domain.x);
                reflectCoordinate(next.position.y,next.velocity.y,domain.y);
            }
            bool inRange=false;
            if(valid) { ivec2 ignored=cellFor(next.position,inRange); }
            if(!valid || !inRange) {
                atomicOr(flags,valid?4u:2u); atomicMin(failedSlot,index);
                outputState[index]=receiver;
            } else outputState[index]=next;
        }
        """;

    private const string ReductionHeader = """
        #version 430
        layout(local_size_x=256) in;
        struct Summary { float meanContribution; float maximumSpeed; uint count; uint flags; };
        uniform int count;
        shared float means[256]; shared float maximums[256]; shared uint counts[256]; shared uint flags[256];
        void reduce(uint thread) {
            barrier();
            for(uint width=128u;width>0u;width/=2u) {
                if(thread<width) { means[thread]+=means[thread+width]; maximums[thread]=max(maximums[thread],maximums[thread+width]);
                    counts[thread]+=counts[thread+width]; flags[thread]|=flags[thread+width]; }
                barrier();
            }
        }
        """;

    internal static readonly string SummarizeParticles = ReductionHeader + """

        struct Particle { vec2 position; vec2 velocity; uvec2 identity; uint species; uint padding; };
        layout(std430,binding=0) readonly buffer Input { Particle particles[]; };
        layout(std430,binding=1) writeonly buffer Output { Summary outputValues[]; };
        void main() {
            uint index=gl_GlobalInvocationID.x,thread=gl_LocalInvocationID.x;
            float speed=0.0; uint invalid=0u;
            if(index<uint(count)) {
                vec2 velocity=particles[index].velocity;
                float largest=max(abs(velocity.x),abs(velocity.y));
                speed=largest==0.0?0.0:largest*length(velocity/largest);
                if(isnan(speed)||isinf(speed)) { invalid=1u; speed=0.0; }
            }
            means[thread]=count==0?0.0:speed/float(count); maximums[thread]=speed;
            counts[thread]=index<uint(count)?1u:0u; flags[thread]=invalid;
            reduce(thread);
            if(thread==0u) outputValues[gl_WorkGroupID.x]=Summary(means[0],maximums[0],counts[0],flags[0]);
        }
        """;

    internal static readonly string ReduceSummaries = ReductionHeader + """

        layout(std430,binding=0) readonly buffer Input { Summary inputValues[]; };
        layout(std430,binding=1) writeonly buffer Output { Summary outputValues[]; };
        void main() {
            uint index=gl_GlobalInvocationID.x,thread=gl_LocalInvocationID.x;
            Summary value=index<uint(count)?inputValues[index]:Summary(0.0,0.0,0u,0u);
            means[thread]=value.meanContribution; maximums[thread]=value.maximumSpeed; counts[thread]=value.count; flags[thread]=value.flags;
            reduce(thread);
            if(thread==0u) outputValues[gl_WorkGroupID.x]=Summary(means[0],maximums[0],counts[0],flags[0]);
        }
        """;
}
