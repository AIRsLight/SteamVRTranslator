using SteamVRTranslator.App.Diagnostics;
using SteamVRTranslator.Core.Geometry;
using SteamVRTranslator.Core.Selection;
using Valve.VR;
using Vortice.DXGI;

namespace SteamVRTranslator.App.SteamVR;

internal enum SpatialOverlayLayer
{
    Video,
    Content,
    Chrome,
    Progress,
    Pointer,
    Ray
}

internal readonly record struct SpatialOverlayResourceKey(
    long OverlayId,
    SpatialOverlayLayer Layer);

internal sealed class SpatialQuadOverlayManager : IDisposable
{
    private const uint BaseSortOrder = 110;
    private const uint LayerCount = 6;
    private readonly D3D11OverlayDevice _device;
    private readonly AppLog _log;
    private readonly Dictionary<SpatialOverlayResourceKey, SpatialQuadOverlayResource> _resources = [];
    private bool _disposed;

    public SpatialQuadOverlayManager(D3D11OverlayDevice device, AppLog log)
    {
        _device = device;
        _log = log;
    }

    public void UpdatePixels(
        long overlayId,
        byte[] rgba,
        bool synchronizeBuffers = false) =>
        UpdatePixels(
            overlayId,
            rgba,
            OverlayRenderer.TextureWidth,
            OverlayRenderer.TextureHeight,
            synchronizeBuffers);

    public void UpdatePixels(
        long overlayId,
        byte[] rgba,
        int pixelWidth,
        int pixelHeight,
        bool synchronizeBuffers = false) =>
        UpdateLayerPixels(
            overlayId,
            SpatialOverlayLayer.Content,
            rgba,
            pixelWidth,
            pixelHeight,
            synchronizeBuffers);

    public void UpdateLayerPixels(
        long overlayId,
        SpatialOverlayLayer layer,
        byte[] rgba,
        int pixelWidth,
        int pixelHeight,
        bool synchronizeBuffers = false) =>
        UpdateLayerPixels(
            overlayId,
            layer,
            rgba,
            pixelWidth,
            pixelHeight,
            Format.R8G8B8A8_UNorm,
            synchronizeBuffers);

    public void UpdateLayerBgraPixels(
        long overlayId,
        SpatialOverlayLayer layer,
        byte[] bgra,
        int pixelWidth,
        int pixelHeight,
        bool synchronizeBuffers = false) =>
        UpdateLayerPixels(
            overlayId,
            layer,
            bgra,
            pixelWidth,
            pixelHeight,
            Format.B8G8R8A8_UNorm,
            synchronizeBuffers);

