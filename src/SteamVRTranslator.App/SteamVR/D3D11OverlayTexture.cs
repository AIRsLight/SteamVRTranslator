using SteamVRTranslator.App.Diagnostics;
using Valve.VR;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using static Vortice.Direct3D11.D3D11;

namespace SteamVRTranslator.App.SteamVR;

internal sealed class D3D11OverlayDevice : IDisposable
{
    private readonly AppLog _log;
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11Query _uploadCompleteQuery;

    public D3D11OverlayDevice(AppLog log)
    {
        _log = log;
        var result = D3D11CreateDevice(
            null,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0],
            out _device,
            out var featureLevel,
            out _context);
        result.CheckError();
        _uploadCompleteQuery = _device.CreateQuery(
            new QueryDescription(QueryType.Event, QueryFlags.None));
        _log.Info($"D3D11 Overlay 共享设备已创建：FeatureLevel={featureLevel}");
    }

    public ID3D11Device Device => _device;

    public ID3D11DeviceContext Context => _context;

    public ID3D11Texture2D CreateTexture() =>
        CreateTexture(
            OverlayRenderer.Width,
            OverlayRenderer.Height,
            BindFlags.ShaderResource | BindFlags.RenderTarget);

    public ID3D11Texture2D CreateTexture(int width, int height, BindFlags bindFlags) =>
        _device.CreateTexture2D(new Texture2DDescription(
            Format.R8G8B8A8_UNorm,
            checked((uint)width),
            checked((uint)height),
            1,
            1,
            bindFlags,
            ResourceUsage.Default,
            CpuAccessFlags.None));

    public void Upload(ID3D11Texture2D texture, byte[] rgba)
        => Upload(texture, rgba, OverlayRenderer.Width, OverlayRenderer.Height);

    public void Upload(ID3D11Texture2D texture, byte[] rgba, int width, int height)
    {
        var expectedLength = checked(width * height * 4);
        if (rgba.Length != expectedLength)
        {
            throw new ArgumentException(
                $"纹理像素长度无效：期望 {expectedLength:N0}，实际 {rgba.Length:N0}。",
                nameof(rgba));
        }

        _context.UpdateSubresource(
            rgba,
            texture,
            rowPitch: checked((uint)(width * 4)),
            depthPitch: checked((uint)rgba.Length));
        WaitForGpu();
    }

    public void WaitForGpu()
    {
        _context.End(_uploadCompleteQuery);
        _context.Flush();
        var waitStarted = Environment.TickCount64;
        while (true)
        {
            using var completion = _context.GetData(_uploadCompleteQuery, AsyncGetDataFlags.None);
            if (completion is not null)
            {
                return;
            }

            if (Environment.TickCount64 - waitStarted > 1000)
            {
                throw new TimeoutException("等待 D3D11 Overlay 纹理上传完成超时。");
            }

            Thread.Yield();
        }
    }

    public void Dispose()
    {
        _uploadCompleteQuery.Dispose();
        _context.Dispose();
        _device.Dispose();
    }
}

internal sealed class D3D11OverlayTexture : IDisposable
{
    private readonly D3D11OverlayDevice _device;
    private readonly ID3D11Texture2D[] _textures;
    private ulong _overlayHandle = OpenVR.k_ulOverlayHandleInvalid;
    private int _nextTextureIndex;

    public D3D11OverlayTexture(D3D11OverlayDevice device, int bufferCount = 2)
    {
        _device = device;
        _textures = Enumerable.Range(0, Math.Max(1, bufferCount))
            .Select(_ => device.CreateTexture())
            .ToArray();
    }

    public void Attach(ulong overlayHandle) => _overlayHandle = overlayHandle;

    public void Update(byte[] rgba)
    {
        ValidatePixels(rgba);

        var textureResource = _textures[_nextTextureIndex];
        UploadAndSubmit(textureResource, rgba);

        _nextTextureIndex = (_nextTextureIndex + 1) % _textures.Length;
    }

    public void Reset(byte[] rgba)
    {
        ValidatePixels(rgba);
        foreach (var texture in _textures)
        {
            UploadAndSubmit(texture, rgba);
        }

        _nextTextureIndex = 0;
    }

    private static void ValidatePixels(byte[] rgba)
    {
        var expectedLength = checked(OverlayRenderer.Width * OverlayRenderer.Height * 4);
        if (rgba.Length != expectedLength)
        {
            throw new ArgumentException(
                $"Overlay 像素长度无效：期望 {expectedLength:N0}，实际 {rgba.Length:N0}。",
                nameof(rgba));
        }
    }

    private void UploadAndSubmit(ID3D11Texture2D textureResource, byte[] rgba)
    {
        _device.Upload(textureResource, rgba);
        var texture = new Texture_t
        {
            handle = textureResource.NativePointer,
            eType = ETextureType.DirectX,
            eColorSpace = EColorSpace.Gamma
        };
        var error = OpenVR.Overlay.SetOverlayTexture(_overlayHandle, ref texture);
        if (error != EVROverlayError.None)
        {
            throw new InvalidOperationException($"注册 D3D11 Overlay 纹理失败：{error}");
        }
    }

    public void Dispose()
    {
        foreach (var texture in _textures)
        {
            texture.Dispose();
        }
    }
}
