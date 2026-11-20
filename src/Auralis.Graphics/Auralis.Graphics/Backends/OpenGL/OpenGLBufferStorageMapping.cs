using Auralis.Graphics.Resources;
using Silk.NET.OpenGL;

namespace Auralis.Graphics.Backends.OpenGL;

/// <summary>
/// Maps backend-neutral CPU access intent to immutable GL buffer-storage flags. MemoryPreference
/// remains a hint because OpenGL does not expose Vulkan/D3D12-style physical heap selection.
/// </summary>
internal static class OpenGLBufferStorageMapping
{
    internal static BufferStorageMask GetStorageFlags(GraphicsBufferDescription description)
    {
        // CPU writes use glNamedBufferSubData from the staging-backed Unmap path and therefore
        // require DYNAMIC_STORAGE_BIT. Readback uses GetNamedBufferSubData and needs no map flag.
        return description.MemoryAccess is GraphicsMemoryAccess.CpuToGpu or GraphicsMemoryAccess.CpuReadWrite
            ? BufferStorageMask.DynamicStorageBit
            : (BufferStorageMask)0;
    }

}
