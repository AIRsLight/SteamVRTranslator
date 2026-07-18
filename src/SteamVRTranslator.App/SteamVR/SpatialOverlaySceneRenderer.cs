using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using SteamVRTranslator.App.Diagnostics;
using SteamVRTranslator.Core.Geometry;
using Valve.VR;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace SteamVRTranslator.App.SteamVR;

internal sealed class SpatialOverlaySceneRenderer : IDisposable
{
    private const float NearPlane = 0.025f;
    private const float FarPlane = 25f;
    private const int MaximumEyeDimension = 1440;

    internal const string ShaderSource = """
        cbuffer DrawConstants : register(b0)
        {
            row_major float4x4 World;
            row_major float4x4 WorldViewProjection;
            float4 Tint;
            float4 Options;
        };

        Texture2D SurfaceTexture : register(t0);
        SamplerState SurfaceSampler : register(s0);

        struct VertexInput
        {
            float3 Position : POSITION;
            float3 Normal : NORMAL;
            float2 TextureCoordinate : TEXCOORD0;
        };

        struct VertexOutput
        {
            float4 Position : SV_POSITION;
            float3 Normal : NORMAL;
            float2 TextureCoordinate : TEXCOORD0;
        };

        VertexOutput VSMain(VertexInput input)
        {
            VertexOutput output;
            output.Position = mul(float4(input.Position, 1.0), WorldViewProjection);
            output.Normal = normalize(mul(float4(input.Normal, 0.0), World).xyz);
            output.TextureCoordinate = input.TextureCoordinate;
            return output;
        }

        float4 PSMain(VertexOutput input) : SV_TARGET
        {
            float4 color = SurfaceTexture.Sample(SurfaceSampler, input.TextureCoordinate);
            clip(color.a - 0.008);
            if (Options.y > 0.5)
            {
                color.rgb *= color.a;
            }

            if (Options.x > 0.5)
            {
                float3 lightDirection = normalize(float3(-0.3, 0.8, -0.4));
                float lighting = 0.42 + (0.58 * abs(dot(normalize(input.Normal), lightDirection)));
                color.rgb *= lighting;
            }

            color *= Tint;
            return color;
        }
        """;

    private readonly AppLog _log;
    private readonly D3D11OverlayDevice _sharedDevice;
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly ProjectionEyeTarget _leftTarget;
    private readonly ProjectionEyeTarget _rightTarget;
    private readonly Dictionary<long, SurfaceTexture> _surfaces = [];
    private readonly Dictionary<string, ControllerMesh> _controllerMeshes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _failedControllerModels = new(StringComparer.Ordinal);
    private readonly Dictionary<ETrackedControllerRole, string> _controllerModelNames = [];
    private readonly SceneMesh _panelMesh;
    private readonly SceneMesh _fallbackControllerMesh;
    private readonly ID3D11Texture2D _whiteTexture;
    private readonly ID3D11ShaderResourceView _whiteTextureView;
    private readonly ID3D11VertexShader _vertexShader;
    private readonly ID3D11PixelShader _pixelShader;
    private readonly ID3D11InputLayout _inputLayout;
    private readonly ID3D11Buffer _constantBuffer;
    private readonly ID3D11BlendState _blendState;
    private readonly ID3D11DepthStencilState _depthState;
    private readonly ID3D11RasterizerState _rasterizerState;
    private readonly ID3D11SamplerState _samplerState;
    private bool _shown;
    private bool _disposed;

