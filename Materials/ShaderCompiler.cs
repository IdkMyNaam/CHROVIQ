using System.Runtime.InteropServices;
using System.Text;

namespace WindowsCustomizer;

/// <summary>
/// Compiles HLSL pixel shaders at runtime with Windows' own shader compiler (d3dcompiler_47.dll, part of
/// Windows), to the shader model WPF runs (ps_3_0). Returns the bytecode, or the compiler's error text.
/// </summary>
internal static class ShaderCompiler
{
    [ComImport, Guid("8BA5FB08-5195-40E2-AC58-0D989C3A0102"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ID3DBlob
    {
        [PreserveSig] IntPtr GetBufferPointer();
        [PreserveSig] UIntPtr GetBufferSize();
    }

    [DllImport("d3dcompiler_47.dll", CharSet = CharSet.Ansi)]
    private static extern int D3DCompile(
        [MarshalAs(UnmanagedType.LPStr)] string source, UIntPtr sourceSize, [MarshalAs(UnmanagedType.LPStr)] string? sourceName,
        IntPtr defines, IntPtr include, [MarshalAs(UnmanagedType.LPStr)] string entryPoint, [MarshalAs(UnmanagedType.LPStr)] string target,
        uint flags1, uint flags2, out ID3DBlob? code, out ID3DBlob? errors);

    private const uint D3DCOMPILE_OPTIMIZATION_LEVEL3 = 1 << 15;

    /// <summary>Compiles; on failure <paramref name="error"/> has the compiler's messages (with line numbers of the file).</summary>
    public static byte[]? Compile(string source, string fileName, out string? error) =>
        Compile(source, fileName, "main", "ps_3_0", out error);

    /// <summary>Compiles one entry point to a shader model ("ps_3_0" for WPF, "ps_5_0" / "vs_5_0" for Direct3D 11).</summary>
    public static byte[]? Compile(string source, string fileName, string entryPoint, string target, out string? error)
    {
        error = null;
        ID3DBlob? code = null, errors = null;
        try
        {
            int bytes = Encoding.ASCII.GetByteCount(source);
            int hr = D3DCompile(source, (UIntPtr)bytes, fileName, IntPtr.Zero, IntPtr.Zero, entryPoint, target,
                                D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, out code, out errors);
            if (hr != 0 || code == null)
            {
                error = errors != null ? Marshal.PtrToStringAnsi(errors.GetBufferPointer(), (int)errors.GetBufferSize()).TrimEnd('\0', '\r', '\n')
                                       : $"The shader compiler failed (0x{hr:X8}).";
                return null;
            }
            var result = new byte[(int)code.GetBufferSize()];
            Marshal.Copy(code.GetBufferPointer(), result, 0, result.Length);
            return result;
        }
        catch (DllNotFoundException) { error = "Windows' shader compiler (d3dcompiler_47.dll) was not found."; return null; }
        catch (Exception ex) { error = ex.Message; return null; }
        finally
        {
            if (code != null) Marshal.ReleaseComObject(code);
            if (errors != null) Marshal.ReleaseComObject(errors);
        }
    }
}
