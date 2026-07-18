using SteamVRTranslator.App.Diagnostics;
using SteamVRTranslator.Core.Geometry;
using SteamVRTranslator.Core.Selection;
using Valve.VR;

namespace SteamVRTranslator.App.SteamVR;

internal sealed class SpatialQuadOverlayManager : IDisposable
{
    private const uint BaseSortOrder = 110;
    private readonly D3D11OverlayDevice _device;
    private readonly AppLog _log;
    private readonly Dictionary<long, SpatialQuadOverlayResource> _resources = [];
    private bool _disposed;

    public SpatialQuadOverlayManager(D3D11OverlayDevice device, AppLog log)
    {
        _device = device;
        _log = log;
    }

    public void UpdatePixels(long overlayId, byte[] rgba)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var resource = GetOrCreate(overlayId);
        resource.Texture.Update(rgba);
        resource.HasTexture = true;
    }

    public void UpdateTransform(
        long overlayId,
        SpatialSelectionPlane plane,
        ETrackingUniverseOrigin origin)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var resource = GetOrCreate(overlayId);
        Ensure(
            OpenVR.Overlay.SetOverlayWidthInMeters(resource.Handle, plane.OverlayExtent),
            $"设置空间对象 {overlayId} 宽度");
        var transform = SpatialQuadOverlayMath.ToTransform(plane);
        Ensure(
            OpenVR.Overlay.SetOverlayTransformAbsolute(resource.Handle, origin, ref transform),
            $"设置空间对象 {overlayId} 变换");
    }

    public void Show(long overlayId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var resource = GetOrCreate(overlayId);
        if (!resource.HasTexture || resource.IsShown)
        {
            return;
        }

        Ensure(OpenVR.Overlay.ShowOverlay(resource.Handle), $"显示空间对象 {overlayId}");
        resource.IsShown = true;
    }

    public void Hide(long overlayId)
    {
        if (!_resources.TryGetValue(overlayId, out var resource) || !resource.IsShown)
        {
            return;
        }

        _ = OpenVR.Overlay.HideOverlay(resource.Handle);
        resource.IsShown = false;
    }

    public void HideAll()
    {
        foreach (var overlayId in _resources.Keys.ToArray())
        {
            Hide(overlayId);
        }
    }

    public void UpdateDepthOrder(
        Vector3f hmdPosition,
        IReadOnlyList<InteractiveOverlay> overlays)
    {
        var ordered = overlays
            .OrderByDescending(overlay => (overlay.Plane.Center - hmdPosition).LengthSquared)
            .ToArray();
        for (var index = 0; index < ordered.Length; index++)
        {
            if (!_resources.TryGetValue(ordered[index].Id, out var resource))
            {
                continue;
            }

            var order = BaseSortOrder + unchecked((uint)index);
            if (resource.SortOrder == order)
            {
                continue;
            }

            Ensure(
                OpenVR.Overlay.SetOverlaySortOrder(resource.Handle, order),
                $"设置空间对象 {ordered[index].Id} 层级");
            resource.SortOrder = order;
        }
    }

    public bool TryIntersect(
        long overlayId,
        Vector3f source,
        Vector3f direction,
        ETrackingUniverseOrigin origin,
        SpatialSelectionPlane plane,
        out SpatialOverlayPointerHit hit)
    {
        hit = default;
        if (!_resources.TryGetValue(overlayId, out var resource) ||
            !resource.IsShown ||
            direction.LengthSquared < 0.5f)
        {
            return false;
        }

        var parameters = new VROverlayIntersectionParams_t
        {
            vSource = ToHmd(source),
            vDirection = ToHmd(direction.Normalized()),
            eOrigin = origin
        };
        var result = new VROverlayIntersectionResults_t();
        if (!OpenVR.Overlay.ComputeOverlayIntersection(
                resource.Handle,
                ref parameters,
                ref result))
        {
            return false;
        }

        var texturePoint = new NormalizedPoint(result.vUVs.v0, result.vUVs.v1);
        if (!SpatialQuadOverlayMath.TryMapTextureToContent(plane, texturePoint, out var contentPoint))
        {
            return false;
        }

        hit = new SpatialOverlayPointerHit(
            overlayId,
            texturePoint.Clamp(),
            contentPoint,
            result.fDistance);
        return true;
    }

    public void Remove(long overlayId)
    {
        if (!_resources.Remove(overlayId, out var resource))
        {
            return;
        }

        resource.Dispose();
        _log.Info($"[overlay] 已销毁独立空间对象：Overlay={overlayId}。");
    }

    private SpatialQuadOverlayResource GetOrCreate(long overlayId)
    {
        if (_resources.TryGetValue(overlayId, out var resource))
        {
            return resource;
        }

        var key = $"io.steamvrtranslator.spatial.{overlayId}";
        var name = $"SteamVR Translator Spatial {overlayId}";
        var existing = OpenVR.k_ulOverlayHandleInvalid;
        if (OpenVR.Overlay.FindOverlay(key, ref existing) == EVROverlayError.None)
        {
            _ = OpenVR.Overlay.DestroyOverlay(existing);
        }

        var handle = OpenVR.k_ulOverlayHandleInvalid;
        Ensure(OpenVR.Overlay.CreateOverlay(key, name, ref handle), $"创建空间对象 {overlayId}");
        try
        {
            Ensure(OpenVR.Overlay.SetOverlayAlpha(handle, 1f), "设置空间对象透明度");
            Ensure(OpenVR.Overlay.SetOverlayColor(handle, 1f, 1f, 1f), "设置空间对象颜色");
            Ensure(
                OpenVR.Overlay.SetOverlayTextureColorSpace(handle, EColorSpace.Gamma),
                "设置空间对象色彩空间");
            Ensure(
                OpenVR.Overlay.SetOverlayInputMethod(handle, VROverlayInputMethod.None),
                "设置空间对象输入方式");
            var bounds = new VRTextureBounds_t
            {
                uMin = 0f,
                uMax = 1f,
                vMin = 0f,
                vMax = 1f
            };
            Ensure(
                OpenVR.Overlay.SetOverlayTextureBounds(handle, ref bounds),
                "设置空间对象纹理方向");
            Ensure(
                OpenVR.Overlay.SetOverlaySortOrder(handle, BaseSortOrder),
                "设置空间对象默认层级");

            var texture = new D3D11OverlayTexture(_device);
            texture.Attach(handle);
            resource = new SpatialQuadOverlayResource(handle, texture)
            {
                SortOrder = BaseSortOrder
            };
            _resources.Add(overlayId, resource);
            _log.Info(
                $"[overlay] 已创建独立 Quad Overlay：Overlay={overlayId}，Handle={handle}。");
            return resource;
        }
        catch
        {
            _ = OpenVR.Overlay.DestroyOverlay(handle);
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        foreach (var resource in _resources.Values)
        {
            resource.Dispose();
        }
        _resources.Clear();
        _disposed = true;
    }

    private static HmdVector3_t ToHmd(Vector3f value) => new()
    {
        v0 = value.X,
        v1 = value.Y,
        v2 = value.Z
    };

    private static void Ensure(EVROverlayError error, string operation)
    {
        if (error != EVROverlayError.None)
        {
            throw new InvalidOperationException($"{operation}失败：{error}");
        }
    }
}

