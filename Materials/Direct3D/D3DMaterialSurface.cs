using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Color4 = Vortice.Mathematics.Color4;
using Viewport = Vortice.Mathematics.Viewport;
using Color = System.Windows.Media.Color;
using D3D9 = Vortice.Direct3D9;

namespace WindowsCustomizer;

/// <summary>
/// One surface (a taskbar's background, a menu's, ...) drawn by a Direct3D material: its passes run on the GPU into
/// textures, and the last one is shown as the surface's background through WPF's D3DImage, in the surface's shape.
///
/// What's behind the surface, the clock and the mouse come through a stand-in <see cref="MaterialEffect"/> on an
/// invisible helper the size of the surface, so the wallpaper, live glass and the frame clock (with its pause for
/// fullscreen apps) work exactly as for the other materials. It redraws when one of those changes, at most once a frame.
/// </summary>
internal sealed class D3DMaterialSurface : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Values
    {
        public Vector4 Time, Size, Mouse, Accent, Surface, Pass;
        public Vector4 P0, P1, P2, P3, P4, P5, P6, P7;
    }

    private sealed class Target : IDisposable
    {
        public required ID3D11Texture2D Texture { get; init; }
        public required ID3D11RenderTargetView View { get; init; }
        public required ID3D11ShaderResourceView Resource { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }
        public void Dispose() { Resource.Dispose(); View.Dispose(); Texture.Dispose(); }
    }

    private readonly Border _layer;
    private readonly Border _helper;
    private readonly Brush? _originalBackground;
    private readonly Material _material;
    private readonly List<byte[]> _passBytes;
    private readonly List<MaterialPass> _passes;
    private readonly MaterialEffect _standIn;
    private readonly Vector4 _surfaceColor;
    private readonly D3DImage _image = new();

    private D3DGraphics? _graphics;
    private List<ID3D11PixelShader>? _shaders;
    private readonly List<Target> _targets = new(); // one per pass; the last is the shared one shown
    private D3D9.IDirect3DTexture9? _shown9;
    private D3D9.IDirect3DSurface9? _shownSurface9;
    private ID3D11Texture2D? _behind;
    private ID3D11ShaderResourceView? _behindView;
    private int _behindWidth, _behindHeight;
    private bool _behindDirty = true;
    private ID3D11ShaderResourceView? _textureView;
    private ID3D11Buffer? _constants;
    private ID3D11Query? _done;
    private int _width, _height;
    private bool _pending, _disposed, _failed;
    private WriteableBitmap? _liveBitmap;
    private byte[] _pixels = Array.Empty<byte>();

    public D3DMaterialSurface(Border layer, Material material, List<byte[]> passes, IReadOnlyDictionary<string, string>? values, Color accent)
    {
        _layer = layer;
        _material = material;
        _passBytes = passes;
        _passes = D3DMaterialShaders.PassesOf(material);
        _originalBackground = layer.Background;
        var color = (layer.Background as SolidColorBrush)?.Color ?? Colors.Transparent;
        _surfaceColor = new Vector4(color.R / 255f, color.G / 255f, color.B / 255f, color.A / 255f);

        // The stand-in effect (a shader that draws nothing) on an invisible helper exactly over the surface.
        _standIn = new MaterialEffect(EmptyShader()) { Accent = accent, Animated = material.Animated };
        for (int i = 0; i < material.Parameters.Count; i++)
        {
            var parameter = material.Parameters[i];
            _standIn.SetParameter(i, parameter, values != null && values.TryGetValue(parameter.Name, out var v) ? v : parameter.Default);
        }
        _helper = new Border { Background = Brushes.Transparent, IsHitTestVisible = false, Effect = _standIn };
        layer.Child = _helper;
        _standIn.ValueChanged += property =>
        {
            if (property == MaterialEffect.WallpaperProperty) WatchBehind();
            Redraw();
        };

        layer.Background = new ImageBrush(_image) { Stretch = Stretch.Fill };
        _image.IsFrontBufferAvailableChanged += (_, _) =>
        {
            // Gone while the PC is locked or the screen is off (WPF drops the GPU picture); back afterwards.
            if (_image.IsFrontBufferAvailable) { ReleaseSized(); Redraw(); }
        };
        layer.SizeChanged += (_, _) => { UpdateSize(); Redraw(); };
        UpdateSize();
        MaterialClock.Add(_standIn, _helper);
        if (material.UsesWallpaper) WallpaperBehind.Track(_helper, _standIn);
        Redraw();
    }

    /// <summary>The mouse over the surface (0..1), or null.</summary>
    public void SetMouse(Point? relative) => _standIn.SetMouse(relative);

    private void UpdateSize()
    {
        var dpi = VisualTreeHelper.GetDpi(_layer);
        _standIn.SetSize(_layer.ActualWidth * dpi.DpiScaleX, _layer.ActualHeight * dpi.DpiScaleY);
    }

    // ---------- Redrawing ----------

    /// <summary>Redraws once, soon (several changes in one frame give one redraw).</summary>
    private void Redraw()
    {
        if (_pending || _disposed || _failed) return;
        _pending = true;
        _layer.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render, () =>
        {
            _pending = false;
            if (_disposed || _failed || !_layer.IsVisible) return;
            try { Draw(); }
            catch (Exception ex)
            {
                AppTrace.Log($"Material \"{_material.Name}\" (Direct3D): {ex.Message}");
                if (ex is SharpGen.Runtime.SharpGenException) D3DGraphics.Lost();
                _failed = true;
                ReleaseAll();
                _layer.Background = _originalBackground; // plain background instead
            }
        });
    }

    private void Draw()
    {
        if (!_image.IsFrontBufferAvailable) return;
        var dpi = VisualTreeHelper.GetDpi(_layer);
        int width = Math.Clamp((int)Math.Round(_layer.ActualWidth * dpi.DpiScaleX), 1, 8192);
        int height = Math.Clamp((int)Math.Round(_layer.ActualHeight * dpi.DpiScaleY), 1, 8192);
        if (_layer.ActualWidth <= 0 || _layer.ActualHeight <= 0) return;

        if (_graphics == null)
        {
            _graphics = D3DGraphics.Shared ?? throw new InvalidOperationException(D3DGraphics.Problem ?? "Direct3D isn't available.");
            D3DGraphics.AddUser();
        }
        var g = _graphics;
        _shaders ??= _passBytes.Select(b => g.Device.CreatePixelShader(b)).ToList();
        _constants ??= g.Device.CreateBuffer(new BufferDescription((uint)Marshal.SizeOf<Values>(), BindFlags.ConstantBuffer));
        _done ??= g.Device.CreateQuery(new QueryDescription(QueryType.Event));
        _textureView ??= LoadTexture(g);
        if (width != _width || height != _height || _targets.Count == 0) MakeTargets(g, width, height);
        if (_behindDirty) UploadBehind(g, width, height);

        var ctx = g.Context;
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        ctx.VSSetShader(g.Vertex);
        ctx.PSSetSampler(0, g.Clamped);
        ctx.PSSetSampler(1, g.Repeated);
        ctx.PSSetConstantBuffer(0, _constants);

        var time = _standIn.TimeValue; var size = _standIn.SizeValue; var mouse = _standIn.MouseValue;
        var accent = _standIn.Accent;
        ID3D11ShaderResourceView? previous = null;
        for (int i = 0; i < _targets.Count; i++)
        {
            var target = _targets[i];
            var values = new Values
            {
                Time = V(time), Size = V(size), Mouse = V(mouse),
                Accent = new Vector4(accent.R / 255f, accent.G / 255f, accent.B / 255f, accent.A / 255f),
                Surface = _surfaceColor,
                Pass = new Vector4(target.Width, target.Height, 1f / target.Width, 1f / target.Height),
                P0 = P(0), P1 = P(1), P2 = P(2), P3 = P(3), P4 = P(4), P5 = P(5), P6 = P(6), P7 = P(7),
            };
            ctx.UpdateSubresource(in values, _constants);
            ctx.OMSetRenderTargets(target.View);
            ctx.RSSetViewport(new Viewport(target.Width, target.Height));
            ctx.PSSetShaderResource(0, _behindView!);
            ctx.PSSetShaderResource(1, previous!);
            ctx.PSSetShaderResource(2, _textureView);
            ctx.PSSetShader(_shaders[i]);
            ctx.ClearRenderTargetView(target.View, new Color4(0, 0, 0, 0));
            ctx.Draw(3, 0);
            // This pass's result is the next one's PREVIOUS; unbind it as a target first.
            ctx.OMSetRenderTargets((ID3D11RenderTargetView)null!);
            previous = target.Resource;
        }
        ctx.PSSetShaderResource(1, (ID3D11ShaderResourceView)null!);

        // Wait until the GPU has drawn it, then show it.
        ctx.End(_done);
        ctx.Flush();
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (!ctx.IsDataAvailable(_done) && waited.ElapsedMilliseconds < 50) Thread.Yield();

        _image.Lock();
        _image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, _shownSurface9!.NativePointer);
        _image.AddDirtyRect(new Int32Rect(0, 0, width, height));
        _image.Unlock();
    }

    private static Vector4 V(System.Windows.Media.Media3D.Point4D p) => new((float)p.X, (float)p.Y, (float)p.Z, (float)p.W);
    private Vector4 P(int i) => V(_standIn.ParameterValue(i));

    /// <summary>A texture per pass (at its share of the size); the last is full size and shared with Direct3D 9 for WPF.</summary>
    private void MakeTargets(D3DGraphics g, int width, int height)
    {
        ReleaseSized();
        _width = width;
        _height = height;
        for (int i = 0; i < _passes.Count; i++)
        {
            bool last = i == _passes.Count - 1;
            double scale = last ? 1 : Math.Clamp(_passes[i].Scale, 0.1, 1);
            int w = Math.Max(1, (int)Math.Round(width * scale)), h = Math.Max(1, (int)Math.Round(height * scale));
            var texture = g.Device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                MiscFlags = last ? ResourceOptionFlags.Shared : ResourceOptionFlags.None,
            });
            _targets.Add(new Target
            {
                Texture = texture, View = g.Device.CreateRenderTargetView(texture),
                Resource = g.Device.CreateShaderResourceView(texture), Width = w, Height = h,
            });
            if (!last) continue;
            // The same picture, opened by Direct3D 9 (what D3DImage shows).
            using var resource = texture.QueryInterface<IDXGIResource>();
            IntPtr handle = resource.SharedHandle;
            _shown9 = g.Device9.CreateTexture((uint)w, (uint)h, 1, D3D9.Usage.RenderTarget, D3D9.Format.A8R8G8B8, D3D9.Pool.Default, ref handle);
            _shownSurface9 = _shown9.GetSurfaceLevel(0);
        }
        _behindDirty = true;
    }

    // ---------- What's behind ----------

    /// <summary>Live glass changes its picture in place: redraw whenever it does.</summary>
    private void WatchBehind()
    {
        if (_liveBitmap != null) _liveBitmap.Changed -= OnLiveChanged;
        _liveBitmap = (_standIn.Wallpaper as ImageBrush)?.ImageSource as WriteableBitmap;
        if (_liveBitmap != null) _liveBitmap.Changed += OnLiveChanged;
        _behindDirty = true;
    }

    private void OnLiveChanged(object? sender, EventArgs e) { _behindDirty = true; Redraw(); }

    /// <summary>The picture behind the surface, at the surface's pixel size, into a texture.</summary>
    private void UploadBehind(D3DGraphics g, int width, int height)
    {
        _behindDirty = false;
        BitmapSource? source = null;
        if (_liveBitmap != null) source = _liveBitmap;
        else if (_standIn.Wallpaper is { } brush)
        {
            // The wallpaper brush shows exactly the part behind the surface: drawn once at the surface's size.
            int w = Math.Min(width, 4096), h = Math.Min(height, 4096);
            var rectangle = new Rectangle { Width = w, Height = h, Fill = brush };
            rectangle.Measure(new Size(w, h));
            rectangle.Arrange(new Rect(0, 0, w, h));
            var picture = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            picture.Render(rectangle);
            source = picture;
        }
        if (source == null) return;
        if (source.Format != PixelFormats.Bgra32 && source.Format != PixelFormats.Pbgra32 && source.Format != PixelFormats.Bgr32)
            source = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int sw = source.PixelWidth, sh = source.PixelHeight;
        if (sw <= 0 || sh <= 0) return;
        // One buffer, reused: a new one every frame (live glass) would pile up in .NET's large-object heap.
        if (_pixels.Length != sw * sh * 4) _pixels = new byte[sw * sh * 4];
        source.CopyPixels(_pixels, sw * 4, 0);

        if (_behind == null || _behindWidth != sw || _behindHeight != sh)
        {
            _behindView?.Dispose();
            _behind?.Dispose();
            _behind = g.Device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)sw, Height = (uint)sh, MipLevels = 1, ArraySize = 1, Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource,
            });
            _behindView = g.Device.CreateShaderResourceView(_behind);
            _behindWidth = sw;
            _behindHeight = sh;
        }
        g.Context.UpdateSubresource(_pixels.AsSpan(), _behind, 0, (uint)(sw * 4), 0);
    }

    /// <summary>The material's picture (repeated), or one white pixel.</summary>
    private ID3D11ShaderResourceView LoadTexture(D3DGraphics g)
    {
        BitmapSource? image = null;
        if (_material.Texture is { Length: > 0 } file && System.IO.File.Exists(System.IO.Path.Combine(_material.Folder, file)))
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(System.IO.Path.Combine(_material.Folder, file));
                bitmap.EndInit();
                image = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
            }
            catch (Exception ex) { AppTrace.Log($"Material \"{_material.Name}\" texture: {ex.Message}"); }
        }
        image ??= BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 255, 255, 255, 255 }, 4);
        int w = image.PixelWidth, h = image.PixelHeight;
        var pixels = new byte[w * h * 4];
        image.CopyPixels(pixels, w * 4, 0);
        var texture = g.Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource,
        });
        g.Context.UpdateSubresource(pixels.AsSpan(), texture, 0, (uint)(w * 4), 0);
        var view = g.Device.CreateShaderResourceView(texture);
        texture.Dispose(); // the view keeps it
        return view;
    }

    // ---------- Cleaning up ----------

    private void ReleaseSized()
    {
        foreach (var t in _targets) t.Dispose();
        _targets.Clear();
        _shownSurface9?.Dispose(); _shownSurface9 = null;
        _shown9?.Dispose(); _shown9 = null;
        _width = _height = 0;
    }

    private void ReleaseAll()
    {
        ReleaseSized();
        _behindView?.Dispose(); _behindView = null;
        _behind?.Dispose(); _behind = null;
        _textureView?.Dispose(); _textureView = null;
        _constants?.Dispose(); _constants = null;
        _done?.Dispose(); _done = null;
        if (_shaders != null) foreach (var s in _shaders) s.Dispose();
        _shaders = null;
        if (_graphics != null) D3DGraphics.RemoveUser();
        _graphics = null;
        _pixels = Array.Empty<byte>();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_liveBitmap != null) _liveBitmap.Changed -= OnLiveChanged;
        _image.Lock();
        _image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, IntPtr.Zero);
        _image.Unlock();
        ReleaseAll();
        if (ReferenceEquals(_layer.Child, _helper)) _layer.Child = null;
        _layer.Background = _originalBackground;
    }

    // ---------- The stand-in's shader ----------

    private static System.Windows.Media.Effects.PixelShader? _empty;

    /// <summary>A WPF shader that draws nothing: the stand-in only carries the values.</summary>
    private static System.Windows.Media.Effects.PixelShader EmptyShader()
    {
        if (_empty != null) return _empty;
        var bytes = ShaderCompiler.Compile("sampler2D s0 : register(s0); float4 main(float2 uv : TEXCOORD) : COLOR { return 0; }", "empty.hlsl", out var error)
                    ?? throw new InvalidOperationException(error);
        var shader = new System.Windows.Media.Effects.PixelShader();
        shader.SetStreamSource(new System.IO.MemoryStream(bytes));
        shader.Freeze();
        return _empty = shader;
    }
}
