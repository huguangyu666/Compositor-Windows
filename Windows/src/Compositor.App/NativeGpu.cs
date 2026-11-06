using System.Numerics;
using System.Runtime.InteropServices;
using Compositor.Core;
using Microsoft.Graphics.Canvas;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.Direct3D11.Shader;
using Vortice.D3DCompiler;
using Vortice.DXGI;
using Windows.Graphics.DirectX.Direct3D11;

namespace Compositor.App;

// Native bindings: Vortice.Windows, Amer Koleci and contributors, MIT.
// https://github.com/amerkoleci/Vortice.Windows
// Win2D (Microsoft, MIT) owns the shared device and presents the shared DXGI surface.
// https://learn.microsoft.com/windows/apps/develop/win2d/interop
// Pixel algorithms remain Compositor's CPU reference; this class only hosts the passes.
internal sealed class NativeGpu : IDisposable
{
    private readonly CanvasDevice canvasDevice;
    private readonly ID3D11Device device;
    private readonly ID3D11DeviceContext1 context;
    private readonly ID3DDeviceContextState state;
    private readonly ID3D11VertexShader vertex;
    private readonly ID3D11ShaderResourceView[] shaderViews = new ID3D11ShaderResourceView[8];
    private static readonly ID3D11ShaderResourceView[] EmptyViews = new ID3D11ShaderResourceView[8];
    internal long TextureBudget { get; private set; } = 384L << 20;
#if DEBUG
    internal long UploadedBytes { get; private set; }
#endif