internal sealed class SpatialQuadOverlayResource(ulong handle, D3D11OverlayTexture texture) : IDisposable
{
    public ulong Handle { get; } = handle;

    public D3D11OverlayTexture Texture { get; } = texture;

    public bool HasTexture { get; set; }

    public bool IsShown { get; set; }

    public uint SortOrder { get; set; }

    public void Dispose()
    {
        _ = OpenVR.Overlay.HideOverlay(Handle);
        _ = OpenVR.Overlay.ClearOverlayTexture(Handle);
        Texture.Dispose();
        _ = OpenVR.Overlay.DestroyOverlay(Handle);
    }
}

internal static class SpatialQuadOverlayMath
{
    public static HmdMatrix34_t ToTransform(SpatialSelectionPlane plane) => new()
    {
        m0 = plane.Right.X,
        m1 = plane.Up.X,
        m2 = plane.Normal.X,
        m3 = plane.Center.X,
        m4 = plane.Right.Y,
        m5 = plane.Up.Y,
        m6 = plane.Normal.Y,
        m7 = plane.Center.Y,
        m8 = plane.Right.Z,
        m9 = plane.Up.Z,
        m10 = plane.Normal.Z,
        m11 = plane.Center.Z
    };

    public static bool TryMapTextureToContent(
        SpatialSelectionPlane plane,
        NormalizedPoint texturePoint,
        out NormalizedPoint contentPoint)
    {
        if (plane.OverlayExtent <= 0f || plane.Width <= 0f || plane.Height <= 0f)
        {
            contentPoint = default;
            return false;
        }

        var width = Math.Clamp(plane.Width / plane.OverlayExtent, 0f, 1f);
        var height = Math.Clamp(plane.Height / plane.OverlayExtent, 0f, 1f);
        var left = (1f - width) / 2f;
        var top = (1f - height) / 2f;
        if (texturePoint.X < left || texturePoint.X > left + width ||
            texturePoint.Y < top || texturePoint.Y > top + height)
        {
            contentPoint = default;
            return false;
        }

        contentPoint = new NormalizedPoint(
            (texturePoint.X - left) / width,
            (texturePoint.Y - top) / height).Clamp();
        return true;
    }
}

internal readonly record struct SpatialOverlayPointerHit(
    long OverlayId,
    NormalizedPoint TexturePoint,
    NormalizedPoint ContentPoint,
    float Distance);
