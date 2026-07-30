using SteamVRTranslator.App.Diagnostics;
using Valve.VR;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using static Vortice.Direct3D11.D3D11;
using static Vortice.DXGI.DXGI;

namespace SteamVRTranslator.App.SteamVR;

internal static class SteamVrD3D11DeviceFactory
{
    private static readonly FeatureLevel[] SupportedFeatureLevels =
        [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0];

    public static void Create(
        AppLog log,
        string purpose,
        out ID3D11Device device,
        out FeatureLevel featureLevel,
        out ID3D11DeviceContext context)
    {
        ArgumentNullException.ThrowIfNull(log);
        var adapterIndex = -1;
        OpenVR.System?.GetDXGIOutputInfo(ref adapterIndex);
        if (adapterIndex < 0)
        {
            throw new InvalidOperationException(
                $"SteamVR 未返回用于{purpose}的 DXGI 显卡索引。");
        }

        using var factory = CreateDXGIFactory1<IDXGIFactory1>();
        if (factory is null)
        {
            throw new InvalidOperationException("创建 DXGI 1.1 Factory 失败。");
        }

        var enumResult = factory.EnumAdapters1(checked((uint)adapterIndex), out var adapter);
        enumResult.CheckError();
        using (adapter)
        {
            var description = adapter.Description1;
            var result = D3D11CreateDevice(
                adapter,
                DriverType.Unknown,
                DeviceCreationFlags.BgraSupport,
                SupportedFeatureLevels,
                out device,
                out featureLevel,
                out context);
            result.CheckError();
            log.Info(
                $"D3D11 {purpose}设备已创建：" +
                $"SteamVRAdapter={adapterIndex} ({description.Description.Trim()}), " +
                $"FeatureLevel={featureLevel}");
        }
    }
}