    public SpatialOverlaySceneRenderer(
        D3D11OverlayDevice sharedDevice,
        AppLog log,
        ulong leftOverlayHandle,
        ulong rightOverlayHandle)
    {
        _sharedDevice = sharedDevice;
        _log = log;
        _device = sharedDevice.Device;
        _context = sharedDevice.Context;

        uint recommendedWidth = 0;
        uint recommendedHeight = 0;
        OpenVR.System.GetRecommendedRenderTargetSize(ref recommendedWidth, ref recommendedHeight);
        (var width, var height) = ScaleEyeTarget(recommendedWidth, recommendedHeight);
        _leftTarget = new ProjectionEyeTarget(_device, leftOverlayHandle, EVREye.Eye_Left, width, height);
        _rightTarget = new ProjectionEyeTarget(_device, rightOverlayHandle, EVREye.Eye_Right, width, height);

        var vertexShaderBytes = Compiler.Compile(
            ShaderSource,
            "VSMain",
            "SpatialOverlayScene.hlsl",
            "vs_5_0",
            ShaderFlags.OptimizationLevel3 | ShaderFlags.PackMatrixRowMajor,
            EffectFlags.None);
        var pixelShaderBytes = Compiler.Compile(
            ShaderSource,
            "PSMain",
            "SpatialOverlayScene.hlsl",
            "ps_5_0",
            ShaderFlags.OptimizationLevel3 | ShaderFlags.PackMatrixRowMajor,
            EffectFlags.None);
        _vertexShader = _device.CreateVertexShader(vertexShaderBytes.Span, null);
        _pixelShader = _device.CreatePixelShader(pixelShaderBytes.Span, null);
        _inputLayout = _device.CreateInputLayout(
            [
                new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
                new InputElementDescription("NORMAL", 0, Format.R32G32B32_Float, 0, 12),
                new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 0, 24)
            ],
            vertexShaderBytes.Span);
        _constantBuffer = _device.CreateBuffer(
            checked((uint)Marshal.SizeOf<DrawConstants>()),
            BindFlags.ConstantBuffer,
            ResourceUsage.Default,
            CpuAccessFlags.None,
            ResourceOptionFlags.None,
            0);
        _blendState = _device.CreateBlendState(new BlendDescription(Blend.One, Blend.InverseSourceAlpha));
        _depthState = _device.CreateDepthStencilState(
            new DepthStencilDescription(true, DepthWriteMask.All, ComparisonFunction.LessEqual));
        _rasterizerState = _device.CreateRasterizerState(RasterizerDescription.CullNone);
        _samplerState = _device.CreateSamplerState(SamplerDescription.LinearClamp);
        _panelMesh = CreatePanelMesh();
        _fallbackControllerMesh = CreateFallbackControllerMesh();
        _whiteTexture = sharedDevice.CreateTexture(1, 1, BindFlags.ShaderResource);
        sharedDevice.Upload(_whiteTexture, [255, 255, 255, 255], 1, 1);
        _whiteTextureView = _device.CreateShaderResourceView(_whiteTexture, null);