    private void UpdateLayerPixels(
        long overlayId,
        SpatialOverlayLayer layer,
        byte[] pixels,
        int pixelWidth,
        int pixelHeight,
        Format format,
        bool synchronizeBuffers)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var resource = GetOrCreate(new SpatialOverlayResourceKey(overlayId, layer));
        var texture = EnsureTexture(resource, pixelWidth, pixelHeight, format);
        if (synchronizeBuffers)
        {
            texture.Reset(pixels);
        }
        else
        {
            texture.Update(pixels);
        }
        resource.HasTexture = true;
    }

    public void UpdateTransform(
        long overlayId,
        SpatialSelectionPlane plane,
        ETrackingUniverseOrigin origin) =>
        UpdateLayerTransform(overlayId, SpatialOverlayLayer.Content, plane, origin);

    public void UpdateLayerTransform(
        long overlayId,
        SpatialOverlayLayer layer,
        SpatialSelectionPlane plane,
        ETrackingUniverseOrigin origin)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var resource = GetOrCreate(new SpatialOverlayResourceKey(overlayId, layer));
        if (resource.TrackingOrigin == origin && resource.Plane == plane)
        {
            return;
        }

        Ensure(
            OpenVR.Overlay.SetOverlayWidthInMeters(resource.Handle, plane.Width),
            $"设置空间对象 {overlayId}/{layer} 宽度");
        var transform = SpatialQuadOverlayMath.ToTransform(plane);
        Ensure(
            OpenVR.Overlay.SetOverlayTransformAbsolute(resource.Handle, origin, ref transform),
            $"设置空间对象 {overlayId}/{layer} 变换");
        resource.Plane = plane;
        resource.TrackingOrigin = origin;
    }

    public void Show(long overlayId) => ShowLayer(overlayId, SpatialOverlayLayer.Content);

    public void ShowLayer(long overlayId, SpatialOverlayLayer layer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var resource = GetOrCreate(new SpatialOverlayResourceKey(overlayId, layer));
        if (!resource.HasTexture || resource.IsShown)
        {
            return;
        }

        Ensure(OpenVR.Overlay.ShowOverlay(resource.Handle), $"显示空间对象 {overlayId}/{layer}");
        resource.IsShown = true;
    }

    public void Hide(long overlayId)
    {
        foreach (var key in _resources.Keys.Where(key => key.OverlayId == overlayId).ToArray())
        {
            HideLayer(key.OverlayId, key.Layer);
        }
    }

    public void HideLayer(long overlayId, SpatialOverlayLayer layer)
    {
        var key = new SpatialOverlayResourceKey(overlayId, layer);
        if (!_resources.TryGetValue(key, out var resource) || !resource.IsShown)
        {
            return;
        }
        _ = OpenVR.Overlay.HideOverlay(resource.Handle);
        resource.IsShown = false;
    }

    public void HideAll()
    {
        foreach (var key in _resources.Keys.ToArray())
        {
            HideLayer(key.OverlayId, key.Layer);
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
            foreach (var layer in Enum.GetValues<SpatialOverlayLayer>())
            {
                var key = new SpatialOverlayResourceKey(ordered[index].Id, layer);
                if (!_resources.TryGetValue(key, out var resource))
                {
                    continue;
                }

                var order = SpatialQuadOverlayMath.CalculateSortOrder(
                    BaseSortOrder,
                    index,
                    layer,
                    LayerCount);
                if (resource.SortOrder == order)
                {
                    continue;
                }

                Ensure(
                    OpenVR.Overlay.SetOverlaySortOrder(resource.Handle, order),
                    $"设置空间对象 {ordered[index].Id}/{layer} 层级");
                resource.SortOrder = order;
            }
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
        var key = new SpatialOverlayResourceKey(overlayId, SpatialOverlayLayer.Content);
        if (!_resources.TryGetValue(key, out var resource) ||
            !resource.IsShown ||
            direction.LengthSquared < 0.5f)
        {
            return false;
        }

        // OpenVR intersects against the submitted content texture's aspect ratio.
        // A direct-video window can be taller than that texture, so use the
        // logical spatial plane shared by rendering, cursor placement and input.
        _ = origin;
        var intersectionPlane = resource.Plane ?? plane;
        if (!SpatialQuadOverlayMath.TryProjectRayInsidePlane(
                source,
                direction,
                intersectionPlane,
                out var texturePoint,
                out var distance))
        {
            return false;
        }

        var worldPoint = source + (direction.Normalized() * distance);
        var contentPoint = SpatialQuadOverlayMath.MapTextureToContent(texturePoint);

        hit = new SpatialOverlayPointerHit(
            overlayId,
            texturePoint,
            contentPoint,
            distance,
            worldPoint);
        return true;
    }

    public void Remove(long overlayId)
    {
        var resources = _resources
            .Where(pair => pair.Key.OverlayId == overlayId)
            .ToArray();
        if (resources.Length == 0)
        {
            return;
        }

        foreach (var pair in resources)
        {
            _resources.Remove(pair.Key);
            pair.Value.Dispose();
        }
        _log.Info($"[overlay] 已销毁独立空间对象：Overlay={overlayId}。");
    }

    private SpatialQuadOverlayResource GetOrCreate(SpatialOverlayResourceKey resourceKey)
    {
        if (_resources.TryGetValue(resourceKey, out var resource))
        {
            return resource;
        }

        var layerSuffix = resourceKey.Layer == SpatialOverlayLayer.Content
            ? string.Empty
            : $".{resourceKey.Layer.ToString().ToLowerInvariant()}";
        var key = $"io.steamvrtranslator.spatial.{resourceKey.OverlayId}{layerSuffix}";
        var name = $"SteamVR Translator Spatial {resourceKey.OverlayId} {resourceKey.Layer}";
        var existing = OpenVR.k_ulOverlayHandleInvalid;
        if (OpenVR.Overlay.FindOverlay(key, ref existing) == EVROverlayError.None)
        {
            _ = OpenVR.Overlay.DestroyOverlay(existing);
        }

        var handle = OpenVR.k_ulOverlayHandleInvalid;
        Ensure(
            OpenVR.Overlay.CreateOverlay(key, name, ref handle),
            $"创建空间对象 {resourceKey.OverlayId}/{resourceKey.Layer}");
        try
        {
            Ensure(OpenVR.Overlay.SetOverlayAlpha(handle, 1f), "设置空间对象透明度");
            Ensure(OpenVR.Overlay.SetOverlayColor(handle, 1f, 1f, 1f), "设置空间对象颜色");
            Ensure(
                OpenVR.Overlay.SetOverlayTextureColorSpace(handle, EColorSpace.Gamma),
                "设置空间对象色彩空间");
            Ensure(OpenVR.Overlay.SetOverlayTexelAspect(handle, 1f), "设置空间对象像素比例");
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

            resource = new SpatialQuadOverlayResource(handle)
            {
                SortOrder = BaseSortOrder
            };
            _resources.Add(resourceKey, resource);
            _log.Info(
                $"[overlay] 已创建独立 Quad Overlay：Overlay={resourceKey.OverlayId}，" +
                $"Layer={resourceKey.Layer}，Handle={handle}。");
            return resource;
        }
        catch
        {
            _ = OpenVR.Overlay.DestroyOverlay(handle);
            throw;
        }
    }

    private D3D11OverlayTexture EnsureTexture(
        SpatialQuadOverlayResource resource,
        int pixelWidth,
        int pixelHeight,
        Format format)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pixelWidth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pixelHeight, 1);
        if (resource.Texture is { } current &&
            current.Width == pixelWidth &&
            current.Height == pixelHeight &&
            current.Format == format)
        {
            return current;
        }

        _ = OpenVR.Overlay.ClearOverlayTexture(resource.Handle);
        resource.Texture?.Dispose();
        var texture = new D3D11OverlayTexture(
            _device,
            pixelWidth,
            pixelHeight,
            format: format);
        texture.Attach(resource.Handle);
        resource.Texture = texture;
        resource.HasTexture = false;
        _log.Info(
            $"[overlay] 空间对象纹理已切换：Handle={resource.Handle}，" +
            $"分辨率={pixelWidth}x{pixelHeight}，格式={format}。");
        return texture;
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

