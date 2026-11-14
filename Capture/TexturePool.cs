using System.Runtime.InteropServices;
using LiteRec.Interop;

namespace LiteRec.Capture;

/// <summary>
/// A fixed set of GPU textures that carry frames from the capturer to the encoder. Reusing textures
/// avoids allocating video memory for every frame, and the fixed size puts a hard cap on memory use:
/// if the encoder ever falls behind, frames are dropped instead of piling up.
///
/// The encoder pipeline takes a COM reference on a texture for as long as it needs it, so a
/// texture is free exactly when the pool holds the only remaining reference.
/// </summary>
internal sealed class TexturePool : IDisposable
{
    private readonly IntPtr[] _textures;

    public TexturePool(D3DDevice device, Size size, int count)
    {
        _textures = new IntPtr[count];
        for (int i = 0; i < count; i++)
        {
            // Texture 0 starts out black: it is shown until the first captured frame arrives.
            _textures[i] = device.CreateTexture(size.Width, size.Height, zeroFill: i == 0);
        }
    }

    public IntPtr GetTexture(int index) => _textures[index];

    /// <summary>Number of queued or in-progress encoder samples that still reference this texture.</summary>
    public int GetPendingUses(int index)
    {
        IntPtr texture = _textures[index];
        Marshal.AddRef(texture);
        return Marshal.Release(texture) - 1; // minus the pool's own reference
    }

    /// <summary>
    /// Finds a texture the encoder is no longer reading. <paramref name="keep"/> is the texture that
    /// holds the most recent picture and must stay intact so it can be repeated. Returns -1 if all are busy.
    /// </summary>
    public int AcquireFree(int keep)
    {
        for (int i = 0; i < _textures.Length; i++)
        {
            if (i != keep && GetPendingUses(i) == 0)
            {
                return i;
            }
        }

        return -1;
    }

    public void Dispose()
    {
        for (int i = 0; i < _textures.Length; i++)
        {
            if (_textures[i] != IntPtr.Zero)
            {
                Marshal.Release(_textures[i]);
                _textures[i] = IntPtr.Zero;
            }
        }
    }
}