    internal unsafe NativeGpu(CanvasDevice canvasDevice)
    {
        this.canvasDevice = canvasDevice;
        nint abi = WinRT.MarshalInterface<IDirect3DDevice>.FromManaged(canvasDevice);
        nint access = 0;
        try
        {
            var accessId = new Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(abi, in accessId, out access));
            var getInterface = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)(*(nint**)access)[3];
            var deviceId = typeof(ID3D11Device).GUID;
            nint native = 0;
            Marshal.ThrowExceptionForHR(getInterface(access, &deviceId, &native));
            device = new(native);
        }
        finally { if (access != 0) Marshal.Release(access); Marshal.Release(abi); }
        try
        {
            using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
            using var adapter = dxgiDevice.GetAdapter();
            using var adapter1 = adapter.QueryInterface<IDXGIAdapter1>();
            if ((adapter1.Description1.Flags & AdapterFlags.Software) != 0)
                throw new NotSupportedException("No hardware GPU is available; use the CPU compositor.");
            try
            {
                using var adapter3 = adapter.QueryInterfaceOrNull<IDXGIAdapter3>();
                if (adapter3 is not null)
                {
                    var memory = adapter3.QueryVideoMemoryInfo(0, MemorySegmentGroup.Local);
                    TextureBudget = Math.Clamp((long)memory.Budget / 4, 64L << 20, 1L << 30);
                }
            }
            catch (SharpGen.Runtime.SharpGenException) { /* Keep the conservative budget on older drivers. */ }
            using var immediate = device.ImmediateContext;
            context = immediate.QueryInterface<ID3D11DeviceContext1>();
            using var extended = device.QueryInterface<ID3D11Device1>();
            state = extended.CreateDeviceContextState<ID3D11Device>(CreateDeviceContextStateFlags.None, [device.FeatureLevel], out _);
            vertex = device.CreateVertexShader(ShaderBytes("Fullscreen"));
        }
        catch { Dispose(); throw; }
    }

    internal IDisposable Begin()
    {
        var canvasLock = canvasDevice.Lock();
        try { return new Session(this, context.SwapDeviceContextState(state), canvasLock); }
        catch { canvasLock.Dispose(); throw; }
    }

    private sealed class Session(NativeGpu owner, ID3DDeviceContextState previous, IDisposable canvasLock) : IDisposable
    {
        public void Dispose()
        {
            try
            {
                // Release bindings too: a retired source must not survive in a saved context state.
                owner.context.ClearState();
                using var unused = owner.context.SwapDeviceContextState(previous);
            }
            finally { previous.Dispose(); canvasLock.Dispose(); }
        }
    }

    internal GpuTexture Texture(ReadOnlySpan<byte> bytes, int width, int height, Format format, int stride = 0)
    {
        var result = new GpuTexture(this, width, height, format, false);
        try
        {
            if (stride == 0) result.SetPixelBytes(bytes);
            else result.SetPixels(bytes, stride, new(0, 0, width, height));
            return result;
        }
        catch { result.Dispose(); throw; }
    }
    internal GpuTexture Target(int width, int height, Format format = Format.B8G8R8A8_UNorm) => new(this, width, height, format, true);
    internal GpuEffect Effect(string name) => new(this, ShaderBytes(name));

    internal void Clear(GpuTexture texture) => context.ClearRenderTargetView(texture.Target!, new(0, 0, 0, 0));
    internal void Draw(GpuTexture target, GpuEffect effect)
    {
        context.OMSetRenderTargets(target.Target!);
        context.RSSetViewport(0, 0, target.Width, target.Height);
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.VSSetShader(vertex);
        context.PSSetShader(effect.Shader);
        for (int i = 0; i < effect.Sources.Length; i++) shaderViews[i] = effect.Sources[i]?.View!;
        context.PSSetShaderResources(0, (uint)shaderViews.Length, shaderViews);
        if (effect.Constants is not null)
        {
            if (effect.Dirty)
            {
                context.UpdateSubresource(effect.Data, effect.Constants); effect.Dirty = false;
            }
            context.PSSetConstantBuffer(0, effect.Constants);
        }
        context.Draw(3, 0);
        // A texture can be the next pass's output; avoid D3D's implicit SRV hazard unbinding.
        context.PSSetShaderResources(0, (uint)EmptyViews.Length, EmptyViews);
        Array.Clear(shaderViews);
        context.OMSetRenderTargets((ID3D11RenderTargetView)null!);
    }

    private static byte[] ShaderBytes(string name)
    {
        using var stream = typeof(NativeGpu).Assembly.GetManifestResourceStream($"Compositor.App.Shaders.{name}.bin")
            ?? throw new InvalidOperationException("Missing GPU shader: " + name);
        using var bytes = new MemoryStream(); stream.CopyTo(bytes); return bytes.ToArray();
    }

    internal sealed class GpuTexture : IDisposable
    {
        private readonly NativeGpu owner;
        internal readonly int Width, Height;
        private readonly int bytesPerPixel;
        private readonly ID3D11Texture2D texture;
        internal readonly ID3D11ShaderResourceView View;
        internal readonly ID3D11RenderTargetView? Target;
        private CanvasBitmap? bitmap;
        internal GpuTexture(NativeGpu owner, int width, int height, Format format, bool target)
        {
            this.owner = owner; Width = width; Height = height;
            bytesPerPixel = format == Format.R32G32B32A32_Float ? 16 : format == Format.R8_UNorm ? 1 : 4;
            texture = owner.device.CreateTexture2D(new Texture2DDescription(format, (uint)width, (uint)height, 1, 1,
                BindFlags.ShaderResource | (target ? BindFlags.RenderTarget : 0)));
            try
            {
                View = owner.device.CreateShaderResourceView(texture);
                if (target) Target = owner.device.CreateRenderTargetView(texture);
            }
            catch { Dispose(); throw; }
        }
        internal void SetPixelBytes(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length != (long)Width * Height * bytesPerPixel) throw new ArgumentException("Invalid GPU upload length.");
            SetPixels(bytes, Width * bytesPerPixel, new(0, 0, Width, Height));
        }
        internal unsafe void SetPixels(ReadOnlySpan<byte> bytes, int stride, RectI region)
        {
            if (stride < Width * bytesPerPixel || bytes.Length < (long)(Height - 1) * stride + Width * bytesPerPixel
                || region.X < 0 || region.Y < 0 || region.Width < 0 || region.Height < 0
                || (long)region.X + region.Width > Width || (long)region.Y + region.Height > Height)
                throw new ArgumentException("Invalid GPU upload region.");
            if (region.Width == 0 || region.Height == 0) return;
            using var canvasLock = owner.canvasDevice.Lock();
            var box = new Vortice.Mathematics.Box(region.X, region.Y, 0, region.X + region.Width, region.Y + region.Height, 1);
            fixed (byte* data = bytes) owner.context.UpdateSubresource(texture, 0, box,
                (nint)(data + region.Y * stride + region.X * bytesPerPixel), (uint)stride, 0);
#if DEBUG
            owner.UploadedBytes += (long)region.Width * region.Height * bytesPerPixel;
#endif
        }
        internal CanvasBitmap Bitmap
        {
            get
            {
                if (bitmap is not null) return bitmap;
                using var surface = texture.QueryInterface<IDXGISurface>();
                Marshal.ThrowExceptionForHR(CreateDirect3D11SurfaceFromDXGISurface(surface.NativePointer, out nint abi));
                try
                {
                    var shared = WinRT.MarshalInterface<IDirect3DSurface>.FromAbi(abi);
                    try { return bitmap = CanvasBitmap.CreateFromDirect3D11Surface(owner.canvasDevice, shared, 96, CanvasAlphaMode.Premultiplied); }
                    finally { shared.Dispose(); }
                }
                finally { Marshal.Release(abi); }
            }
        }
        public void Dispose() { bitmap?.Dispose(); Target?.Dispose(); View?.Dispose(); texture?.Dispose(); }
    }

    internal sealed class GpuEffect : IDisposable
    {
        internal readonly ID3D11PixelShader Shader;
        internal readonly ID3D11Buffer? Constants;
        internal readonly GpuTexture?[] Sources = new GpuTexture?[8];
        internal readonly byte[] Data;
        internal bool Dirty = true;
        private readonly Dictionary<string, (int Offset, int Size)> variables = new();
        internal GpuTexture? Source1 { set => Sources[0] = value; }
        internal GpuTexture? Source2 { set => Sources[1] = value; }
        internal GpuTexture? Source3 { set => Sources[2] = value; }
        internal GpuTexture? Source4 { set => Sources[3] = value; }
        internal GpuTexture? Source5 { set => Sources[4] = value; }
        internal GpuTexture? Source6 { set => Sources[5] = value; }
        internal GpuEffect Properties => this;
        internal GpuEffect(NativeGpu owner, byte[] code)
        {
            Shader = owner.device.CreatePixelShader(code);
            try
            {
                using var reflection = Compiler.Reflect<ID3D11ShaderReflection>(code);
                if (reflection.Description.ConstantBuffers == 0) { Data = []; return; }
                var buffer = reflection.GetConstantBufferByIndex(0);
                var description = buffer.Description;
                Data = new byte[description.Size];
                for (uint i = 0; i < description.VariableCount; i++)
                {
                    var v = buffer.GetVariableByIndex(i).Description;
                    variables.Add(v.Name, ((int)v.StartOffset, (int)v.Size));
                }
                Constants = owner.device.CreateBuffer(new BufferDescription((uint)Data.Length, BindFlags.ConstantBuffer));
            }
            catch { Dispose(); throw; }
        }
        internal object this[string name]
        {
            set
            {
                if (!variables.TryGetValue(name, out var variable)) throw new ArgumentException("Unknown shader constant: " + name);
                var span = Data.AsSpan(variable.Offset, variable.Size);
                switch (value)
                {
                    case int v: MemoryMarshal.Write(span, in v); break;
                    case float v: MemoryMarshal.Write(span, in v); break;
                    case double v: MemoryMarshal.Write(span, in v); break;
                    case Vector2 v: MemoryMarshal.Write(span, in v); break;
                    case Vector4 v: MemoryMarshal.Write(span, in v); break;
                    default: throw new ArgumentException("Unsupported GPU constant.");
                }
                Dirty = true;
            }
        }
        public void Dispose() { Constants?.Dispose(); Shader?.Dispose(); }
    }

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11SurfaceFromDXGISurface(nint surface, out nint result);
    public void Dispose() { vertex?.Dispose(); state?.Dispose(); context?.Dispose(); device?.Dispose(); }
}