internal sealed class SpatialQuadOverlayResource(ulong handle) : IDisposable
{
    public ulong Handle { get; } = handle;

    public D3D11OverlayTexture? Texture { get; set; }

    public bool HasTexture { get; set; }

    public bool IsShown { get; set; }

    public uint SortOrder { get; set; }

    public SpatialSelectionPlane? Plane { get; set; }

    public ETrackingUniverseOrigin? TrackingOrigin { get; set; }

    public void Dispose()
    {
        _ = OpenVR.Overlay.HideOverlay(Handle);
        _ = OpenVR.Overlay.ClearOverlayTexture(Handle);
        Texture?.Dispose();
        _ = OpenVR.Overlay.DestroyOverlay(Handle);
    }
}

internal static class SpatialQuadOverlayMath
{
    public static uint CalculateSortOrder(
        uint baseSortOrder,
        int depthIndex,
        SpatialOverlayLayer layer,
        uint layerCount = 6) =>
        baseSortOrder +
        checked((uint)depthIndex * layerCount) +
        checked((uint)layer);

    public static SpatialSelectionPlane OffsetPlane(
        SpatialSelectionPlane plane,
        float normalOffset) =>
        plane with
        {
            Center = plane.Center + (plane.Normal * normalOffset)
        };

    public static SpatialSelectionPlane CreateSquareChildPlane(
        SpatialSelectionPlane plane,
        NormalizedPoint textureCenter,
        double logicalExtent,
        float normalOffset)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(logicalExtent, 0);
        var logicalSize = OverlayRenderer.CalculateLogicalCanvasSize(plane);
        var clamped = textureCenter.Clamp();
        var metersPerDip = plane.Width / Math.Max(1f, (float)logicalSize.Width);
        var extent = metersPerDip * (float)logicalExtent;
        return plane with
        {
            Center = plane.Center +
                     (plane.Right * ((clamped.X - 0.5f) * plane.Width)) +
                     (plane.Up * ((0.5f - clamped.Y) * plane.Height)) +
                     (plane.Normal * normalOffset),
            Width = extent,
            Height = extent
        };
    }

    public static SpatialSelectionPlane CreateSquareChildPlaneAtWorldPoint(
        SpatialSelectionPlane plane,
        Vector3f worldCenter,
        double logicalExtent,
        float normalOffset)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(logicalExtent, 0);
        var logicalSize = OverlayRenderer.CalculateLogicalCanvasSize(plane);
        var metersPerDip = plane.Width / Math.Max(1f, (float)logicalSize.Width);
        var extent = metersPerDip * (float)logicalExtent;
        return plane with
        {
            Center = worldCenter + (plane.Normal * normalOffset),
            Width = extent,
            Height = extent
        };
    }

    public static SpatialSelectionPlane CreatePointerPlane(
        SpatialSelectionPlane plane,
        OverlayPointerVisual pointer,
        double logicalExtent,
        float normalOffset) =>
        pointer.WorldPoint is { } worldPoint
            ? CreateSquareChildPlaneAtWorldPoint(
                plane,
                worldPoint,
                logicalExtent,
                normalOffset)
            : CreateSquareChildPlane(
                plane,
                pointer.TexturePoint,
                logicalExtent,
                normalOffset);

    public static SpatialSelectionPlane CreateChildPlane(
        SpatialSelectionPlane plane,
        DirectOverlayPixelRegion region,
        float normalOffset)
    {
        var clamped = region.Clamp();
        return plane with
        {
            Center = plane.Center +
                     (plane.Right * (((clamped.X + (clamped.Width / 2f)) - 0.5f) * plane.Width)) +
                     (plane.Up * ((0.5f - (clamped.Y + (clamped.Height / 2f))) * plane.Height)) +
                     (plane.Normal * normalOffset),
            Width = plane.Width * clamped.Width,
            Height = plane.Height * clamped.Height
        };
    }

    public static NormalizedPoint OpenVrToTexturePoint(NormalizedPoint overlayPoint) =>
        new NormalizedPoint(overlayPoint.X, 1f - overlayPoint.Y).Clamp();

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

    public static NormalizedPoint MapTextureToContent(NormalizedPoint texturePoint) =>
        texturePoint.Clamp();

    public static Vector3f TexturePointToWorld(
        SpatialSelectionPlane plane,
        NormalizedPoint texturePoint,
        float normalOffset = 0f)
    {
        var point = texturePoint.Clamp();
        return plane.Center +
               (plane.Right * ((point.X - 0.5f) * plane.Width)) +
               (plane.Up * ((0.5f - point.Y) * plane.Height)) +
               (plane.Normal * normalOffset);
    }

    public static NormalizedPoint WorldPointToTexture(
        SpatialSelectionPlane plane,
        Vector3f worldPoint,
        float physicalHeight)
    {
        var width = MathF.Max(0.0001f, plane.Width);
        var height = MathF.Max(0.0001f, physicalHeight);
        var local = worldPoint - plane.Center;
        return new NormalizedPoint(
            0.5f + (Vector3f.Dot(local, plane.Right) / width),
            0.5f - (Vector3f.Dot(local, plane.Up) / height)).Clamp();
    }

    public static bool TryCreateRayPlane(
        Vector3f source,
        Vector3f target,
        Vector3f viewer,
        float thickness,
        out SpatialSelectionPlane plane)
    {
        plane = default;
        var segment = target - source;
        var length = segment.Length;
        if (length < 0.01f || thickness <= 0f)
        {
            return false;
        }

        var center = source + (segment * 0.5f);
        var right = segment / length;
        var towardViewer = (viewer - center).Normalized();
        var up = Vector3f.Cross(towardViewer, right).Normalized();
        if (up.LengthSquared < 0.5f)
        {
            up = Vector3f.Cross(new Vector3f(0f, 1f, 0f), right).Normalized();
        }
        if (up.LengthSquared < 0.5f)
        {
            up = Vector3f.Cross(new Vector3f(1f, 0f, 0f), right).Normalized();
        }
        if (up.LengthSquared < 0.5f)
        {
            return false;
        }

        var normal = Vector3f.Cross(right, up).Normalized();
        if (Vector3f.Dot(normal, towardViewer) < 0f)
        {
            up *= -1f;
            normal *= -1f;
        }

        plane = new SpatialSelectionPlane(
            center,
            right,
            up,
            normal,
            length,
            thickness,
            length,
            default,
            default,
            0f);
        return true;
    }

    public static bool TryProjectRayToPlane(
        Vector3f source,
        Vector3f direction,
        SpatialSelectionPlane plane,
        out NormalizedPoint texturePoint,
        out float distance)
    {
        texturePoint = default;
        distance = 0;
        if (direction.LengthSquared < 0.5f || plane.Width <= 0.0001f || plane.Height <= 0.0001f)
        {
            return false;
        }

        var normalizedDirection = direction.Normalized();
        var denominator = Vector3f.Dot(normalizedDirection, plane.Normal);
        if (MathF.Abs(denominator) < 0.0001f)
        {
            return false;
        }

        distance = Vector3f.Dot(plane.Center - source, plane.Normal) / denominator;
        if (distance < 0)
        {
            return false;
        }

        var local = source + (normalizedDirection * distance) - plane.Center;
        texturePoint = new NormalizedPoint(
            0.5f + (Vector3f.Dot(local, plane.Right) / plane.Width),
            0.5f - (Vector3f.Dot(local, plane.Up) / plane.Height));
        return true;
    }

    public static bool TryProjectRayInsidePlane(
        Vector3f source,
        Vector3f direction,
        SpatialSelectionPlane plane,
        out NormalizedPoint texturePoint,
        out float distance)
    {
        if (!TryProjectRayToPlane(source, direction, plane, out texturePoint, out distance) ||
            texturePoint.X < 0f ||
            texturePoint.X > 1f ||
            texturePoint.Y < 0f ||
            texturePoint.Y > 1f)
        {
            texturePoint = default;
            distance = 0f;
            return false;
        }

        texturePoint = texturePoint.Clamp();
        return true;
    }
}

internal readonly record struct SpatialOverlayPointerHit(
    long OverlayId,
    NormalizedPoint TexturePoint,
    NormalizedPoint ContentPoint,
    float Distance,
    Vector3f WorldPoint);