        _log.Info(
            $"[overlay-scene] 双眼深度合成器已创建：" +
            $"SteamVR 建议={recommendedWidth}x{recommendedHeight}，实际={width}x{height}。");
    }

    public void UpdateSurface(long overlayId, byte[] rgba)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_surfaces.TryGetValue(overlayId, out var surface))
        {
            surface = new SurfaceTexture(_sharedDevice, _device);
            _surfaces.Add(overlayId, surface);
        }

        surface.Update(rgba);
    }

    public void RemoveSurface(long overlayId)
    {
        if (_surfaces.Remove(overlayId, out var surface))
        {
            surface.Dispose();
        }
    }

    public void Render(IReadOnlyList<InteractiveOverlay> overlays)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (overlays.Count == 0)
        {
            Hide();
            return;
        }

        var poses = new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];
        OpenVR.System.GetDeviceToAbsoluteTrackingPose(
            ETrackingUniverseOrigin.TrackingUniverseStanding,
            0f,
            poses);
        var hmdPose = poses[OpenVR.k_unTrackedDeviceIndex_Hmd];
        if (!hmdPose.bPoseIsValid)
        {
            return;
        }

        PrepareControllerModels(poses);
        RenderEye(_leftTarget, hmdPose.mDeviceToAbsoluteTracking, poses, overlays);
        RenderEye(_rightTarget, hmdPose.mDeviceToAbsoluteTracking, poses, overlays);
        _shown = true;
    }

    public void Hide()
    {
        if (!_shown)
        {
            return;
        }

        _ = OpenVR.Overlay.HideOverlay(_leftTarget.OverlayHandle);
        _ = OpenVR.Overlay.HideOverlay(_rightTarget.OverlayHandle);
        _shown = false;
    }

    private void RenderEye(
        ProjectionEyeTarget target,
        HmdMatrix34_t worldFromHead,
        IReadOnlyList<TrackedDevicePose_t> poses,
        IReadOnlyList<InteractiveOverlay> overlays)
    {
        var headFromEye = OpenVR.System.GetEyeToHeadTransform(target.Eye);
        var worldFromEye = Multiply(worldFromHead, headFromEye);
        var eyeWorld = ToNumerics(worldFromEye);
        if (!Matrix4x4.Invert(eyeWorld, out var view))
        {
            return;
        }

        var projection = ToNumerics(OpenVR.System.GetProjectionMatrix(target.Eye, NearPlane, FarPlane));
        var frame = target.NextFrame();
        _context.OMSetRenderTargets(frame.RenderTargetView, target.DepthStencilView);
        _context.ClearRenderTargetView(frame.RenderTargetView, new Color4(0f, 0f, 0f, 0f));
        _context.ClearDepthStencilView(
            target.DepthStencilView,
            DepthStencilClearFlags.Depth,
            1f,
            0);
        _context.RSSetViewport(0f, 0f, target.Width, target.Height, 0f, 1f);
        _context.RSSetState(_rasterizerState);
        _context.OMSetBlendState(_blendState);
        _context.OMSetDepthStencilState(_depthState, 0);
        _context.IASetInputLayout(_inputLayout);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.VSSetShader(_vertexShader);
        _context.PSSetShader(_pixelShader);
        _context.VSSetConstantBuffer(0, _constantBuffer);
        _context.PSSetConstantBuffer(0, _constantBuffer);
        _context.PSSetSampler(0, _samplerState);

        foreach (var overlay in overlays)
        {
            if (!_surfaces.TryGetValue(overlay.Id, out var surface))
            {
                continue;
            }

            var world = PlaneWorld(overlay.Plane);
            Draw(
                _panelMesh,
                surface.View,
                world,
                view,
                projection,
                Vector4.One,
                useLighting: false,
                straightAlpha: true);
        }

        DrawController(ETrackedControllerRole.LeftHand, poses, view, projection);
        DrawController(ETrackedControllerRole.RightHand, poses, view, projection);

        _context.PSSetShaderResource(0, null!);
        _context.OMSetRenderTargets([], null!);
        _sharedDevice.WaitForGpu();
        Submit(target, frame.Texture, worldFromEye);
    }

    private void DrawController(
        ETrackedControllerRole role,
        IReadOnlyList<TrackedDevicePose_t> poses,
        Matrix4x4 view,
        Matrix4x4 projection)
    {
        var deviceIndex = OpenVR.System.GetTrackedDeviceIndexForControllerRole(role);
        if (deviceIndex == OpenVR.k_unTrackedDeviceIndexInvalid ||
            deviceIndex >= poses.Count ||
            !poses[(int)deviceIndex].bPoseIsValid)
        {
            return;
        }

        var world = ToNumerics(poses[(int)deviceIndex].mDeviceToAbsoluteTracking);
        if (_controllerModelNames.TryGetValue(role, out var modelName) &&
            _controllerMeshes.TryGetValue(modelName, out var controller))
        {
            Draw(
                controller.Mesh,
                controller.TextureView ?? _whiteTextureView,
                world,
                view,
                projection,
                Vector4.One,
                useLighting: true,
                straightAlpha: true);
            return;
        }

        Draw(
            _fallbackControllerMesh,
            _whiteTextureView,
            world,
            view,
            projection,
            role == ETrackedControllerRole.LeftHand
                ? new Vector4(0.58f, 0.78f, 0.92f, 1f)
                : new Vector4(0.78f, 0.83f, 0.88f, 1f),
            useLighting: true,
            straightAlpha: true);
    }

    private void Draw(
        SceneMesh mesh,
        ID3D11ShaderResourceView texture,
        Matrix4x4 world,
        Matrix4x4 view,
        Matrix4x4 projection,
        Vector4 tint,
        bool useLighting,
        bool straightAlpha)
    {
        var constants = new DrawConstants
        {
            World = world,
            WorldViewProjection = world * view * projection,
            Tint = tint,
            Options = new Vector4(useLighting ? 1f : 0f, straightAlpha ? 1f : 0f, 0f, 0f)
        };
        _context.UpdateSubresource(in constants, _constantBuffer);
        _context.IASetVertexBuffer(0, mesh.VertexBuffer, SceneVertex.Stride, 0);
        _context.IASetIndexBuffer(mesh.IndexBuffer, Format.R16_UInt, 0);
        _context.PSSetShaderResource(0, texture);
        _context.DrawIndexed(mesh.IndexCount, 0, 0);
    }

    private static void Submit(
        ProjectionEyeTarget target,
        ID3D11Texture2D textureResource,
        HmdMatrix34_t worldFromEye)
    {
        var left = 0f;
        var right = 0f;
        var top = 0f;
        var bottom = 0f;
        OpenVR.System.GetProjectionRaw(target.Eye, ref left, ref right, ref top, ref bottom);
        var projection = new VROverlayProjection_t
        {
            fLeft = left,
            fRight = right,
            fTop = top,
            fBottom = bottom
        };
        var transform = worldFromEye;
        EnsureOverlay(
            OpenVR.Overlay.SetOverlayTransformProjection(
                target.OverlayHandle,
                ETrackingUniverseOrigin.TrackingUniverseStanding,
                ref transform,
                ref projection,
                target.Eye),
            $"设置 {target.Eye} 投影变换");
        var texture = new Texture_t
        {
            handle = textureResource.NativePointer,
            eType = ETextureType.DirectX,
            eColorSpace = EColorSpace.Gamma
        };
        EnsureOverlay(
            OpenVR.Overlay.SetOverlayTexture(target.OverlayHandle, ref texture),
            $"提交 {target.Eye} 深度合成纹理");
        EnsureOverlay(OpenVR.Overlay.ShowOverlay(target.OverlayHandle), $"显示 {target.Eye} 空间场景");
    }

    private void PrepareControllerModels(IReadOnlyList<TrackedDevicePose_t> poses)
    {
        PrepareControllerModel(ETrackedControllerRole.LeftHand, poses);
        PrepareControllerModel(ETrackedControllerRole.RightHand, poses);
        foreach (var controller in _controllerMeshes.Values)
        {
            controller.TryLoadTexture(_device, _log);
        }
    }

    private void PrepareControllerModel(
        ETrackedControllerRole role,
        IReadOnlyList<TrackedDevicePose_t> poses)
    {
        var deviceIndex = OpenVR.System.GetTrackedDeviceIndexForControllerRole(role);
        if (deviceIndex == OpenVR.k_unTrackedDeviceIndexInvalid ||
            deviceIndex >= poses.Count ||
            !poses[(int)deviceIndex].bDeviceIsConnected)
        {
            return;
        }

        var modelName = GetTrackedDeviceString(deviceIndex, ETrackedDeviceProperty.Prop_RenderModelName_String);
        if (string.IsNullOrWhiteSpace(modelName))
        {
            return;
        }

        _controllerModelNames[role] = modelName;
        if (_controllerMeshes.ContainsKey(modelName) || _failedControllerModels.Contains(modelName))
        {
            return;
        }

        var modelPointer = IntPtr.Zero;
        var error = OpenVR.RenderModels.LoadRenderModel_Async(modelName, ref modelPointer);
        if (error == EVRRenderModelError.Loading)
        {
            return;
        }
        if (error != EVRRenderModelError.None || modelPointer == IntPtr.Zero)
        {
            _failedControllerModels.Add(modelName);
            _log.Warning($"[overlay-scene] 无法加载手柄模型 '{modelName}'：{error}，使用简化定位模型。");
            return;
        }

        try
        {
            var source = Marshal.PtrToStructure<RenderModel_t>(modelPointer);
            var vertices = new SceneVertex[checked((int)source.unVertexCount)];
            var sourceVertexSize = Marshal.SizeOf<RenderModel_Vertex_t>();
            for (var index = 0; index < vertices.Length; index++)
            {
                var vertex = Marshal.PtrToStructure<RenderModel_Vertex_t>(
                    IntPtr.Add(source.rVertexData, index * sourceVertexSize));
                vertices[index] = new SceneVertex(
                    new Vector3(vertex.vPosition.v0, vertex.vPosition.v1, vertex.vPosition.v2),
                    new Vector3(vertex.vNormal.v0, vertex.vNormal.v1, vertex.vNormal.v2),
                    new Vector2(vertex.rfTextureCoord0, vertex.rfTextureCoord1));
            }

            var indexCount = checked((int)(source.unTriangleCount * 3));
            var indices = new short[indexCount];
            Marshal.Copy(source.rIndexData, indices, 0, indices.Length);
            var mesh = CreateMesh(vertices, indices);
            _controllerMeshes.Add(modelName, new ControllerMesh(mesh, source.diffuseTextureId));
            _log.Info(
                $"[overlay-scene] 手柄模型已加载：手={role}，模型={modelName}，" +
                $"顶点={vertices.Length}，三角形={source.unTriangleCount}。");
        }
        catch (Exception exception)
        {
            _failedControllerModels.Add(modelName);
            _log.Error($"[overlay-scene] 创建手柄模型 '{modelName}' 失败，使用简化定位模型。", exception);
        }
        finally
        {
            OpenVR.RenderModels.FreeRenderModel(modelPointer);
        }
    }

    private SceneMesh CreatePanelMesh() => CreateMesh(
        [
            new SceneVertex(new Vector3(-0.5f, 0.5f, 0f), Vector3.UnitZ, new Vector2(0f, 0f)),
            new SceneVertex(new Vector3(0.5f, 0.5f, 0f), Vector3.UnitZ, new Vector2(1f, 0f)),
            new SceneVertex(new Vector3(0.5f, -0.5f, 0f), Vector3.UnitZ, new Vector2(1f, 1f)),
            new SceneVertex(new Vector3(-0.5f, -0.5f, 0f), Vector3.UnitZ, new Vector2(0f, 1f))
        ],
        [0, 1, 2, 0, 2, 3]);

    private SceneMesh CreateFallbackControllerMesh()
    {
        const float x = 0.022f;
        const float y = 0.035f;
        const float z = 0.075f;
        var vertices = new List<SceneVertex>(24);
        var indices = new List<short>(36);
        AddBoxFace(vertices, indices, new Vector3(-x, -y, -z), new Vector3(x, -y, z), -Vector3.UnitY);
        AddBoxFace(vertices, indices, new Vector3(-x, y, z), new Vector3(x, y, -z), Vector3.UnitY);
        AddBoxFace(vertices, indices, new Vector3(-x, -y, z), new Vector3(x, y, z), Vector3.UnitZ);
        AddBoxFace(vertices, indices, new Vector3(x, -y, -z), new Vector3(-x, y, -z), -Vector3.UnitZ);
        AddBoxFace(vertices, indices, new Vector3(-x, -y, -z), new Vector3(-x, y, z), -Vector3.UnitX);
        AddBoxFace(vertices, indices, new Vector3(x, -y, z), new Vector3(x, y, -z), Vector3.UnitX);
        return CreateMesh(vertices.ToArray(), indices.ToArray());
    }

    private static void AddBoxFace(
        ICollection<SceneVertex> vertices,
        ICollection<short> indices,
        Vector3 lower,
        Vector3 upper,
        Vector3 normal)
    {
        var first = checked((short)vertices.Count);
        Vector3[] positions;
        if (MathF.Abs(normal.X) > 0.5f)
        {
            positions =
            [
                new Vector3(lower.X, lower.Y, lower.Z),
                new Vector3(lower.X, upper.Y, lower.Z),
                new Vector3(upper.X, upper.Y, upper.Z),
                new Vector3(upper.X, lower.Y, upper.Z)
            ];
        }
        else if (MathF.Abs(normal.Y) > 0.5f)
        {
            positions =
            [
                new Vector3(lower.X, lower.Y, lower.Z),
                new Vector3(upper.X, lower.Y, lower.Z),
                new Vector3(upper.X, upper.Y, upper.Z),
                new Vector3(lower.X, upper.Y, upper.Z)
            ];
        }
        else
        {
            positions =
            [
                new Vector3(lower.X, lower.Y, lower.Z),
                new Vector3(upper.X, lower.Y, lower.Z),
                new Vector3(upper.X, upper.Y, upper.Z),
                new Vector3(lower.X, upper.Y, upper.Z)
            ];
        }

        var textureCoordinates = new[]
        {
            new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(1f, 0f), new Vector2(0f, 0f)
        };
        for (var index = 0; index < positions.Length; index++)
        {
            vertices.Add(new SceneVertex(positions[index], normal, textureCoordinates[index]));
        }
        foreach (var index in new short[] { 0, 1, 2, 0, 2, 3 })
        {
            indices.Add(checked((short)(first + index)));
        }
    }

    private SceneMesh CreateMesh(SceneVertex[] vertices, short[] indices)
    {
        var vertexBuffer = _device.CreateBuffer(
            vertices,
            BindFlags.VertexBuffer,
            ResourceUsage.Immutable,
            CpuAccessFlags.None,
            ResourceOptionFlags.None,
            0,
            0);
        var indexBuffer = _device.CreateBuffer(
            indices,
            BindFlags.IndexBuffer,
            ResourceUsage.Immutable,
            CpuAccessFlags.None,
            ResourceOptionFlags.None,
            0,
            0);
        return new SceneMesh(vertexBuffer, indexBuffer, checked((uint)indices.Length));
    }

    private static Matrix4x4 PlaneWorld(SpatialSelectionPlane plane)
    {
        var extent = plane.OverlayExtent;
        return new Matrix4x4(
            plane.Right.X * extent, plane.Right.Y * extent, plane.Right.Z * extent, 0f,
            plane.Up.X * extent, plane.Up.Y * extent, plane.Up.Z * extent, 0f,
            plane.Normal.X, plane.Normal.Y, plane.Normal.Z, 0f,
            plane.Center.X, plane.Center.Y, plane.Center.Z, 1f);
    }

    internal static Matrix4x4 ToNumerics(HmdMatrix34_t matrix) => new(
        matrix.m0, matrix.m4, matrix.m8, 0f,
        matrix.m1, matrix.m5, matrix.m9, 0f,
        matrix.m2, matrix.m6, matrix.m10, 0f,
        matrix.m3, matrix.m7, matrix.m11, 1f);

    internal static Matrix4x4 ToNumerics(HmdMatrix44_t matrix) => new(
        matrix.m0, matrix.m4, matrix.m8, matrix.m12,
        matrix.m1, matrix.m5, matrix.m9, matrix.m13,
        matrix.m2, matrix.m6, matrix.m10, matrix.m14,
        matrix.m3, matrix.m7, matrix.m11, matrix.m15);

    internal static HmdMatrix34_t Multiply(HmdMatrix34_t left, HmdMatrix34_t right) => new()
    {
        m0 = (left.m0 * right.m0) + (left.m1 * right.m4) + (left.m2 * right.m8),
        m1 = (left.m0 * right.m1) + (left.m1 * right.m5) + (left.m2 * right.m9),
        m2 = (left.m0 * right.m2) + (left.m1 * right.m6) + (left.m2 * right.m10),
        m3 = (left.m0 * right.m3) + (left.m1 * right.m7) + (left.m2 * right.m11) + left.m3,
        m4 = (left.m4 * right.m0) + (left.m5 * right.m4) + (left.m6 * right.m8),
        m5 = (left.m4 * right.m1) + (left.m5 * right.m5) + (left.m6 * right.m9),
        m6 = (left.m4 * right.m2) + (left.m5 * right.m6) + (left.m6 * right.m10),
        m7 = (left.m4 * right.m3) + (left.m5 * right.m7) + (left.m6 * right.m11) + left.m7,
        m8 = (left.m8 * right.m0) + (left.m9 * right.m4) + (left.m10 * right.m8),
        m9 = (left.m8 * right.m1) + (left.m9 * right.m5) + (left.m10 * right.m9),
        m10 = (left.m8 * right.m2) + (left.m9 * right.m6) + (left.m10 * right.m10),
        m11 = (left.m8 * right.m3) + (left.m9 * right.m7) + (left.m10 * right.m11) + left.m11
    };

    private static (int Width, int Height) ScaleEyeTarget(uint recommendedWidth, uint recommendedHeight)
    {
        var sourceWidth = Math.Max(1, checked((int)recommendedWidth));
        var sourceHeight = Math.Max(1, checked((int)recommendedHeight));
        var scale = Math.Min(1d, MaximumEyeDimension / (double)Math.Max(sourceWidth, sourceHeight));
        var width = Math.Max(256, (int)Math.Round(sourceWidth * scale));
        var height = Math.Max(256, (int)Math.Round(sourceHeight * scale));
        return (width, height);
    }

    private static string? GetTrackedDeviceString(uint deviceIndex, ETrackedDeviceProperty property)
    {
        var error = ETrackedPropertyError.TrackedProp_Success;
        var required = OpenVR.System.GetStringTrackedDeviceProperty(deviceIndex, property, null, 0, ref error);
        if (required == 0 || error is not ETrackedPropertyError.TrackedProp_Success and not ETrackedPropertyError.TrackedProp_BufferTooSmall)
        {
            return null;
        }

        var buffer = new StringBuilder(checked((int)required));
        error = ETrackedPropertyError.TrackedProp_Success;
        _ = OpenVR.System.GetStringTrackedDeviceProperty(deviceIndex, property, buffer, required, ref error);
        return error == ETrackedPropertyError.TrackedProp_Success ? buffer.ToString() : null;
    }

    private static void EnsureOverlay(EVROverlayError error, string operation)
    {
        if (error != EVROverlayError.None)
        {
            throw new InvalidOperationException($"{operation}失败：{error}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Hide();
        foreach (var surface in _surfaces.Values)
        {
            surface.Dispose();
        }
        foreach (var controller in _controllerMeshes.Values)
        {
            controller.Dispose();
        }
        _surfaces.Clear();
        _controllerMeshes.Clear();
        _whiteTextureView.Dispose();
        _whiteTexture.Dispose();
        _fallbackControllerMesh.Dispose();
        _panelMesh.Dispose();
        _samplerState.Dispose();
        _rasterizerState.Dispose();
        _depthState.Dispose();
        _blendState.Dispose();
        _constantBuffer.Dispose();
        _inputLayout.Dispose();
        _pixelShader.Dispose();
        _vertexShader.Dispose();
        _rightTarget.Dispose();
        _leftTarget.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct SceneVertex(Vector3 Position, Vector3 Normal, Vector2 TextureCoordinate)
    {
        public static readonly uint Stride = checked((uint)Marshal.SizeOf<SceneVertex>());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DrawConstants
    {
        public Matrix4x4 World;
        public Matrix4x4 WorldViewProjection;
        public Vector4 Tint;
        public Vector4 Options;
    }

    private sealed class SurfaceTexture : IDisposable
    {
        private readonly D3D11OverlayDevice _sharedDevice;
        private readonly ID3D11Texture2D _texture;

        public SurfaceTexture(D3D11OverlayDevice sharedDevice, ID3D11Device device)
        {
            _sharedDevice = sharedDevice;
            _texture = sharedDevice.CreateTexture(
                OverlayRenderer.Width,
                OverlayRenderer.Height,
                BindFlags.ShaderResource);
            View = device.CreateShaderResourceView(_texture, null);
        }

        public ID3D11ShaderResourceView View { get; }

        public void Update(byte[] rgba) => _sharedDevice.Upload(_texture, rgba);

        public void Dispose()
        {
            View.Dispose();
            _texture.Dispose();
        }
    }

    private sealed class SceneMesh(
        ID3D11Buffer vertexBuffer,
        ID3D11Buffer indexBuffer,
        uint indexCount) : IDisposable
    {
        public ID3D11Buffer VertexBuffer { get; } = vertexBuffer;

        public ID3D11Buffer IndexBuffer { get; } = indexBuffer;

        public uint IndexCount { get; } = indexCount;

        public void Dispose()
        {
            IndexBuffer.Dispose();
            VertexBuffer.Dispose();
        }
    }

    private sealed class ControllerMesh(SceneMesh mesh, int textureId) : IDisposable
    {
        private bool _textureFailed;

        public SceneMesh Mesh { get; } = mesh;

        public int TextureId { get; } = textureId;

        public ID3D11ShaderResourceView? TextureView { get; private set; }

        public void TryLoadTexture(ID3D11Device device, AppLog log)
        {
            if (TextureView is not null || _textureFailed || TextureId < 0)
            {
                return;
            }

            var texturePointer = IntPtr.Zero;
            var error = OpenVR.RenderModels.LoadTextureD3D11_Async(
                TextureId,
                device.NativePointer,
                ref texturePointer);
            if (error == EVRRenderModelError.Loading)
            {
                return;
            }
            if (error != EVRRenderModelError.None || texturePointer == IntPtr.Zero)
            {
                _textureFailed = true;
                log.Warning($"[overlay-scene] 手柄纹理 {TextureId} 加载失败：{error}。");
                return;
            }

            var texture = new ID3D11Texture2D(texturePointer);
            try
            {
                TextureView = device.CreateShaderResourceView(texture, null);
            }
            finally
            {
                OpenVR.RenderModels.FreeTextureD3D11(texturePointer);
            }
        }

        public void Dispose()
        {
            TextureView?.Dispose();
            Mesh.Dispose();
        }
    }

    private sealed class ProjectionEyeTarget : IDisposable
    {
        private readonly ProjectionFrame[] _frames;
        private readonly ID3D11Texture2D _depthTexture;
        private int _nextFrame;

        public ProjectionEyeTarget(
            ID3D11Device device,
            ulong overlayHandle,
            EVREye eye,
            int width,
            int height)
        {
            OverlayHandle = overlayHandle;
            Eye = eye;
            Width = width;
            Height = height;
            _frames = Enumerable.Range(0, 2)
                .Select(_ => new ProjectionFrame(device, width, height))
                .ToArray();
            _depthTexture = device.CreateTexture2D(new Texture2DDescription(
                Format.D32_Float,
                checked((uint)width),
                checked((uint)height),
                1,
                1,
                BindFlags.DepthStencil,
                ResourceUsage.Default,
                CpuAccessFlags.None));
            DepthStencilView = device.CreateDepthStencilView(_depthTexture, null);
        }

        public ulong OverlayHandle { get; }

        public EVREye Eye { get; }

        public int Width { get; }

        public int Height { get; }

        public ID3D11DepthStencilView DepthStencilView { get; }

        public ProjectionFrame NextFrame()
        {
            var frame = _frames[_nextFrame];
            _nextFrame = (_nextFrame + 1) % _frames.Length;
            return frame;
        }

        public void Dispose()
        {
            DepthStencilView.Dispose();
            _depthTexture.Dispose();
            foreach (var frame in _frames)
            {
                frame.Dispose();
            }
        }
    }

    private sealed class ProjectionFrame : IDisposable
    {
        public ProjectionFrame(ID3D11Device device, int width, int height)
        {
            Texture = device.CreateTexture2D(new Texture2DDescription(
                Format.R8G8B8A8_UNorm,
                checked((uint)width),
                checked((uint)height),
                1,
                1,
                BindFlags.RenderTarget | BindFlags.ShaderResource,
                ResourceUsage.Default,
                CpuAccessFlags.None));
            RenderTargetView = device.CreateRenderTargetView(Texture, null);
        }

        public ID3D11Texture2D Texture { get; }

        public ID3D11RenderTargetView RenderTargetView { get; }

        public void Dispose()
        {
            RenderTargetView.Dispose();
            Texture.Dispose();
        }
    }
}
