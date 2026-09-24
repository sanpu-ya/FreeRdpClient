using System.Runtime.InteropServices;
using Windows.Storage.Streams;
using WinRT;

namespace FreeRdpClient.Interop;

/// <summary>Direct access to the memory of an <see cref="IBuffer"/> (IBufferByteAccess).</summary>
internal static unsafe class PixelBufferAccess
{
    private static readonly Guid IID_IBufferByteAccess = new("905a0fef-bc53-11df-8c49-001e4fc686da");

    /// <summary>
    /// Returns a pointer to the buffer's bytes. The pointer stays valid as long as the buffer
    /// (and the WriteableBitmap owning it) is alive.
    /// </summary>
    public static byte* GetPointer(IBuffer buffer)
    {
        var unknown = MarshalInterface<IBuffer>.FromManaged(buffer);
        try
        {
            var iid = IID_IBufferByteAccess;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in iid, out var byteAccess));
            try
            {
                // vtable: IUnknown (3) + Buffer(byte** value)
                var vtbl = *(IntPtr**)byteAccess;
                var getBuffer = (delegate* unmanaged[Stdcall]<IntPtr, byte**, int>)vtbl[3];
                byte* data;
                Marshal.ThrowExceptionForHR(getBuffer(byteAccess, &data));
                return data;
            }
            finally
            {
                Marshal.Release(byteAccess);
            }
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }
}
