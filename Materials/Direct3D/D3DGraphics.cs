using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using D3D9 = Vortice.Direct3D9;

namespace WindowsCustomizer;

/// <summary>
/// The Direct3D devices all Direct3D materials share: Direct3D 11 draws them, Direct3D 9Ex hands the pictures to
/// WPF (its D3DImage only takes Direct3D 9 surfaces; the two share the picture without copying it). Made the first
/// time a Direct3D material is used; if that fails (no suitable graphics card, remote desktop), Direct3D materials
/// show the surface's plain background instead, and <see cref="Problem"/> says why.
/// </summary>
internal sealed class D3DGraphics : IDisposable
{
    private static D3DGraphics? _shared;
    private static bool _tried;

    /// <summary>Why Direct3D isn't available, or null.</summary>
    public static string? Problem { get; private set; }

    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }
    public D3D9.IDirect3DDevice9Ex Device9 { get; }
    public ID3D11VertexShader Vertex { get; }
    public ID3D11SamplerState Clamped { get; }
    public ID3D11SamplerState Repeated { get; }
    private readonly D3D9.IDirect3D9Ex _d3d9;

    [DllImport("user32.dll")] private static extern IntPtr GetDesktopWindow();

    /// <summary>The shared devices, made on first use; null when Direct3D isn't available here.</summary>
    public static D3DGraphics? Shared
    {
        get
        {
            if (_shared != null || _tried) return _shared;
            _tried = true;
            try { _shared = new D3DGraphics(); }
            catch (Exception ex)
            {
                Problem = ex.Message;
                AppTrace.Log($"Direct3D materials: not available ({ex.Message})");
            }
            return _shared;
        }
    }

    private static int _users;
    private static System.Windows.Threading.DispatcherTimer? _releaseTimer;

    /// <summary>A surface started drawing with the devices.</summary>
    public static void AddUser()
    {
        _users++;
        _releaseTimer?.Stop();
    }

    /// <summary>
    /// A surface stopped. When none is left, the devices (and the memory the graphics driver keeps for them) are released
    /// after 15 seconds: not at once, since a bar being rebuilt starts drawing again right away.
    /// </summary>
    public static void RemoveUser()
    {
        _users = Math.Max(0, _users - 1);
        if (_users > 0 || _shared == null) return;
        if (_releaseTimer == null)
        {
            _releaseTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
            _releaseTimer.Tick += (_, _) =>
            {
                _releaseTimer!.Stop();
                if (_users > 0 || _shared == null) return;
                AppTrace.Log("Direct3D materials: none in use; devices released");
                try { _shared.Dispose(); } catch (Exception) { /* already gone */ }
                _shared = null;
                _tried = false;
            };
        }
        _releaseTimer.Stop();
        _releaseTimer.Start();
    }

    /// <summary>The devices stopped working (a driver update, a graphics card reset): made again on next use.</summary>
    public static void Lost()
    {
        AppTrace.Log("Direct3D materials: device lost; starting again");
        try { _shared?.Dispose(); } catch (Exception) { /* already broken */ }
        _shared = null;
        _tried = false;
    }

    private D3DGraphics()
    {
        D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            new[] { FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 },
            out ID3D11Device? device, out ID3D11DeviceContext? context).CheckError();
        Device = device!;
        Context = context!;

        _d3d9 = D3D9.D3D9.Direct3DCreate9Ex();
        var present = new D3D9.PresentParameters
        {
            Windowed = true,
            BackBufferWidth = 1,
            BackBufferHeight = 1,
            BackBufferCount = 1,
            SwapEffect = D3D9.SwapEffect.Discard,
            DeviceWindowHandle = GetDesktopWindow(),
            PresentationInterval = D3D9.PresentInterval.Default,
        };
        Device9 = _d3d9.CreateDeviceEx(0, D3D9.DeviceType.Hardware, IntPtr.Zero,
            D3D9.CreateFlags.HardwareVertexProcessing | D3D9.CreateFlags.Multithreaded | D3D9.CreateFlags.FpuPreserve, present);

        var vertex = ShaderCompiler.Compile(D3DMaterialShaders.VertexSource, "vertex.hlsl", "main", "vs_5_0", out var error)
                     ?? throw new InvalidOperationException(error);
        Vertex = Device.CreateVertexShader(vertex);
        Clamped = Device.CreateSamplerState(SamplerDescription.LinearClamp);
        Repeated = Device.CreateSamplerState(SamplerDescription.LinearWrap);
        AppTrace.Log($"Direct3D materials: ready ({Device.FeatureLevel})");
    }

    public void Dispose()
    {
        Clamped.Dispose();
        Repeated.Dispose();
        Vertex.Dispose();
        Context.Dispose();
        Device.Dispose();
        Device9.Dispose();
        _d3d9.Dispose();
    }
}
